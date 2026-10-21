using System.Collections.Concurrent;
using System.Diagnostics;
using NsProvider.Core.Storage;

namespace NsProvider.Core.Services;

/// <summary>
/// Gerencia o processo do jogo: start/stop, console (stdin), ring buffer de saída.
///  - Drop de privilégio: spawn via setpriv com UID/GID sem poderes (jogo nunca roda como root)
///  - Recursos: processo anexado a um cgroup v2 (memória, CPU, pids) — sem Docker
/// O status "running" vive aqui (em memória); o banco guarda o resto.
/// </summary>
public sealed class ProcessManager(ServerEventHub hub)
{
    public const int ConsoleBufferLines = 1000;
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private sealed class ManagedProcess
    {
        public required Process Proc;
        public DateTime StartedAtUtc;
        public readonly object BufferLock = new();
        public readonly List<string> Buffer = new(ConsoleBufferLines);
    }

    private readonly ConcurrentDictionary<Guid, ManagedProcess> _processes = new();
    private readonly ConcurrentDictionary<Guid, object> _eventsLock = new();
    private readonly ConcurrentDictionary<Guid, List<string>> _events = new();
    private readonly CgroupManager _cgroups = new();
    private readonly ServerEventHub _hub = hub;

    public event Action<Guid>? ProcessExited;

    /// <summary>
    /// Reaper: na inicialização do motor, mata processos de servidores órfãos
    /// (ex.: motor morto na força) olhando os links /proc/*/exe dentro da pasta servers/.
    /// </summary>
    public static void CleanupOrphanProcesses()
    {
        if (OperatingSystem.IsWindows()) return;
        var serversRoot = Path.GetFullPath(AppPaths.ServersDir);
        foreach (var pidDir in Directory.GetDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(pidDir), out var pid) && pid != Environment.ProcessId)
            {
                try
                {
                    var exe = File.ResolveLinkTarget(Path.Combine(pidDir, "exe"), returnFinalTarget: true);
                    if (exe?.FullName is { } exePath && exePath.StartsWith(serversRoot))
                    {
                        using var kill = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "/bin/kill",
                            Arguments = $"-9 {pid}",
                            UseShellExecute = false,
                        });
                        kill?.WaitForExit(3_000);
                        Console.WriteLine($"[motor] reaper: processo órfão morto (pid {pid}: {exePath})");
                    }
                }
                catch (System.ComponentModel.Win32Exception) { /* sem permissão — ignora */ }
                catch (FileNotFoundException) { /* processo saiu — ignora */ }
                catch (IOException) { /* corrida de /proc — ignora */ }
            }
    }


    public bool IsRunning(Guid serverId) => _processes.ContainsKey(serverId);

    public ServerProcessStatus GetStatus(Guid serverId)
    {
        if (_processes.TryGetValue(serverId, out var mp) && !mp.Proc.HasExited)
        {
            return new ServerProcessStatus
            {
                Running = true,
                Pid = mp.Proc.Id,
                StartedAt = mp.StartedAtUtc,
            };
        }
        return new ServerProcessStatus { Running = false };
    }

    /// <summary>Adiciona uma linha de evento do motor ao console (visível mesmo parado).</summary>
    public void SendEventLine(Guid serverId, string line)
    {
        var list = _events.GetOrAdd(serverId, _ => new List<string>());
        var lck = _eventsLock.GetOrAdd(serverId, _ => new object());
        lock (lck)
        {
            list.Add(line);
            if (list.Count > 100) list.RemoveRange(0, list.Count - 100);
        }
        _hub.Motor(serverId, line); // streaming em tempo real (SSE)
    }

    public IReadOnlyList<string> GetEvents(Guid serverId, int maxLines)
    {
        if (!_events.TryGetValue(serverId, out var list)) return [];
        var lck = _eventsLock.GetOrAdd(serverId, _ => new object());
        lock (lck)
        {
            var take = Math.Min(maxLines, list.Count);
            return list.GetRange(list.Count - take, take);
        }
    }

    public void Start(Guid serverId, string binPath, string workingDir,
        int uid, int gid, int memoryMb, double cpuCores, int pidsMax)
    {
        if (IsRunning(serverId))
            throw new InvalidOperationException("Servidor já está em execução.");
        if (!File.Exists(binPath))
            throw new FileNotFoundException($"Binário não encontrado: {binPath}");

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(binPath);
            if (!mode.HasFlag(UnixFileMode.UserExecute))
                File.SetUnixFileMode(binPath, mode | UnixFileMode.UserExecute
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        // Drop de privilégio: o jogo executa como UID sem poderes (nunca root).
        // setpriv executa o binário no mesmo PID — Kill da árvore continua funcionando.
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo { FileName = binPath, WorkingDirectory = workingDir }
            : new ProcessStartInfo
            {
                FileName = "/usr/bin/setpriv",
                Arguments = $"--reuid={uid} --regid={gid} --clear-groups {binPath}",
                WorkingDirectory = workingDir,
            };
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.CreateNoWindow = true;

        var proc = new Process { StartInfo = psi };
        var mp = new ManagedProcess { Proc = proc };

        proc.OutputDataReceived += (_, e) => AppendLine(mp, e.Data);
        proc.ErrorDataReceived += (_, e) => AppendLine(mp, e.Data);
        proc.Exited += (_, _) => OnExited(serverId, mp);
        proc.EnableRaisingEvents = true;

        if (!proc.Start())
            throw new InvalidOperationException("Falha ao iniciar o processo do servidor.");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        mp.StartedAtUtc = DateTime.UtcNow;
        SendEventLine(serverId, $"[motor] processo iniciado (pid {proc.Id}, uid {uid})");
        _processes[serverId] = mp;
        _hub.Status(serverId, true, proc.Id, mp.StartedAtUtc);

        // limites de recursos (cgroups) — o filho do samp herda o cgroup no fork
        _cgroups.Attach(serverId, proc.Id, memoryMb, cpuCores, pidsMax);
    }

    public void Stop(Guid serverId)
    {
        if (!_processes.TryRemove(serverId, out var mp))
            return; // não está rodando — idempotente

        try
        {
            // SIGTERM na árvore inteira (pega samp-npc filhos também)
            mp.Proc.Kill(entireProcessTree: true);
            if (!mp.Proc.WaitForExit(StopTimeout))
            {
                // SIGKILL como último recurso
                if (!OperatingSystem.IsWindows() && !mp.Proc.HasExited)
                {
                    try
                    {
                        using var kill = Process.Start(new ProcessStartInfo
                        {
                            FileName = "/bin/kill",
                            Arguments = $"-9 {mp.Proc.Id}",
                            UseShellExecute = false,
                        });
                        kill?.WaitForExit();
                    }
                    catch { /* melhor esforço */ }
                }
                mp.Proc.WaitForExit(5_000);
            }
        }
        finally
        {
            SendEventLine(serverId, "[motor] processo finalizado");
            _cgroups.Remove(serverId);
            _hub.Status(serverId, false);
            mp.Proc.Dispose();
            ProcessExited?.Invoke(serverId);
        }
    }

    /// <summary>Envia um comando para o console do servidor (stdin).</summary>
    public void SendInput(Guid serverId, string command)
    {
        if (!_processes.TryGetValue(serverId, out var mp) || mp.Proc.HasExited)
            throw new InvalidOperationException("Servidor não está em execução.");
        mp.Proc.StandardInput.WriteLine(command);
    }

    /// <summary>Snapshot das últimas linhas do console.</summary>
    public IReadOnlyList<string> GetConsole(Guid serverId, int maxLines)
    {
        if (!_processes.TryGetValue(serverId, out var mp))
            return [];
        lock (mp.BufferLock)
        {
            if (mp.Buffer.Count == 0) return [];
            var take = Math.Min(maxLines, mp.Buffer.Count);
            return mp.Buffer.GetRange(mp.Buffer.Count - take, take);
        }
    }

    private static void AppendLine(ManagedProcess mp, string? line)
    {
        if (line is null) return;
        lock (mp.BufferLock)
        {
            mp.Buffer.Add(line);
            if (mp.Buffer.Count > ConsoleBufferLines)
                mp.Buffer.RemoveRange(0, mp.Buffer.Count - ConsoleBufferLines);
        }
    }

    /// <summary>Quantidade de servidores rodando agora.</summary>
    public int RunningCount
    {
        get
        {
            var n = 0;
            foreach (var kvp in _processes)
                if (!kvp.Value.Proc.HasExited) n++;
            return n;
        }
    }

    private void OnExited(Guid serverId, ManagedProcess mp)
    {
        // libera do dicionário se ainda estiver lá (crash / exit próprio)
        if (_processes.TryRemove(serverId, out _))
        {
            SendEventLine(serverId, "[motor] processo finalizado");
            _cgroups.Remove(serverId);
            _hub.Status(serverId, false);
            ProcessExited?.Invoke(serverId);
        }
    }
}
