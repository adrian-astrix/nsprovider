using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using NsProvider.Core.Storage;

namespace NsProvider.Core.Services;

/// <summary>
/// Estatísticas da máquina pro painel (GET /api/stats):
/// processador (modelo, cores, threads, uso), memória, disco e servidores (total x rodando).
/// O uso de CPU é amostrado em background lendo /proc/stat.
/// </summary>
public sealed class SystemStatsService(
    ServerRepository servers,
    ProcessManager processes) : BackgroundService
{
    private double _cpuPercent;
    private readonly object _cpuLock = new();

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        var previous = ReadProcStat();
        while (!stopping.IsCancellationRequested)
        {
            await Task.Delay(2_000, stopping);
            try
            {
                var current = ReadProcStat();
                var totalDelta = current.total - previous.total;
                var idleDelta = current.idle - previous.idle;
                if (totalDelta > 0)
                {
                    lock (_cpuLock)
                        _cpuPercent = 100.0 * (1.0 - (double)idleDelta / totalDelta);
                }
                previous = current;
            }
            catch { /* /proc indisponível — tenta de novo */ }
        }
    }

    public object GetSnapshot()
    {
        var all = servers.ListAll();
        var running = processes.RunningCount;

        return new
        {
            motor = "nsprovider",
            versao = "0.4.3",
            uptimeSeconds = (long)(DateTime.UtcNow - ProcessStartTimeUtc).TotalSeconds,
            processador = CpuInfo(),
            memoria = MemoryInfo(),
            disco = DiskInfo(),
            servidores = new
            {
                total = all.Count,
                rodando = running,
                parados = all.Count - running,
            },
        };
    }

    private static readonly DateTime ProcessStartTimeUtc = DateTime.UtcNow;

    private object CpuInfo()
    {
        var (model, threads, cores) = ReadCpuInfo();
        return new
        {
            modelo = model,
            threads,
            cores,
            usoPercent = Math.Round(GetCpuPercent(), 1),
            loadAvg = ReadLoadAvg(),
        };
    }

    private static (string model, int threads, int cores) ReadCpuInfo()
    {
        var model = "";
        var threads = 0;
        var coreIds = new HashSet<string>();
        try
        {
            string? currentPhysical = null;
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (key == "model name" && model.Length == 0) model = value;
                else if (key == "processor") threads++;
                else if (key == "physical id") currentPhysical = value;
                else if (key == "core id" && currentPhysical is not null) coreIds.Add($"{currentPhysical}:{value}");
            }
        }
        catch { }
        if (threads == 0) threads = Environment.ProcessorCount;
        var cores = coreIds.Count > 0 ? coreIds.Count : threads;
        return (model is "" ? "desconhecido" : model, threads, cores);
    }

    private static object MemoryInfo()
    {
        long totalKb = 0, availableKb = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:")) totalKb = ParseKb(line);
                else if (line.StartsWith("MemAvailable:")) availableKb = ParseKb(line);
            }
        }
        catch { }
        var used = Math.Max(0, totalKb - availableKb);
        return new
        {
            totalMb = totalKb / 1024,
            usadaMb = used / 1024,
            disponivelMb = availableKb / 1024,
        };
    }

    private static long ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var v) ? v : 0;
    }

    private static object DiskInfo()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(AppPaths.Base)) ?? "/";
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.Name == root)
                ?? new DriveInfo(root);
            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            return new
            {
                totalGb = Math.Round(total / 1073741824.0, 1),
                usadoGb = Math.Round((total - free) / 1073741824.0, 1),
                livreGb = Math.Round(free / 1073741824.0, 1),
            };
        }
        catch
        {
            return new { totalGb = 0.0, usadoGb = 0.0, livreGb = 0.0 };
        }
    }

    private static double[] ReadLoadAvg()
    {
        try
        {
            var parts = File.ReadLines("/proc/loadavg").First().Split(' ');
            return parts.Take(3).Select(p => double.TryParse(p, out var v) ? v : 0).ToArray();
        }
        catch { return [0, 0, 0]; }
    }

    private static (long idle, long total) ReadProcStat()
    {
        var first = File.ReadLines("/proc/stat").First();
        if (!first.StartsWith("cpu ")) return (0, 0);
        var values = first.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Select(long.Parse).ToArray();
        var idle = values[3] + (values.Length > 4 ? values[4] : 0); // idle + iowait
        return (idle, values.Sum());
    }

    private double GetCpuPercent()
    {
        lock (_cpuLock) return _cpuPercent;
    }
}
