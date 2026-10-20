namespace NsProvider.Core;

/// <summary>
/// Layout de pastas do motor:
///   {Base}/servers/{userId}/{serverId}   → cada servidor (UUIDs)
///   {Base}/templates/{samp|openmp}       → pacotes base instalados
///   {Base}/data                          → motor.db + host keys do SFTP
/// Base configurável via env NSP_BASE (default /home/nsprovider).
/// </summary>
public static class AppPaths
{
    public static string Base =>
        Environment.GetEnvironmentVariable("NSP_BASE") ?? "/home/nsprovider";

    public static string ServersDir => Path.Combine(Base, "servers");
    public static string TemplatesDir => Path.Combine(Base, "templates");
    public static string BackupsDir => Path.Combine(Base, "backups");
    public static string DataDir => Path.Combine(Base, "data");
    public static string DatabaseFile => Path.Combine(DataDir, "motor.db");
    public static string KeysDir => Path.Combine(DataDir, "keys");

    public static string UserDir(Guid userId) => Path.Combine(ServersDir, userId.ToString());

    public static string ServerDir(Guid userId, Guid serverId) =>
        Path.Combine(ServersDir, userId.ToString(), serverId.ToString());

    public static string BackupDir(Guid serverId) => Path.Combine(BackupsDir, serverId.ToString());

    public static string TemplateDir(ServerType type) =>
        Path.Combine(TemplatesDir, type == ServerType.Samp ? "samp" : "openmp");

    public static void EnsureAll()
    {
        Directory.CreateDirectory(ServersDir);
        Directory.CreateDirectory(TemplatesDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(KeysDir);
    }
}
