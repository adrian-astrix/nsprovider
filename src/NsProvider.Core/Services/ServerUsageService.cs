using System.Collections.Concurrent;
using NsProvider.Core.Storage;

namespace NsProvider.Core.Services;

/// <summary>
/// Consumo ao vivo de cada servidor, direto do cgroup v2:
///  - CPU: cpu.stat (usage_usec) — delta entre leituras / tempo = % de 1 core
///  - RAM: memory.current (fallback: VmRSS do processo)
///  - PIDs: pids.current
///  - Disco: watchdog (DiskQuotaService)
/// Usado pelo painel para mostrar os gráficos/números por servidor.
/// </summary>
public sealed class ServerUsageService(
    ServerRepository servers,
    ProcessManager processes,
    DiskQuotaService disk)
{
    private sealed record CpuSample(long UsageUsec, long Ticks);

    private readonly ConcurrentDictionary<Guid, CpuSample> _cpuSamples = new();

    public sealed record ServerUsage(
        Guid ServerId,
        bool Running,
        int? Pid,
        DateTime? StartedAt,
        long UptimeSeconds,
        double CpuPercent,
        double MemoryMb,
        int MemoryLimitMb,
        double CpuCoresLimit,
        int Pids,
        int PidsLimit,
        double DiskUsageMb,
        int DiskQuotaMb);

    public IReadOnlyList<ServerUsage> ListAll() => servers.ListAll().Select(s => Get(s.Id)).ToList();

    public ServerUsage Get(Guid serverId)
    {
        var server = servers.GetById(serverId)
            ?? throw new InvalidOperationException("Servidor não encontrado.");

        var status = processes.GetStatus(serverId);
        var memLimit = server.MemoryMb ?? CgroupManager.MemoryMb;
        var cpuLimit = server.CpuCores ?? CgroupManager.CpuCores;
        var diskQuota = server.DiskMb ?? DiskQuotaService.DiskMb;
        var diskBytes = disk.GetUsageBytes(serverId);

        double cpuPercent = 0;
        double memoryMb = 0;
        var pids = 0;

        if (status.Running)
        {
            var groupDir = $"/sys/fs/cgroup/{CgroupManager.Slice}/{serverId}";
            var usageUsec = ReadKeyValue(Path.Combine(groupDir, "cpu.stat"), "usage_usec");

            if (usageUsec > 0)
            {
                var now = DateTime.UtcNow.Ticks; // 100ns
                var sample = _cpuSamples.GetOrAdd(serverId, _ => new CpuSample(usageUsec, now));
                var deltaUsec = usageUsec - sample.UsageUsec;
                var deltaTicks = now - sample.Ticks;
                if (deltaUsec >= 0 && deltaTicks > 0)
                {
                    // ticks(100ns) → µs = /10 ; % de 1 core = deltaUsec / deltaUs * 100
                    cpuPercent = Math.Round(deltaUsec * 1000.0 / deltaTicks, 1);
                }
                _cpuSamples[serverId] = new CpuSample(usageUsec, now);
            }

            var memBytes = ReadLongFile(Path.Combine(groupDir, "memory.current"));
            if (memBytes == 0 && status.Pid is { } pid) memBytes = ReadRssBytes(pid);
            memoryMb = Math.Round(memBytes / 1024.0 / 1024.0, 1);

            pids = (int)ReadLongFile(Path.Combine(groupDir, "pids.current"));
        }
        else
        {
            _cpuSamples.TryRemove(serverId, out _); // reinicia a amostra no próximo start
        }

        var uptime = status is { Running: true, StartedAt: { } started }
            ? (long)Math.Max(0, (DateTime.UtcNow - started).TotalSeconds)
            : 0;

        return new ServerUsage(
            serverId,
            status.Running,
            status.Pid,
            status.StartedAt,
            uptime,
            cpuPercent,
            memoryMb,
            memLimit,
            cpuLimit,
            pids,
            CgroupManager.PidsMax,
            Math.Round(diskBytes / 1024.0 / 1024.0, 1),
            diskQuota);
    }

    /// <summary>Lê "chave valor" de um arquivo do cgroup (ex.: usage_usec em cpu.stat).</summary>
    private static long ReadKeyValue(string path, string key)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == key &&
                    long.TryParse(parts[1], out var value))
                    return value;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return 0;
    }

    private static long ReadLongFile(string path)
    {
        try { return long.TryParse(File.ReadAllText(path).Trim(), out var v) ? v : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Fallback: VmRSS do /proc/pid/status (quando o cgroup não está disponível).</summary>
    private static long ReadRssBytes(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    var kb = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Skip(1).FirstOrDefault();
                    return long.TryParse(kb, out var v) ? v * 1024 : 0;
                }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return 0;
    }
}
