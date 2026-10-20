using System.Data;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;

namespace NsProvider.Core.Storage;

public sealed class Database
{
    static Database()
    {
        SqlMapper.AddTypeHandler(new GuidStringHandler());
    }

    private sealed class GuidStringHandler : SqlMapper.TypeHandler<Guid>
    {
        public override Guid Parse(object value) =>
            value switch
            {
                Guid g => g,
                string s => Guid.Parse(s),
                byte[] b => new Guid(b),
                _ => throw new InvalidCastException($"Não sei converter {value?.GetType()} para Guid."),
            };

        public override void SetValue(IDbDataParameter parameter, Guid value) =>
            parameter.Value = value.ToString();
    }

    private readonly string _connectionString;

    public Database()
    {
        AppPaths.EnsureAll();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        Init();
    }

    public string ConnectionString => _connectionString;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Init()
    {
        using var conn = Open();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS users (
                id            TEXT PRIMARY KEY,
                name          TEXT NOT NULL UNIQUE COLLATE NOCASE,
                password_hash TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                uid           INTEGER
            );
            CREATE TABLE IF NOT EXISTS servers (
                id            TEXT PRIMARY KEY,
                user_id       TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                type          INTEGER NOT NULL,
                name          TEXT NOT NULL,
                port          INTEGER NOT NULL,
                max_players   INTEGER NOT NULL,
                hostname      TEXT NOT NULL,
                rcon_password TEXT NOT NULL,
                gamemode      TEXT NOT NULL,
                announce      INTEGER NOT NULL DEFAULT 0,
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_servers_port ON servers(port);
            CREATE INDEX IF NOT EXISTS ix_servers_user ON servers(user_id);
            """);

        // migrações incrementais
        try { conn.Execute("ALTER TABLE users ADD COLUMN uid INTEGER"); }
        catch (SqliteException) { /* coluna já existe */ }
    }

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Convert.ToHexString(salt).ToLowerInvariant()}${Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 3 || parts[0] != "pbkdf2") return false;
        var salt = Convert.FromHexString(parts[1]);
        var expected = Convert.FromHexString(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
