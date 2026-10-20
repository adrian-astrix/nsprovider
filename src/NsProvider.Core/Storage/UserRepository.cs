using Dapper;
using Microsoft.Data.Sqlite;

namespace NsProvider.Core.Storage;

public sealed class UserRepository
{
    private readonly string _connectionString;

    public UserRepository(Database db)
    {
        _connectionString = db.ConnectionString;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Cria usuário SEM senha (o painel web é a fonte de autenticação — igual wings/Pterodactyl).
    /// O painel pode passar o próprio ID (mesmo usuário em múltiplos motores).
    /// </summary>
    public User Create(string name, Guid? id = null)
    {
        var user = new User
        {
            Id = id ?? Guid.NewGuid(),
            Name = name.Trim(),
            CreatedAt = DateTime.UtcNow,
        };
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        user.Uid = conn.ExecuteScalar<int?>(
            "SELECT COALESCE(MAX(uid), 9999) + 1 FROM users", param: null, tx) ?? 10000;
        conn.Execute(
            "INSERT INTO users (id, name, password_hash, created_at, uid) VALUES (@id, @Name, '', @CreatedAt, @Uid)",
            new { id = user.Id.ToString(), user.Name, user.CreatedAt, user.Uid }, tx);
        tx.Commit();
        return user;
    }

    /// <summary>Garante que o usuário tem UID alocado (usuários criados antes da migração).</summary>
    public int EnsureUid(Guid id)
    {
        var user = GetById(id) ?? throw new InvalidOperationException("Usuário não encontrado.");
        if (user.Uid is int uid) return uid;
        using var conn = Open();
        uid = conn.ExecuteScalar<int?>("SELECT COALESCE(MAX(uid), 9999) + 1 FROM users") ?? 10000;
        conn.Execute("UPDATE users SET uid = @uid WHERE id = @id", new { uid, id = id.ToString() });
        return uid;
    }

    public int? GetUid(Guid id)
    {
        using var conn = Open();
        return conn.ExecuteScalar<int?>("SELECT uid FROM users WHERE id = @id", new { id = id.ToString() });
    }

    public User? GetByName(string name)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<User>(
            "SELECT id AS Id, name AS Name, created_at AS CreatedAt, uid AS Uid FROM users WHERE name = @name",
            new { name });
    }

    public User? GetById(Guid id)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<User>(
            "SELECT id AS Id, name AS Name, created_at AS CreatedAt, uid AS Uid FROM users WHERE id = @id",
            new { id = id.ToString() });
    }

    public IReadOnlyList<User> List()
    {
        using var conn = Open();
        return conn.Query<User>(
            "SELECT id AS Id, name AS Name, created_at AS CreatedAt, uid AS Uid FROM users ORDER BY created_at").ToList();
    }

    public bool Delete(Guid id)
    {
        using var conn = Open();
        return conn.Execute("DELETE FROM users WHERE id = @id", new { id = id.ToString() }) > 0;
    }
}
