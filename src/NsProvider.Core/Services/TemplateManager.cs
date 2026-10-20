using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace NsProvider.Core.Services;

/// <summary>
/// Gerencia os templates base (pacotes oficiais SA-MP / open.mp).
/// Aceita instalação via .zip ou .tar.gz — os dois formatos que os projetos distribuem.
/// </summary>
public sealed class TemplateManager
{
    public static string BinaryName(ServerType type) =>
        type == ServerType.Samp ? "samp03svr" : "omp-server";

    public static string PackageName(ServerType type) =>
        type == ServerType.Samp ? "samp" : "openmp";

    public bool IsInstalled(ServerType type)
    {
        return File.Exists(Path.Combine(AppPaths.TemplateDir(type), BinaryName(type)));
    }

    public IReadOnlyList<object> ListInstalled()
    {
        var list = new List<object>();
        foreach (ServerType type in Enum.GetValues<ServerType>())
        {
            var dir = AppPaths.TemplateDir(type);
            list.Add(new
            {
                type = PackageName(type),
                installed = IsInstalled(type),
                path = dir,
            });
        }
        return list;
    }

    /// <summary>
    /// Instala um template a partir de um stream .zip ou .tar.gz.
    /// Detecta e remove a pasta-raiz do arquivo (ex: "samp03/" ou "Server/")
    /// de forma que o binário fique na raiz do diretório do template.
    /// </summary>
    public ServerType Install(ServerType type, Stream packageStream, string fileName)
    {
        var dest = AppPaths.TemplateDir(type);
        var staging = dest + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        try
        {
            var lower = (fileName ?? "").ToLowerInvariant();
            if (lower.EndsWith(".zip"))
            {
                using var zip = new ZipArchive(packageStream, ZipArchiveMode.Read);
                zip.ExtractToDirectory(staging);
            }
            else if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz") || lower.EndsWith(".gz"))
            {
                using var gzip = new GZipStream(packageStream, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                while (tar.GetNextEntry() is { } entry)
                {
                    var entryPath = entry.Name;
                    if (string.IsNullOrWhiteSpace(entryPath)) continue;
                    var target = Path.GetFullPath(Path.Combine(staging, entryPath));
                    if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar))
                        continue; // ignora path traversal
                    switch (entry.EntryType)
                    {
                        case TarEntryType.Directory:
                            Directory.CreateDirectory(target);
                            break;
                        case TarEntryType.RegularFile:
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            using var fs = File.Create(target);
                            entry.DataStream?.CopyTo(fs);
                            break;
                        }
                        case TarEntryType.SymbolicLink:
                        {
                            var link = ((TarEntry)entry).LinkName;
                            if (link is not null && !File.Exists(target) && !Directory.Exists(target))
                                File.CreateSymbolicLink(target, link);
                            break;
                        }
                    }
                }
            }
            else
            {
                throw new ArgumentException("Formato não suportado. Use .zip ou .tar.gz.");
            }

            // Achata a pasta-raiz única, se existir (ex: samp03/, Server/)
            var rootDir = FlattenSingleRoot(staging);

            // Garante execução do binário principal em Linux
            var bin = Path.Combine(rootDir, BinaryName(type));
            if (!File.Exists(bin))
                throw new InvalidOperationException(
                    $"Binário '{BinaryName(type)}' não encontrado no pacote. Template {PackageName(type)} não instalado.");

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(bin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.Move(rootDir, dest);
            return type;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static string FlattenSingleRoot(string dir)
    {
        // Ex: pacote extraído em "staging/samp03/..." ou "staging/Server/..."
        // → move o conteúdo da única subpasta para a raiz do staging.
        while (true)
        {
            var entries = Directory.GetFileSystemEntries(dir);
            if (entries.Length != 1 || !Directory.Exists(entries[0])) break;
            var single = entries[0];
            foreach (var child in Directory.GetFileSystemEntries(single))
            {
                var target = Path.Combine(dir, Path.GetFileName(child));
                if (Directory.Exists(child))
                    Directory.Move(child, target);
                else
                    File.Move(child, target);
            }
            Directory.Delete(single, true);
        }
        return dir;
    }
}
