using System.Text;

namespace NsProvider.Core.Services;

/// <summary>
/// Limites de recursos por servidor via cgroups v2 nativos (sem Docker).
/// O motor (root) cria /sys/fs/cgroup/{Slice}/{serverId} e anexa o PID do jogo.
/// Samp03svr/omp-server herdam o cgroup em todos os filhos (fork).
/// </summary>
public sealed class CgroupManager
{
    public const string Slice = "nsprovider.slice";

    public static bool Available =>
        Directory.Exists("/sys/fs/cgroup") &&
        File.Exists("/sys/fs/cgroup/cgroup.controllers");

    // defaults configuráveis por env
    public static int MemoryMb => int.TryParse(Environment.GetEnvironmentVariable("NSP_MEM_MB"), out var v) && v > 0 ? v : 1024;
    public static double CpuCores => double.TryParse(Environment.GetEnvironmentVariable("NSP_CPU_CORES"), out var v) && v > 0 ? v : 1.0;
    public static int PidsMax => int.TryParse(Environment.GetEnvironmentVariable("NSP_PIDS_MAX"), out var v) && v > 0 ? v : 200;

    private static string SliceDir => $"/sys/fs/cgroup/{Slice}";
    private static string GroupDir(Guid serverId) => $"{SliceDir}/{serverId}";

    /// <summary>
    /// Garante o slice do motor e habilita os controllers na subárvore.
    /// Chamar na inicialização (e ignorar erro se o sistema não tiver o controller).
    /// </summary>
    public void PrepareSlice()
    {
        if (!Available) return;
        try
        {
            Directory.CreateDirectory(SliceDir);

            // habilita controllers disponíveis para os filhos do slice
            var controllersRoot = File.ReadAllText("/sys/fs/cgroup/cgroup.controllers").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var enabled = File.Exists(Path.Combine(SliceDir, "cgroup.subtree_control"))
                ? File.ReadAllText(Path.Combine(SliceDir, "cgroup.subtree_control")).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : [];
            var enable = controllersRoot
                .Where(c => c is "memory" or "cpu" or "pids" or "io")
                .Where(c => !enabled.Contains(c))
                .Select(c => "+" + c);
            var line = string.Join(' ', enable);
            if (line.Length > 0)
                File.WriteAllText(Path.Combine(SliceDir, "cgroup.subtree_control"), line);
        }
        catch (UnauthorizedAccessException)
        {
            Console.WriteLine("[motor] aviso: sem permissão p/ cgroups (limites de recursos desativados)");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[motor] aviso: cgroups indisponíveis ({ex.Message})");
        }
    }

    /// <summary>Anexa o processo do jogo a um cgroup com limites. Best-effort.</summary>
    public void Attach(Guid serverId, int pid, int memoryMb, double cpuCores, int pidsMax)
    {
        if (!Available) return;
        try
        {
            var dir = GroupDir(serverId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "memory.max"), $"{(long)memoryMb * 1024 * 1024}");
            File.WriteAllText(Path.Combine(dir, "memory.swap.max"), $"{(long)memoryMb * 1024 * 1024}");
            // ex: 1.0 core => "100000 100000"
            var quota = (long)(cpuCores * 100_000);
            File.WriteAllText(Path.Combine(dir, "cpu.max"), $"{quota} 100000");
            File.WriteAllText(Path.Combine(dir, "pids.max"), $"{pidsMax}");
            File.WriteAllText(Path.Combine(dir, "cgroup.procs"), $"{pid}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[motor] aviso: não consegui aplicar cgroup p/ {serverId}: {ex.Message}");
        }
    }

    /// <summary>Remove o cgroup do servidor (best-effort; falha se ainda tiver processo).</summary>
    public void Remove(Guid serverId)
    {
        if (!Available) return;
        try { Directory.Delete(GroupDir(serverId), false); }
        catch { /* ainda em uso ou inexistente */ }
    }

    /// <summary>Limpa cgroups vazios de execuções anteriores.</summary>
    public void CleanupEmpty()
    {
        if (!Available || !Directory.Exists(SliceDir)) return;
        foreach (var dir in Directory.GetDirectories(SliceDir))
        {
            try
            {
                var procs = File.ReadAllText(Path.Combine(dir, "cgroup.procs")).Trim();
                if (procs.Length == 0) Directory.Delete(dir);
            }
            catch { }
        }
    }
}
