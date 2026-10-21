using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;
using NsProvider.Core.Storage;

namespace NsProvider.Core.Services;

/// <summary>
/// Tail contínuo do log de cada servidor em execução → ServerEventHub (SSE).
/// SA-MP e open.mp não escrevem no stdout: o console real está em
/// server_log.txt / log.txt. Lê só o que é novo desde o último tick
/// (com detecção de rotação/truncamento) e entrega linha a linha.
/// </summary>
public sealed class ConsoleTailService(
    ServerRepository servers,
    ProcessManager processes,
    ServerEventHub hub) : BackgroundService
{
    private const int PollMs = 400;
    private const int InitialTailBytes = 64 * 1024;

    private sealed class TailState
    {
        public long Offset = -1;
        public string Partial = "";
    }

    private readonly ConcurrentDictionary<Guid, TailState> _tails = new();

    /// <summary>
    /// Chamado pelo ServerManager no exato momento do start: a partir daqui o log
    /// só ganha linhas NOVAS deste boot (o que veio antes fica no histórico do GET).
    /// </summary>
    public void MarkStarting(Guid serverId, long logLengthAtStart)
    {
        var state = _tails.GetOrAdd(serverId, _ => new TailState());
        state.Offset = Math.Max(0, logLengthAtStart);
        state.Partial = "";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { Console.Error.WriteLine($"[motor] tail do console: {ex.Message}"); }

            try { await Task.Delay(PollMs, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        var running = new HashSet<Guid>();

        foreach (var server in servers.ListAll())
        {
            if (!processes.IsRunning(server.Id)) continue;
            running.Add(server.Id);

            var logPath = Path.Combine(
                AppPaths.ServerDir(server.UserId, server.Id),
                server.Type == ServerType.Samp ? "server_log.txt" : "log.txt");
            if (!File.Exists(logPath)) continue;

            try
            {
                var state = _tails.GetOrAdd(server.Id, _ => new TailState());
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                // sem offset (primeiro tick) ou arquivo girou/truncou → resincroniza
                if (state.Offset < 0 || state.Offset > fs.Length)
                    state.Offset = InitialOffset(fs);
                if (fs.Length == state.Offset) continue;

                fs.Seek(state.Offset, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                state.Offset = fs.Length;

                var buffer = state.Partial + text;
                var parts = buffer.Split('\n');
                state.Partial = parts[^1];
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    var line = parts[i].TrimEnd('\r');
                    if (line.Length > 0) hub.Log(server.Id, line);
                }
            }
            catch (IOException) { /* log em rotação — tenta no próximo tick */ }
            catch (UnauthorizedAccessException) { /* sem permissão — ignora */ }
        }

        // servidor parou: esquece o offset para reler do começo no próximo start
        foreach (var id in _tails.Keys)
            if (!running.Contains(id)) _tails.TryRemove(id, out _);
    }

    /// <summary>
    /// Primeiro tick: começa nos últimos 64KB (inclui o boot recém-escrito) e pula
    /// até a próxima quebra de linha para não publicar uma linha cortada.
    /// </summary>
    private static long InitialOffset(FileStream fs)
    {
        if (fs.Length <= InitialTailBytes) return 0;

        fs.Seek(fs.Length - InitialTailBytes, SeekOrigin.Begin);
        int b;
        while ((b = fs.ReadByte()) != -1 && b != '\n') { /* descarta linha parcial */ }
        return fs.Position;
    }
}
