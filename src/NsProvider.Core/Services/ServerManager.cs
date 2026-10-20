using System.Security.Cryptography;
using NsProvider.Core.Storage;

namespace NsProvider.Core.Services;

/// <summary>
/// Orquestra o ciclo de vida dos servidores: criar (do template), atualizar
/// (slots, hostname, rcon, gamemode), deletar, start/stop e console.
/// Layout: {Base}/servers/{userId}/{serverId}
/// </summary>
public sealed class ServerManager(
    Database db,
    UserRepository users,
    ServerRepository servers,
    PortAllocator ports,
    TemplateManager templates,
    ProcessManager processes)
{
    private readonly Database _db = db;
    private readonly UserRepository _users = users;
    private readonly ServerRepository _servers = servers;
    private readonly PortAllocator _ports = ports;
    private readonly TemplateManager _templates = templates;
    private readonly ProcessManager _processes = processes;

    public ProcessManager Processes => _processes;

    public GameServer Create(Guid userId, ServerType type, int maxPlayers,
        string? name, string? hostname, string? gamemode, bool announce, Guid? serverId = null,
        int? memoryMb = null, double? cpuCores = null, int? diskMb = null)
    {
        var user = _users.GetById(userId)
            ?? throw new InvalidOperationException("Usuário não encontrado.");

        if (maxPlayers is < 1 or > 1000)
            throw new InvalidOperationException("maxPlayers deve estar entre 1 e 1000.");
        ValidateResources(memoryMb, cpuCores, diskMb);

        if (!_templates.IsInstalled(type))
            throw new InvalidOperationException(
                $"Template '{TemplateManager.PackageName(type)}' não está instalado no motor.");

        // o painel web pode trazer o próprio UUID (mesmo ID em múltiplos motores);
        // sem ID, o motor gera
        if (serverId is { } requested && _servers.GetById(requested) is not null)
            throw new InvalidOperationException("Já existe um servidor com esse ID.");
        var id = serverId ?? Guid.NewGuid();
        var serverDir = AppPaths.ServerDir(userId, id);

        var gamemodes = ConfigGenerator.ListGamemodes(type, AppPaths.TemplateDir(type));
        var effectiveGamemode = string.IsNullOrWhiteSpace(gamemode) ? gamemodes.FirstOrDefault() : gamemode;
        if (effectiveGamemode is null)
            throw new InvalidOperationException("Template não possui nenhum gamemode (.amx).");

        // copia o template inteiro para a pasta do servidor
        CopyDirectory(AppPaths.TemplateDir(type), serverDir);

        var server = new GameServer
        {
            Id = id,
            UserId = userId,
            Type = type,
            Name = string.IsNullOrWhiteSpace(name) ? $"server-{serverId.ToString()[..8]}" : name.Trim(),
            Port = _ports.Allocate(),
            MaxPlayers = maxPlayers,
            Hostname = string.IsNullOrWhiteSpace(hostname) ? $"{user.Name}'s {TemplateManager.PackageName(type)} server" : hostname.Trim(),
            RconPassword = GenerateRandomPassword(),
            Gamemode = effectiveGamemode,
            Announce = announce,
            MemoryMb = memoryMb,
            CpuCores = cpuCores,
            DiskMb = diskMb,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        server = _servers.Create(server);
        ConfigGenerator.Write(server, serverDir);

        // arquivos pertencem ao UID do dono — o jogo (que roda com esse UID) precisa escrever
        var uid = _users.EnsureUid(userId);
        ChownRecursive(serverDir, uid, uid);
        return server;
    }

    public GameServer Update(Guid serverId, int? maxPlayers, string? hostname, string? name,
        string? rconPassword, string? gamemode, bool? announce,
        int? memoryMb = null, double? cpuCores = null, int? diskMb = null)
    {
        var server = _servers.GetById(serverId)
            ?? throw new InvalidOperationException("Servidor não encontrado.");

        if (maxPlayers is { } mp)
        {
            if (mp is < 1 or > 1000)
                throw new InvalidOperationException("maxPlayers deve estar entre 1 e 1000.");
            server.MaxPlayers = mp;
        }
        if (hostname is not null) server.Hostname = hostname.Trim();
        if (name is not null && name.Trim().Length > 0) server.Name = name.Trim();
        if (rconPassword is not null && rconPassword.Length > 0) server.RconPassword = rconPassword;
        if (announce is { } ann) server.Announce = ann;

        // recursos (cpu, memória, disco) — aplica ao vivo se estiver rodando
        bool recursosMudou = false;
        if (memoryMb is { } mem && mem != server.MemoryMb)
        {
            ValidateResources(mem, null, null);
            server.MemoryMb = mem;
            recursosMudou = true;
        }
        if (cpuCores is { } cpu && cpu != server.CpuCores)
        {
            ValidateResources(null, cpu, null);
            server.CpuCores = cpu;
            recursosMudou = true;
        }
        if (diskMb is { } disk && disk != server.DiskMb)
        {
            ValidateResources(null, null, disk);
            server.DiskMb = disk;
            recursosMudou = true;
        }

        if (gamemode is not null)
        {
            var available = ConfigGenerator.ListGamemodes(server.Type, AppPaths.ServerDir(server.UserId, serverId));
            if (!available.Contains(gamemode, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Gamemode '{gamemode}' não existe. Disponíveis: {string.Join(", ", available)}");
            server.Gamemode = available.First(g => g.Equals(gamemode, StringComparison.OrdinalIgnoreCase));
        }

        server.UpdatedAt = DateTime.UtcNow;
        _servers.Update(server);
        ConfigGenerator.Write(server, AppPaths.ServerDir(server.UserId, serverId));
        // config reescrita pelo motor (root) → devolve ao dono
        var uid = _users.EnsureUid(server.UserId);
        ChownRecursive(AppPaths.ServerDir(server.UserId, serverId), uid, uid);

        // recursos alterados com servidor ligado → cgroup ao vivo, sem restart
        if (recursosMudou && _processes.GetStatus(serverId) is { Running: true } status && status.Pid is { } pid)
        {
            var cgroups = new CgroupManager();
            cgroups.Attach(serverId, pid,
                server.MemoryMb ?? CgroupManager.MemoryMb,
                server.CpuCores ?? CgroupManager.CpuCores,
                CgroupManager.PidsMax);
            _processes.SendEventLine(serverId,
                $"[motor] recursos atualizados: mem {server.MemoryMb ?? CgroupManager.MemoryMb}MB · cpu {server.CpuCores ?? CgroupManager.CpuCores} core(s) · disco {server.DiskMb ?? DiskQuotaService.DiskMb}MB");
        }
        return server;
    }

    private static void ValidateResources(int? memoryMb, double? cpuCores, int? diskMb)
    {
        if (memoryMb is { } mem && mem is < 64 or > 262_144)
            throw new InvalidOperationException("memoryMb deve estar entre 64 e 262144 (256GB).");
        if (cpuCores is { } cpu && cpu is <= 0 or > 256)
            throw new InvalidOperationException("cpuCores deve estar entre 0.1 e 256.");
        if (diskMb is { } disk && disk is < 50 or > 10_485_760)
            throw new InvalidOperationException("diskMb deve estar entre 50 e 10485760 (10TB).");
    }

    public void Delete(Guid serverId)
    {
        var server = _servers.GetById(serverId)
            ?? throw new InvalidOperationException("Servidor não encontrado.");

        if (_processes.IsRunning(serverId))
            _processes.Stop(serverId);

        _servers.Delete(serverId);
        var dir = AppPaths.ServerDir(server.UserId, serverId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        // backups ficam fora da pasta do servidor — limpa também
        var backupDir = AppPaths.BackupDir(serverId);
        if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
    }

    public GameServer Start(Guid serverId)
    {
        var server = _servers.GetById(serverId)
            ?? throw new InvalidOperationException("Servidor não encontrado.");

        var serverDir = AppPaths.ServerDir(server.UserId, serverId);
        var bin = Path.Combine(serverDir, TemplateManager.BinaryName(server.Type));
        var uid = _users.EnsureUid(server.UserId);
        ChownRecursive(serverDir, uid, uid); // cobre pastas criadas antes da migração de UID
        _processes.Start(serverId, bin, serverDir,
            uid, uid,
            server.MemoryMb ?? CgroupManager.MemoryMb,
            server.CpuCores ?? CgroupManager.CpuCores,
            CgroupManager.PidsMax);
        return server;
    }

    public void Stop(Guid serverId) => _processes.Stop(serverId);

    /// <summary>
    /// Console do servidor: eventos do motor (ring buffer) + tail do arquivo de log.
    /// SA-MP escreve em server_log.txt; open.mp em log.txt (ambos não usam stdout).
    /// </summary>
    public (bool Running, IReadOnlyList<string> Lines) GetConsole(Guid serverId, int maxLines)
    {
        var server = _servers.GetById(serverId)
            ?? throw new InvalidOperationException("Servidor não encontrado.");

        var events = _processes.GetEvents(serverId, Math.Min(maxLines, 50));
        var stdout = _processes.GetConsole(serverId, Math.Min(maxLines, 50));
        var lines = new List<string>(events);
        lines.AddRange(stdout.Where(l => !lines.Contains(l)));

        var logName = server.Type == ServerType.Samp ? "server_log.txt" : "log.txt";
        var logPath = Path.Combine(AppPaths.ServerDir(server.UserId, serverId), logName);
        if (File.Exists(logPath))
        {
            try
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var all = new List<string>();
                while (sr.ReadLine() is { } line) all.Add(line);
                var take = Math.Max(0, maxLines - lines.Count);
                lines.AddRange(all.Skip(Math.Max(0, all.Count - take)));
            }
            catch (IOException) { /* log file locked/rotated — ignora */ }
        }

        return (_processes.IsRunning(serverId), lines);
    }

    /// <summary>Envia comando ao console. SA-MP/open.mp: via RCON UDP (stdin não é lido pelo samp03svr).</summary>
    public (bool sent, string? response) SendConsoleCommand(Guid serverId, string command)
    {
        var server = _servers.GetById(serverId)
            ?? throw new InvalidOperationException("Servidor não encontrado.");

        var rconResponse = RconClient.Send("127.0.0.1", server.Port, server.RconPassword, command);
        return (true, rconResponse);
    }

    public (GameServer Server, ServerProcessStatus Status) GetWithStatus(Guid serverId)
    {
        var server = _servers.GetById(serverId)
            ?? throw new InvalidOperationException("Servidor não encontrado.");
        return (server, _processes.GetStatus(serverId));
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
    }

    /// <summary>Chown recursivo (usado também pelo restore de backup).</summary>
    public static void ChownRecursivePublic(string dir, int uid, int gid) => ChownRecursive(dir, uid, gid);

    private static void ChownRecursive(string dir, int uid, int gid)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
                NativeMethods.Chown(entry, (uint)uid, (uint)gid);
            NativeMethods.Chown(dir, (uint)uid, (uint)gid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[motor] aviso: chown parcial em {dir}: {ex.Message}");
        }
    }

    private static string GenerateRandomPassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(12);
        return Convert.ToBase64String(bytes);
    }
}
