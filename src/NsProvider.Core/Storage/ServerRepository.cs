using Dapper;
using Microsoft.Data.Sqlite;

namespace NsProvider.Core.Storage;

public sealed class ServerRepository
{
    private readonly string _connectionString;

    public ServerRepository(Database db)
    {
        _connectionString = db.ConnectionString;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private const string Columns =
        "id AS Id, user_id AS UserId, type AS Type, name AS Name, port AS Port, " +
        "max_players AS MaxPlayers, hostname AS Hostname, rcon_password AS RconPassword, " +
        "gamemode AS Gamemode, announce AS Announce, created_at AS CreatedAt, updated_at AS UpdatedAt, " +
        "memory_mb AS MemoryMb, cpu_cores AS CpuCores, disk_mb AS DiskMb, bind_ip AS BindIp";

    public GameServer Create(GameServer s)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var portTaken = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM servers WHERE port = @Port AND COALESCE(bind_ip, '') = COALESCE(@BindIp, '')",
            new { s.Port, BindIp = NormalizeIp(s.BindIp) }, tx) > 0;
        if (portTaken)
            throw new InvalidOperationException(
                $"A porta {s.Port} já está em uso no IP {(string.IsNullOrEmpty(s.BindIp) ? "compartilhado (0.0.0.0)" : s.BindIp)}.");

        conn.Execute("""
            INSERT INTO servers (id, user_id, type, name, port, max_players, hostname, rcon_password, gamemode, announce, created_at, updated_at, memory_mb, cpu_cores, disk_mb, bind_ip)
            VALUES (@sid, @userId, @Type, @Name, @Port, @MaxPlayers, @Hostname, @RconPassword, @Gamemode, @Announce, @CreatedAt, @UpdatedAt, @MemoryMb, @CpuCores, @DiskMb, @BindIp)
            """, new
        {
            sid = s.Id.ToString(),
            userId = s.UserId.ToString(),
            Type = (int)s.Type,
            s.Name,
            s.Port,
            s.MaxPlayers,
            s.Hostname,
            s.RconPassword,
            s.Gamemode,
            Announce = s.Announce ? 1 : 0,
            s.CreatedAt,
            s.UpdatedAt,
            s.MemoryMb,
            s.CpuCores,
            s.DiskMb,
            BindIp = NormalizeIp(s.BindIp),
        }, tx);
        tx.Commit();
        return s;
    }

    public GameServer? GetById(Guid id)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<GameServer>(
            $"SELECT {Columns} FROM servers WHERE id = @id", new { id = id.ToString() });
    }

    public IReadOnlyList<GameServer> ListByUser(Guid userId)
    {
        using var conn = Open();
        return conn.Query<GameServer>(
            $"SELECT {Columns} FROM servers WHERE user_id = @userId ORDER BY created_at",
            new { userId = userId.ToString() }).ToList();
    }

    public IReadOnlyList<GameServer> ListAll()
    {
        using var conn = Open();
        return conn.Query<GameServer>($"SELECT {Columns} FROM servers ORDER BY created_at").ToList();
    }

    public void Update(GameServer s)
    {
        using var conn = Open();
        conn.Execute("""
            UPDATE servers SET
                name = @Name, max_players = @MaxPlayers, hostname = @Hostname,
                rcon_password = @RconPassword, gamemode = @Gamemode, announce = @Announce,
                updated_at = @UpdatedAt, memory_mb = @MemoryMb, cpu_cores = @CpuCores, disk_mb = @DiskMb,
                port = @Port, bind_ip = @BindIp
            WHERE id = @sid
            """, new
        {
            sid = s.Id.ToString(),
            s.Name,
            s.MaxPlayers,
            s.Hostname,
            s.RconPassword,
            s.Gamemode,
            Announce = s.Announce ? 1 : 0,
            s.UpdatedAt,
            s.MemoryMb,
            s.CpuCores,
            s.DiskMb,
            s.Port,
            BindIp = NormalizeIp(s.BindIp),
        });
    }

    public bool Delete(Guid id)
    {
        using var conn = Open();
        return conn.Execute("DELETE FROM servers WHERE id = @id", new { id = id.ToString() }) > 0;
    }

    public bool PortInUse(string? bindIp, int port)
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM servers WHERE port = @port AND COALESCE(bind_ip, '') = @ip",
            new { port, ip = NormalizeIp(bindIp) }) > 0;
    }

    /// <summary>null / "0.0.0.0" / vazio → compartilhado ('').</summary>
    public static string NormalizeIp(string? ip) =>
        string.IsNullOrWhiteSpace(ip) || ip.Trim() == "0.0.0.0" ? "" : ip.Trim();
}
