using System.Formats.Tar;
using System.IO.Compression;
using NsProvider.Core.Storage;

namespace NsProvider.Core.Services;

/// <summary>
/// Backups por servidor: {Base}/backups/{serverId}/{backupId}.tar.gz
///  - criação assíncrona (POST devolve 202 com o backup em status "pending")
///  - limites via env: NSP_BACKUP_MAX_MB (tamanho do servidor) e NSP_BACKUP_MAX_COUNT (por servidor)
///    (no futuro o painel web define os limites por usuário)
///  - download, delete e restore (para o servidor, limpa a pasta e extrai)
/// </summary>
public sealed class BackupService(
    ServerRepository servers,
    BackupRepository backups,
    ServerManager manager,
    UserRepository users)
{
    public static int MaxMb => int.TryParse(Environment.GetEnvironmentVariable("NSP_BACKUP_MAX_MB"), out var v) && v > 0 ? v : 2048;
    public static int MaxCount => int.TryParse(Environment.GetEnvironmentVariable("NSP_BACKUP_MAX_COUNT"), out var v) && v > 0 ? v : 5;

    public Backup Start(Guid serverId)
    {
        var server = servers.GetById(serverId)
            ?? throw new InvalidOperationException("Servidor não encontrado.");
        var dir = AppPaths.ServerDir(server.UserId, serverId);
        if (!Directory.Exists(dir))
            throw new InvalidOperationException("Pasta do servidor não existe.");

        if (backups.CountByServer(serverId) >= MaxCount)
            throw new InvalidOperationException(
                $"Limite de {MaxCount} backups por servidor atingido. Delete um antes de criar outro.");

        var backup = new Backup
        {
            Id = Guid.NewGuid(),
            ServerId = serverId,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
        };
        backups.Create(backup);

        _ = Task.Run(() => RunBackup(backup, serverId, dir));
        return backup;
    }

    private void RunBackup(Backup backup, Guid serverId, string dir)
    {
        var tmp = Path.Combine(AppPaths.BackupDir(serverId), backup.Id + ".tar.gz.part");
        try
        {
            Directory.CreateDirectory(AppPaths.BackupDir(serverId));
            long total = 0;
            using (var fileOut = File.Create(tmp))
            using (var gzip = new GZipStream(fileOut, CompressionLevel.Fastest))
            using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                // pastas (garante estrutura, inclusive vazias)...
                foreach (var folder in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory,
                        Path.GetRelativePath(dir, folder).Replace('\\', '/')));

                // ...depois arquivos, contando bytes e abortando acima da quota
                foreach (var filePath in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(filePath);
                    total += fi.Length;
                    if (total > (long)MaxMb * 1024 * 1024)
                        throw new InvalidOperationException(
                            $"Conteúdo do servidor ({total / 1024 / 1024}MB) excede o limite de backup ({MaxMb}MB).");

                    using var fs = File.OpenRead(filePath);
                    var entry = new PaxTarEntry(TarEntryType.RegularFile,
                        Path.GetRelativePath(dir, filePath).Replace('\\', '/'))
                    { DataStream = fs };
                    writer.WriteEntry(entry);
                }
            }

            var final = Path.Combine(AppPaths.BackupDir(serverId), backup.Id + ".tar.gz");
            File.Move(tmp, final);

            backup.Status = "ready";
            backup.SizeBytes = new FileInfo(final).Length;
            backup.FinishedAt = DateTime.UtcNow;
            backups.Update(backup);
            Console.WriteLine($"[motor] backup {backup.Id} pronto ({backup.SizeBytes / 1024 / 1024}MB)");
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            backup.Status = "failed";
            backup.Error = ex.Message;
            backup.FinishedAt = DateTime.UtcNow;
            backups.Update(backup);
            Console.WriteLine($"[motor] backup {backup.Id} falhou: {ex.Message}");
        }
    }

    public IReadOnlyList<Backup> List(Guid serverId) => backups.ListByServer(serverId);

    public Backup Get(Guid serverId, Guid backupId) =>
        backups.GetById(serverId, backupId)
        ?? throw new InvalidOperationException("Backup não encontrado.");

    /// <summary>Caminho físico do .tar.gz (só backups prontos).</summary>
    public string GetFilePath(Guid serverId, Guid backupId)
    {
        var backup = Get(serverId, backupId);
        if (backup.Status != "ready")
            throw new InvalidOperationException($"Backup ainda não está pronto (status: {backup.Status}).");
        var path = Path.Combine(AppPaths.BackupDir(serverId), backupId + ".tar.gz");
        return File.Exists(path) ? path : throw new InvalidOperationException("Arquivo de backup sumiu do disco.");
    }

    public void Delete(Guid serverId, Guid backupId)
    {
        Get(serverId, backupId); // 404 se não existe
        TryDelete(Path.Combine(AppPaths.BackupDir(serverId), backupId + ".tar.gz"));
        TryDelete(Path.Combine(AppPaths.BackupDir(serverId), backupId + ".tar.gz.part"));
        backups.Delete(serverId, backupId);
    }

    /// <summary>
    /// Restaura: para o servidor, limpa a pasta e extrai o backup. Bloqueante.
    /// </summary>
    public void Restore(Guid serverId, Guid backupId)
    {
        var server = servers.GetById(serverId)
            ?? throw new InvalidOperationException("Servidor não encontrado.");
        var path = GetFilePath(serverId, backupId);
        var serverDir = AppPaths.ServerDir(server.UserId, serverId);

        if (manager.Processes.IsRunning(serverId))
            manager.Stop(serverId);

        foreach (var entry in Directory.EnumerateFileSystemEntries(serverDir))
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }

        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new TarReader(gz);
        while (reader.GetNextEntry() is { } entry)
        {
            var name = entry.Name.Replace('\\', '/');
            if (Path.IsPathRooted(name) || name.Split('/').Contains(".."))
                throw new UnauthorizedAccessException($"Entrada suspeita no backup: {name}");

            var target = Path.GetFullPath(Path.Combine(serverDir, name));
            if (target != serverDir && !target.StartsWith(serverDir + Path.DirectorySeparatorChar))
                throw new UnauthorizedAccessException($"Entrada fora da pasta do servidor: {name}");

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
            }
            else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.HardLink or TarEntryType.SymbolicLink)
            {
                if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var outFs = File.Create(target);
                entry.DataStream.CopyTo(outFs);
            }
        }

        // devolve a posse pro UID do dono (o tar guarda root)
        var uid = users.EnsureUid(server.UserId);
        ServerManager.ChownRecursivePublic(serverDir, uid, uid);
        Console.WriteLine($"[motor] backup {backupId} restaurado no servidor {serverId}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}
