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
        "memory_mb AS MemoryMb, cpu_cores AS CpuCores, disk_mb AS DiskMb";

    public GameServer Create(GameServer s)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var portTaken = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM servers WHERE port = @Port", s, tx) > 0;
        if (portTaken)
            throw new InvalidOperationException($"A porta {s.Port} já está em uso.");

        conn.Execute("""
            INSERT INTO servers (id, user_id, type, name, port, max_players, hostname, rcon_password, gamemode, announce, created_at, updated_at, memory_mb, cpu_cores, disk_mb)
            VALUES (@sid, @userId, @Type, @Name, @Port, @MaxPlayers, @Hostname, @RconPassword, @Gamemode, @Announce, @CreatedAt, @UpdatedAt, @MemoryMb, @CpuCores, @DiskMb)
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
                updated_at = @UpdatedAt, memory_mb = @MemoryMb, cpu_cores = @CpuCores, disk_mb = @DiskMb
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
        });
    }

    public bool Delete(Guid id)
    {
        using var conn = Open();
        return conn.Execute("DELETE FROM servers WHERE id = @id", new { id = id.ToString() }) > 0;
    }

    public bool PortInUse(int port)
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM servers WHERE port = @port", new { port }) > 0;
    }
}
