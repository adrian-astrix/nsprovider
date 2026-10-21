using Microsoft.AspNetCore.Http.Json;
using NsProvider.Core;
using NsProvider.Core.Services;
using NsProvider.Core.Storage;

namespace NsProvider.Api;

public static class Endpoints
{
    public static void MapUsers(WebApplication app)
    {
        var users = app.Services.GetRequiredService<UserRepository>();

        // Usuário SEM senha: autenticação é coisa do painel web (estilo wings/Pterodactyl).
        // O painel pode passar o próprio UUID (mesmo usuário em múltiplos motores).
        app.MapPost("/api/users", (CreateUserRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Nome obrigatório." });
            if (req.Id is { } id && id == Guid.Empty)
                return Results.BadRequest(new { error = "ID inválido." });
            try
            {
                var user = users.Create(req.Name, req.Id);
                return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Name, user.CreatedAt });
            }
            catch (Exception ex) when (ex.Message.Contains("UNIQUE"))
            {
                return Results.Conflict(new { error = "Nome (ou ID) de usuário já existe." });
            }
        });

        app.MapGet("/api/users", () =>
            Results.Ok(users.List().Select(u => new { u.Id, u.Name, u.CreatedAt })));

        app.MapGet("/api/users/{id:guid}", (Guid id) =>
        {
            var u = users.GetById(id);
            return u is null
                ? Results.NotFound(new { error = "Usuário não encontrado." })
                : Results.Ok(new { u.Id, u.Name, u.CreatedAt });
        });

        app.MapDelete("/api/users/{id:guid}", (Guid id) =>
            users.Delete(id) ? Results.Ok(new { deleted = true }) : Results.NotFound());
    }

    public static void MapServers(WebApplication app)
    {
        var manager = app.Services.GetRequiredService<ServerManager>();
        var servers = app.Services.GetRequiredService<ServerRepository>();
        var users = app.Services.GetRequiredService<UserRepository>();

        app.MapPost("/api/users/{userId:guid}/servers", (Guid userId, CreateServerRequest req) =>
        {
            try
            {
                var type = ParseType(req.Type);
                var (mem, cpu, disk) = req.Recursos?.Deconstruct() ?? (null, null, null);
                var server = manager.Create(userId, type, req.MaxPlayers, req.Name, req.Hostname,
                    req.Gamemode, req.Announce, req.Id, mem, cpu, disk, req.Ip, req.Port);
                return Results.Created($"/api/servers/{server.Id}", ServerView(server, manager, includeRcon: true));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/users/{userId:guid}/servers", (Guid userId) =>
        {
            if (users.GetById(userId) is null)
                return Results.NotFound(new { error = "Usuário não encontrado." });
            return Results.Ok(servers.ListByUser(userId).Select(s => ServerView(s, manager)));
        });

        app.MapGet("/api/servers", () =>
            Results.Ok(servers.ListAll().Select(s => ServerView(s, manager))));

        app.MapGet("/api/servers/{id:guid}", (Guid id) =>
        {
            var s = servers.GetById(id);
            if (s is null) return Results.NotFound(new { error = "Servidor não encontrado." });
            var disk = app.Services.GetRequiredService<DiskQuotaService>();
            return Results.Ok(ServerViewDetailed(s, manager, disk));
        });
        app.MapPatch("/api/servers/{id:guid}", (Guid id, UpdateServerRequest req) =>
        {
            try
            {
                var (mem, cpu, disk) = req.Recursos?.Deconstruct() ?? (null, null, null);
                var server = manager.Update(id, req.MaxPlayers, req.Hostname, req.Name,
                    req.RconPassword, req.Gamemode, req.Announce, mem, cpu, disk, req.Ip, req.Port);
                return Results.Ok(ServerView(server, manager, includeRcon: true));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/servers/{id:guid}", (Guid id) =>
        {
            try
            {
                manager.Delete(id);
                return Results.Ok(new { deleted = true });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/servers/{id:guid}/start", (Guid id) =>
        {
            try
            {
                manager.Start(id);
                var (server, status) = manager.GetWithStatus(id);
                return Results.Ok(ServerViewDetailed(server, manager));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/servers/{id:guid}/stop", (Guid id) =>
        {
            manager.Stop(id);
            return Results.Ok(new { stopped = true });
        });

        app.MapPost("/api/servers/{id:guid}/restart", (Guid id) =>
        {
            try
            {
                manager.Stop(id);
                manager.Start(id);
                var (server, status) = manager.GetWithStatus(id);
                return Results.Ok(ServerViewDetailed(server, manager));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // ---- console ----
        app.MapGet("/api/servers/{id:guid}/console", (Guid id, int? lines) =>
        {
            try
            {
                var (running, log) = manager.GetConsole(id, Math.Clamp(lines ?? 100, 1, 500));
                return Results.Ok(new { running, lines = log });
            }
            catch (InvalidOperationException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        app.MapPost("/api/servers/{id:guid}/console", (Guid id, ConsoleCommandRequest req) =>
        {
            try
            {
                var (sent, response) = manager.SendConsoleCommand(id, req.Command ?? "");
                return Results.Ok(new { sent, response });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    public static void MapFiles(WebApplication app)
    {
        var servers = app.Services.GetRequiredService<ServerRepository>();

        // Resolve o caminho físico garantido dentro da pasta do servidor (anti path traversal)
        static (string physical, Guid userId) ResolvePath(ServerRepository servers, Guid serverId, string? virtualPath)
        {
            var server = servers.GetById(serverId)
                ?? throw new InvalidOperationException("Servidor não encontrado.");
            var root = AppPaths.ServerDir(server.UserId, serverId);
            var rel = (virtualPath ?? "/").Replace('\\', '/').TrimStart('/');
            var physical = Path.GetFullPath(Path.Combine(root, rel));
            if (!physical.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar)
                && physical != Path.GetFullPath(root))
                throw new UnauthorizedAccessException("Caminho fora da pasta do servidor.");
            return (physical, server.UserId);
        }

        app.MapGet("/api/servers/{id:guid}/files", (Guid id, string? path) =>
        {
            try
            {
                var (dir, _) = ResolvePath(servers, id, path);
                if (!Directory.Exists(dir))
                    return Results.NotFound(new { error = "Pasta não existe." });
                var items = Directory.EnumerateFileSystemEntries(dir).Select(p =>
                {
                    var fi = new FileInfo(p);
                    var isDir = fi.Attributes.HasFlag(FileAttributes.Directory);
                    return new
                    {
                        name = Path.GetFileName(p),
                        isDirectory = isDir,
                        size = isDir ? (long?)null : fi.Length,
                        modifiedAt = fi.LastWriteTimeUtc,
                    };
                }).OrderByDescending(i => i.isDirectory).ThenBy(i => i.name);
                return Results.Ok(new { path = path ?? "/", items });
            }
            catch (UnauthorizedAccessException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/servers/{id:guid}/files/mkdir", (Guid id, MkdirRequest req) =>
        {
            try
            {
                var (dir, _) = ResolvePath(servers, id, req.Path);
                Directory.CreateDirectory(dir);
                return Results.Created($"/api/servers/{id}/files?path={req.Path}", new { created = req.Path });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/servers/{id:guid}/files", (Guid id, string path) =>
        {
            try
            {
                var (target, _) = ResolvePath(servers, id, path);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                else if (File.Exists(target)) File.Delete(target);
                else return Results.NotFound(new { error = "Arquivo/pasta não existe." });
                return Results.Ok(new { deleted = path });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/servers/{id:guid}/files/rename", (Guid id, RenameRequest req) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.From) || string.IsNullOrWhiteSpace(req.To))
                    return Results.BadRequest(new { error = "'from' e 'to' obrigatórios." });
                var (from, _) = ResolvePath(servers, id, req.From);
                var (to, _) = ResolvePath(servers, id, req.To);
                if (!File.Exists(from) && !Directory.Exists(from))
                    return Results.NotFound(new { error = "Arquivo/pasta de origem não existe." });
                if (File.Exists(to) || Directory.Exists(to))
                    return Results.BadRequest(new { error = "Destino já existe." });
                if (Directory.Exists(from)) Directory.Move(from, to);
                else File.Move(from, to);
                return Results.Ok(new { renamed = req.From, to = req.To });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/servers/{id:guid}/files/download", (Guid id, string path) =>
        {
            try
            {
                var (file, _) = ResolvePath(servers, id, path);
                if (!File.Exists(file))
                    return Results.NotFound(new { error = "Arquivo não existe." });
                return Results.File(file, "application/octet-stream", Path.GetFileName(file));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/servers/{id:guid}/files/upload", async (Guid id, string path, HttpRequest request) =>
        {
            try
            {
                var (file, _) = ResolvePath(servers, id, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await using var fs = File.Create(file);
                await request.Body.CopyToAsync(fs);
                return Results.Created($"/api/servers/{id}/files?path={path}", new { uploaded = path });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    public static void MapTemplates(WebApplication app)
    {
        var templates = app.Services.GetRequiredService<TemplateManager>();

        app.MapGet("/api/templates", () => Results.Ok(templates.ListInstalled()));

        // Instala template via upload: POST /api/templates/samp (body = .zip/.tar.gz)
        // Formato detectado por magic bytes (PK=zip, 1F 8B=gzip) — extensão não importa.
        app.MapPost("/api/templates/{type}", async (string type, HttpRequest request) =>
        {
            if (!TryParseType(type, out var serverType))
                return Results.BadRequest(new { error = "Tipo inválido. Use 'samp' ou 'openmp'." });
            if (request.ContentLength is null or 0)
                return Results.BadRequest(new { error = "Envie o pacote (.zip ou .tar.gz) no body." });

            var tempFile = Path.GetTempFileName();
            try
            {
                await using (var fs = File.Create(tempFile))
                {
                    await request.Body.CopyToAsync(fs);
                }

                await using var pkg = File.OpenRead(tempFile);
                var header = new byte[2];
                await pkg.ReadExactlyAsync(header);
                pkg.Seek(0, SeekOrigin.Begin);

                var fileName = header[0] == 'P' && header[1] == 'K' ? "pkg.zip"
                    : header[0] == 0x1F && header[1] == 0x8B ? "pkg.tar.gz"
                    : "";

                if (fileName == "")
                    return Results.BadRequest(new { error = "Formato não reconhecido: envie .zip ou .tar.gz." });

                templates.Install(serverType, pkg, fileName);
                return Results.Ok(new { installed = type });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                File.Delete(tempFile);
            }
        });
    }

    public static void MapNetwork(WebApplication app)
    {
        var network = app.Services.GetRequiredService<NetworkService>();

        // IPs da máquina + uso: qual IP tem quais servidores/portas
        app.MapGet("/api/network", () => Results.Ok(network.List()));
    }

    public static void MapStats(WebApplication app)
    {
        var stats = app.Services.GetRequiredService<SystemStatsService>();
        app.MapGet("/api/stats", () => Results.Ok(stats.GetSnapshot()));
    }

    public static void MapBackups(WebApplication app)
    {
        var backupService = app.Services.GetRequiredService<BackupService>();

        app.MapPost("/api/servers/{id:guid}/backups", (Guid id) =>
        {
            try
            {
                var backup = backupService.Start(id); // assíncrono: status "pending"
                return Results.Accepted($"/api/servers/{id}/backups/{backup.Id}", BackupView(backup));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/servers/{id:guid}/backups", (Guid id) =>
            Results.Ok(backupService.List(id).Select(BackupView)));

        app.MapGet("/api/servers/{id:guid}/backups/{backupId:guid}", (Guid id, Guid backupId) =>
        {
            try { return Results.Ok(BackupView(backupService.Get(id, backupId))); }
            catch (InvalidOperationException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        app.MapGet("/api/servers/{id:guid}/backups/{backupId:guid}/download", (Guid id, Guid backupId) =>
        {
            try
            {
                var path = backupService.GetFilePath(id, backupId);
                return Results.File(path, "application/gzip", backupId + ".tar.gz");
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/servers/{id:guid}/backups/{backupId:guid}/restore", (Guid id, Guid backupId) =>
        {
            try
            {
                backupService.Restore(id, backupId);
                return Results.Ok(new { restored = backupId });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (UnauthorizedAccessException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/servers/{id:guid}/backups/{backupId:guid}", (Guid id, Guid backupId) =>
        {
            try
            {
                backupService.Delete(id, backupId);
                return Results.Ok(new { deleted = backupId });
            }
            catch (InvalidOperationException ex) { return Results.NotFound(new { error = ex.Message }); }
        });
    }

    private static object BackupView(Backup b) => new
    {
        b.Id,
        b.ServerId,
        b.Status,
        sizeBytes = b.SizeBytes,
        sizeMb = b.SizeBytes is { } s ? s / 1024 / 1024 : (long?)null,
        b.CreatedAt,
        b.FinishedAt,
        b.Error,
    };

    // ---- helpers ----

    public static bool TryParseType(string value, out ServerType type)
    {
        type = value.Trim().ToLowerInvariant() switch
        {
            "samp" or "sa-mp" or "0.3.7" => ServerType.Samp,
            "openmp" or "omp" or "open.mp" => ServerType.OpenMp,
            _ => ServerType.Samp,
        };
        return value.Trim().ToLowerInvariant() is "samp" or "sa-mp" or "0.3.7"
            or "openmp" or "omp" or "open.mp";
    }

    private static ServerType ParseType(string value) =>
        TryParseType(value, out var t) ? t : throw new InvalidOperationException("Tipo inválido: use 'samp' ou 'openmp'.");

    private static object ServerView(GameServer s, ServerManager manager, bool includeRcon = false) => new
    {
        s.Id,
        s.UserId,
        type = TemplateManager.PackageName(s.Type),
        s.Name,
        s.Port,
        s.MaxPlayers,
        s.Hostname,
        rconPassword = includeRcon ? s.RconPassword : null,
        s.Gamemode,
        s.Announce,
        s.CreatedAt,
        s.UpdatedAt,
        ip = string.IsNullOrWhiteSpace(s.BindIp) ? "0.0.0.0" : s.BindIp,
        recursos = new
        {
            diskQuotaMb = s.DiskMb ?? DiskQuotaService.DiskMb,
            memoryLimitMb = s.MemoryMb ?? CgroupManager.MemoryMb,
            cpuCoresLimit = s.CpuCores ?? CgroupManager.CpuCores,
        },
    };

    private static object ServerViewDetailed(GameServer s, ServerManager manager, DiskQuotaService? disk = null) => new
    {
        s.Id,
        s.UserId,
        type = TemplateManager.PackageName(s.Type),
        s.Name,
        s.Port,
        s.MaxPlayers,
        s.Hostname,
        s.RconPassword,
        s.Gamemode,
        s.Announce,
        s.CreatedAt,
        s.UpdatedAt,
        ip = string.IsNullOrWhiteSpace(s.BindIp) ? "0.0.0.0" : s.BindIp,
        status = manager.Processes.GetStatus(s.Id),
        recursos = new
        {
            diskUsageMb = disk is null ? (long?)null : disk.GetUsageBytes(s.Id) / 1024 / 1024,
            diskQuotaMb = s.DiskMb ?? DiskQuotaService.DiskMb,
            memoryLimitMb = s.MemoryMb ?? CgroupManager.MemoryMb,
            cpuCoresLimit = s.CpuCores ?? CgroupManager.CpuCores,
            pidsLimit = CgroupManager.PidsMax,
        },
    };
}

public sealed record CreateUserRequest(string Name, Guid? Id);
public sealed record RecursosRequest(int? MemoryMb, double? CpuCores, int? DiskMb)
{
    public (int?, double?, int?) Deconstruct() => (MemoryMb, CpuCores, DiskMb);
}
public sealed record CreateServerRequest(string Type, int MaxPlayers, string? Name, string? Hostname, string? Gamemode, bool Announce = false, Guid? Id = null, RecursosRequest? Recursos = null, string? Ip = null, int? Port = null);
public sealed record UpdateServerRequest(int? MaxPlayers, string? Hostname, string? Name, string? RconPassword, string? Gamemode, bool? Announce, RecursosRequest? Recursos = null, string? Ip = null, int? Port = null);
public sealed record ConsoleCommandRequest(string? Command);
public sealed record MkdirRequest(string? Path);
public sealed record RenameRequest(string? From, string? To);
