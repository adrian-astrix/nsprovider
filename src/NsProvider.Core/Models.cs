namespace NsProvider.Core;

public enum ServerType
{
    Samp = 1,
    OpenMp = 2,
}

public sealed class User
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>UID numérico do Linux usado pra rodar os processos do jogo (drop de privilégio).</summary>
    public int? Uid { get; set; }
}

public sealed class GameServer
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ServerType Type { get; set; }
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public int MaxPlayers { get; set; }
    public string Hostname { get; set; } = "";
    public string RconPassword { get; set; } = "";
    public string Gamemode { get; set; } = "";
    public bool Announce { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ServerProcessStatus
{
    public bool Running { get; set; }
    public int? Pid { get; set; }
    public DateTime? StartedAt { get; set; }
}
