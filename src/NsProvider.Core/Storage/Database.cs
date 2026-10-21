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
            CREATE INDEX IF NOT EXISTS ix_servers_user ON servers(user_id);
            CREATE TABLE IF NOT EXISTS backups (
                id          TEXT PRIMARY KEY,
                server_id   TEXT NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
                status      TEXT NOT NULL,
                size_bytes  INTEGER,
                created_at  TEXT NOT NULL,
                finished_at TEXT,
                error       TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_backups_server ON backups(server_id);
            """);

        // migrações incrementais
        try { conn.Execute("ALTER TABLE users ADD COLUMN uid INTEGER"); }
        catch (SqliteException) { /* coluna já existe */ }
        try { conn.Execute("ALTER TABLE servers ADD COLUMN memory_mb INTEGER"); }
        catch (SqliteException) { }
        try { conn.Execute("ALTER TABLE servers ADD COLUMN cpu_cores REAL"); }
        catch (SqliteException) { }
        try { conn.Execute("ALTER TABLE servers ADD COLUMN disk_mb INTEGER"); }
        catch (SqliteException) { }
        try { conn.Execute("ALTER TABLE servers ADD COLUMN bind_ip TEXT"); }
        catch (SqliteException) { }
        // porta agora é única POR IP (dois servidores podem usar 7777 em IPs diferentes)
        try { conn.Execute("DROP INDEX IF EXISTS ix_servers_port"); } catch (SqliteException) { }
        try { conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS ix_servers_ip_port ON servers(COALESCE(bind_ip, ''), port)"); }
        catch (SqliteException) { }
    }
}
