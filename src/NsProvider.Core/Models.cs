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

    // recursos por servidor (null = usa o default global do motor via env)
    public int? MemoryMb { get; set; }
    public double? CpuCores { get; set; }
    public int? DiskMb { get; set; }

    /// <summary>IPv4 local em que o jogo escuta (null/vazio = 0.0.0.0, IP compartilhado).</summary>
    public string? BindIp { get; set; }
}

public sealed class ServerProcessStatus
{
    public bool Running { get; set; }
    public int? Pid { get; set; }
    public DateTime? StartedAt { get; set; }
}

/// <summary>Status possíveis de um backup: pending → ready | failed.</summary>
public sealed class Backup
{
    public Guid Id { get; set; }
    public Guid ServerId { get; set; }
    public string Status { get; set; } = "pending";
    public long? SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Error { get; set; }
}
