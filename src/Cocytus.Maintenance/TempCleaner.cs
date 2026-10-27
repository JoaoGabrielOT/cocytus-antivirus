using System.IO.Enumeration;
using Cocytus.Maintenance.Native;

namespace Cocytus.Maintenance;

/// <summary>Uma pasta onde a limpeza procura arquivos.</summary>
/// <param name="Folder">Pasta.</param>
/// <param name="Pattern">Filtro de nome (ex.: thumbcache_*.db).</param>
/// <param name="Recursive">Inclui subpastas (sem seguir links nem junções).</param>
/// <param name="MinAge">Só arquivos sem alteração há pelo menos esse tempo.</param>
public sealed record CleanupRoot(string Folder, string Pattern, bool Recursive, TimeSpan MinAge);

public sealed record CleanupCategory(string Id, string Name, string Description, IReadOnlyList<CleanupRoot> Roots);

public sealed record CleanupEstimate(CleanupCategory Category, int Files, long Bytes);

public sealed record CleanupResult(int Deleted, long BytesFreed, int Skipped, IReadOnlyList<string> SkippedSamples);

/// <summary>
/// Limpeza de arquivos temporários do usuário. Não segue links simbólicos nem junções, não toca em arquivos somente
/// leitura e pula os que estão em uso. Os arquivos apagados não vão para a Lixeira (são temporários).
/// </summary>
public static class TempCleaner
{
    private const int MaxSamples = 20;

    public static IReadOnlyList<CleanupCategory> DefaultCategories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            new("temp", "Arquivos temporários do usuário",
                "Pasta Temp do seu usuário. Só arquivos sem alteração há mais de 24 horas; os que estão em uso são mantidos.",
                [new CleanupRoot(Path.GetTempPath(), "*", Recursive: true, TimeSpan.FromHours(24))]),
            new("thumbs", "Cache de miniaturas",
                "Miniaturas de imagens e vídeos do Explorador. O Windows recria quando precisar; arquivos em uso pelo Explorador são mantidos.",
                [new CleanupRoot(Path.Combine(local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db", Recursive: false, TimeSpan.Zero)]),
            new("wer", "Relatórios de erro do Windows (usuário)",
                "Relatórios de falhas de programas já enviados ou na fila, do seu usuário.",
                [
                    new CleanupRoot(Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"), "*", Recursive: true, TimeSpan.Zero),
                    new CleanupRoot(Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue"), "*", Recursive: true, TimeSpan.Zero),
                ]),
        ];
    }

    public static CleanupEstimate Analyze(CleanupCategory category, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(category);
        var files = 0;
        long bytes = 0;
        foreach (var root in category.Roots)
        {
            foreach (var file in Candidates(root, DateTime.UtcNow, cancellationToken))
            {
                files++;
                bytes += file.Length;
            }
        }

        return new CleanupEstimate(category, files, bytes);
    }

    public static CleanupResult Clean(CleanupCategory category, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(category);
        var now = DateTime.UtcNow;
        var deleted = 0;
        long freed = 0;
        var skipped = 0;
        var samples = new List<string>();
        foreach (var root in category.Roots)
        {
            // Pastas antigas que podem ficar vazias depois da limpeza (a data delas muda quando os arquivos saem).
            var oldFolders = root.Recursive ? OldFolders(root, now, cancellationToken) : [];
            foreach (var file in Candidates(root, now, cancellationToken).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var length = file.Length;
                    file.Delete();
                    deleted++;
                    freed += length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    if (samples.Count < MaxSamples)
                    {
                        samples.Add($"{file.FullName}: {ex.Message}");
                    }
                }
            }

            foreach (var folder in oldFolders.OrderByDescending(f => f.Length))
            {
                try
                {
                    if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        Directory.Delete(folder);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Pasta em uso ou recriada: fica.
                }
            }
        }

        return new CleanupResult(deleted, freed, skipped, samples);
    }

    private static IEnumerable<FileInfo> Candidates(CleanupRoot root, DateTime utcNow, CancellationToken cancellationToken)
    {
        if (!IsSafeRoot(root.Folder))
        {
            yield break;
        }

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root.Folder));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = dir.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (entry is DirectoryInfo sub)
                {
                    if (root.Recursive)
                    {
                        pending.Push(sub);
                    }

                    continue;
                }

                if (entry is FileInfo file
                    && !file.Attributes.HasFlag(FileAttributes.ReadOnly)
                    && !file.Attributes.HasFlag(FileAttributes.System)
                    && FileSystemName.MatchesSimpleExpression(root.Pattern, file.Name)
                    && IsOldEnough(file, root.MinAge, utcNow))
                {
                    yield return file;
                }
            }
        }
    }

    private static List<string> OldFolders(CleanupRoot root, DateTime utcNow, CancellationToken cancellationToken)
    {
        var folders = new List<string>();
        if (!IsSafeRoot(root.Folder))
        {
            return folders;
        }

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root.Folder));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo[] subs;
            try
            {
                subs = pending.Pop().GetDirectories();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sub in subs)
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (IsOldEnough(sub, root.MinAge, utcNow))
                {
                    folders.Add(sub.FullName);
                }

                pending.Push(sub);
            }
        }

        return folders;
    }

    private static bool IsOldEnough(FileSystemInfo info, TimeSpan minAge, DateTime utcNow)
    {
        if (minAge <= TimeSpan.Zero)
        {
            return true;
        }

        var newest = info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc;
        return utcNow - newest >= minAge;
    }

    /// <summary>Nunca limpa raiz de unidade, pasta do Windows, perfil do usuário ou uma pasta que seja link.</summary>
    internal static bool IsSafeRoot(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return false;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var forbidden = new[]
        {
            Path.GetPathRoot(full) ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (forbidden.Any(f => f.Length > 0 && Path.TrimEndingDirectorySeparator(f).Equals(full, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint);
    }
}

/// <summary>Lixeira de todas as unidades.</summary>
public static class RecycleBin
{
    public static (long Items, long Bytes) Query() => MaintenanceNative.QueryRecycleBin();

    /// <summary>Esvazia (exclusão definitiva). Chame só depois da confirmação do usuário.</summary>
    public static void Empty(IntPtr ownerWindow) => MaintenanceNative.EmptyRecycleBin(ownerWindow);
}
