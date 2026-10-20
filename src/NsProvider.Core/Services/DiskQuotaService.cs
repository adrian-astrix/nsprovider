using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using NsProvider.Core.Storage;

namespace NsProvider.Core.Services;

/// <summary>
/// Vigia o espaço em disco de cada servidor (background, a cada NSP_DISK_CHECK_SECS):
///  - soma o tamanho dos arquivos de cada pasta de servidor (exposto na API)
///  - estourou a quota (NSP_DISK_MB) → para o servidor e avisa no console
///  - arquivos criados via SFTP pelo motor (root) ganham chown pro UID do dono,
///    senão o jogo não consegue escrever neles
/// </summary>
public sealed class DiskQuotaService(
    ServerRepository servers,
    UserRepository users,
    ServerManager manager) : BackgroundService
{
    public static int DiskMb => int.TryParse(Environment.GetEnvironmentVariable("NSP_DISK_MB"), out var v) && v > 0 ? v : 5120;
    public static int CheckSeconds => int.TryParse(Environment.GetEnvironmentVariable("NSP_DISK_CHECK_SECS"), out var v) && v > 0 ? v : 60;

    private readonly ConcurrentDictionary<Guid, long> _usageBytes = new();

    public long GetUsageBytes(Guid serverId) => _usageBytes.TryGetValue(serverId, out var b) ? b : 0;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(CheckSeconds));
        while (await timer.WaitForNextTickAsync(stopping))
        {
            try
            {
                foreach (var server in servers.ListAll())
                {
                    var dir = AppPaths.ServerDir(server.UserId, server.Id);
                    if (!Directory.Exists(dir)) continue;

                    var bytes = WalkAndFix(dir, users.GetUid(server.UserId));
                    _usageBytes[server.Id] = bytes;

                    if (bytes > (long)DiskMb * 1024 * 1024 && manager.Processes.IsRunning(server.Id))
                    {
                        manager.Processes.SendEventLine(server.Id,
                            $"[motor] QUOTA DE DISCO EXCEDIDA ({bytes / 1024 / 1024}MB > {DiskMb}MB) — servidor suspenso");
                        manager.Stop(server.Id);
                        Console.WriteLine($"[motor] {server.Id}: quota de disco excedida — servidor suspenso");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[motor] watchdog de disco: {ex.Message}");
            }
        }
    }

    private static long WalkAndFix(string dir, int? uid)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var info = new FileInfo(entry);
                    if (info.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        FixOwner(entry, uid);
                        stack.Push(entry);
                    }
                    else
                    {
                        total += info.Length;
                        FixOwner(entry, uid);
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return total;
    }

    private static void FixOwner(string path, int? uid)
    {
        if (uid is not int u || OperatingSystem.IsWindows()) return;
        NativeMethods.Chown(path, (uint)u, (uint)u); // no-op se o dono já é o mesmo
    }
}

internal static partial class NativeMethods
{
    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string path, uint owner, uint group);

    public static void Chown(string path, uint owner, uint group) => chown(path, owner, group);
}
