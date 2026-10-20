using Dapper;
using Microsoft.Data.Sqlite;

namespace NsProvider.Core.Storage;

public sealed class BackupRepository
{
    private readonly string _connectionString;

    public BackupRepository(Database db) => _connectionString = db.ConnectionString;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private const string Select = """
        SELECT id AS Id, server_id AS ServerId, status AS Status, size_bytes AS SizeBytes,
               created_at AS CreatedAt, finished_at AS FinishedAt, error AS Error
        FROM backups
        """;

    public Backup Create(Backup backup)
    {
        using var conn = Open();
        conn.Execute(
            "INSERT INTO backups (id, server_id, status, size_bytes, created_at, finished_at, error) " +
            "VALUES (@id, @serverId, @Status, @SizeBytes, @CreatedAt, @FinishedAt, @Error)",
            new
            {
                id = backup.Id.ToString(),
                serverId = backup.ServerId.ToString(), // Guid como string (SQLite compara TEXT case-sensitive)
                backup.Status,
                backup.SizeBytes,
                backup.CreatedAt,
                backup.FinishedAt,
                backup.Error,
            });
        return backup;
    }

    public Backup? GetById(Guid serverId, Guid backupId)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<Backup>(
            $"{Select} WHERE id = @id AND server_id = @serverId",
            new { id = backupId.ToString(), serverId = serverId.ToString() });
    }

    public IReadOnlyList<Backup> ListByServer(Guid serverId)
    {
        using var conn = Open();
        return conn.Query<Backup>(
            $"{Select} WHERE server_id = @serverId ORDER BY created_at DESC",
            new { serverId = serverId.ToString() }).ToList();
    }

    public int CountByServer(Guid serverId)
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM backups WHERE server_id = @serverId",
            new { serverId = serverId.ToString() });
    }

    public void Update(Backup backup)
    {
        using var conn = Open();
        conn.Execute(
            "UPDATE backups SET status = @Status, size_bytes = @SizeBytes, finished_at = @FinishedAt, error = @Error WHERE id = @id",
            new { backup.Status, backup.SizeBytes, backup.FinishedAt, backup.Error, id = backup.Id.ToString() });
    }

    public bool Delete(Guid serverId, Guid backupId)
    {
        using var conn = Open();
        return conn.Execute(
            "DELETE FROM backups WHERE id = @id AND server_id = @serverId",
            new { id = backupId.ToString(), serverId = serverId.ToString() }) > 0;
    }
}
