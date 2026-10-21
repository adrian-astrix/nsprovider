using System.Collections.Concurrent;
using System.Threading.Channels;

namespace NsProvider.Core.Services;

/// <summary>
/// Evento transmitido em tempo real (SSE) para o painel.
///   log    → linha do console do jogo (tail do server_log.txt / log.txt)
///   motor  → linha gerada pelo motor (start/stop, recursos, backups...)
///   status → mudou de estado (running/pid/startedAt)
///   snapshot → estado inicial de todos os servidores (só no /api/events)
/// </summary>
public sealed record ServerEvent(
    string Type,
    Guid ServerId,
    string? Line = null,
    bool? Running = null,
    int? Pid = null,
    DateTime? StartedAt = null,
    DateTime? At = null);

/// <summary>
/// Pub/sub em memória: cada painel conectado vira um assinante com canal limitado
/// (drop-oldest: cliente lento nunca trava o motor). Assinatura por servidor
/// (console + status) ou global (status de todos, sem linhas de log).
/// </summary>
public sealed class ServerEventHub
{
    public const int SubscriberBuffer = 4096;

    private sealed class Subscriber(Guid? serverId)
    {
        public Guid? ServerId { get; } = serverId;
        public System.Threading.Channels.Channel<ServerEvent> Channel { get; } =
            System.Threading.Channels.Channel.CreateBounded<ServerEvent>(
            new BoundedChannelOptions(SubscriberBuffer)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
    }

    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly ConcurrentDictionary<Guid, bool> _lastRunning = new();

    /// <summary>Assina eventos de um servidor (null = todos os servidores, sem logs).</summary>
    public (Guid Id, ChannelReader<ServerEvent> Reader) Subscribe(Guid? serverId)
    {
        var sub = new Subscriber(serverId);
        var id = Guid.NewGuid();
        _subscribers[id] = sub;
        return (id, sub.Channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var sub))
            sub.Channel.Writer.TryComplete();
    }

    public void Publish(ServerEvent ev)
    {
        ev = ev with { At = ev.At ?? DateTime.UtcNow };
        foreach (var sub in _subscribers.Values)
        {
            // log só vai para quem assinou aquele servidor (global ficaria poluído)
            if (ev.Type == "log" && sub.ServerId is null) continue;
            if (sub.ServerId is { } sid && sid != ev.ServerId) continue;
            sub.Channel.Writer.TryWrite(ev);
        }
    }

    public void Log(Guid serverId, string line) => Publish(new ServerEvent("log", serverId, line));

    public void Motor(Guid serverId, string line) => Publish(new ServerEvent("motor", serverId, line));

    /// <summary>Publica mudança de estado (deduplica stop + exit do mesmo processo).</summary>
    public void Status(Guid serverId, bool running, int? pid = null, DateTime? startedAt = null)
    {
        if (_lastRunning.TryGetValue(serverId, out var prev) && prev == running)
            return;
        _lastRunning[serverId] = running;
        Publish(new ServerEvent("status", serverId, null, running, pid, startedAt));
    }
}
