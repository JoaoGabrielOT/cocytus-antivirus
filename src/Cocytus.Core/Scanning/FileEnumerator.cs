namespace Cocytus.Core.Scanning;

internal enum ScanItemKind
{
    File,
    Skipped,
    Error,
}

internal readonly record struct ScanItem(string Path, ScanItemKind Kind, NotScannedReason Reason, string? Detail)
{
    public static ScanItem ForFile(string path) => new(path, ScanItemKind.File, NotScannedReason.None, null);

    public static ScanItem Skip(string path, NotScannedReason reason, string detail) => new(path, ScanItemKind.Skipped, reason, detail);

    public static ScanItem Fail(string path, string detail) => new(path, ScanItemKind.Error, NotScannedReason.None, detail);
}

/// <summary>
/// Percorre os alvos sem seguir links simbólicos nem junções, e sem forçar o download de
/// arquivos que estão só na nuvem (OneDrive "somente online").
/// </summary>
internal static class FileEnumerator
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes CloudOnlyMask = RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline;

    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Converte para caminhos absolutos, remove repetidos e alvos que já estão dentro de outra pasta alvo.</summary>
    public static IReadOnlyList<string> NormalizeTargets(IEnumerable<string> targets) =>
        NormalizeTargets(targets.Select(t => new ScanTarget(t))).Select(t => t.Path).ToList();

    /// <summary>
    /// Converte para caminhos absolutos, junta repetidos e remove alvos já cobertos por outra pasta alvo.
    /// Uma pasta restrita a executáveis só cobre alvos também restritos ou arquivos executáveis; uma pasta completa
    /// dentro de uma restrita continua como alvo próprio (e a restrita não entra nela).
    /// </summary>
    public static IReadOnlyList<ScanTarget> NormalizeTargets(IEnumerable<ScanTarget> targets)
    {
        var merged = new List<ScanTarget>();
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.Path))
            {
                continue;
            }

            var path = ToFullPath(target.Path);
            var index = merged.FindIndex(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                merged.Add(target with { Path = path });
            }
            else if (!target.ExecutablesOnly)
            {
                merged[index] = merged[index] with { ExecutablesOnly = false };
            }
        }

        return merged
            .Where(t => !merged.Any(other => !ReferenceEquals(other, t) && IsInside(t.Path, other.Path) && Directory.Exists(other.Path) && Covers(other, t)))
            .ToList();
    }

    private static bool Covers(ScanTarget outer, ScanTarget inner) =>
        !outer.ExecutablesOnly || inner.ExecutablesOnly || (!Directory.Exists(inner.Path) && ExecutableTypes.Matches(inner.Path));

    private static string ToFullPath(string target)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return target.Trim();
        }
    }

    internal static bool IsInside(string path, string folder)
    {
        var prefix = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<ScanItem> Enumerate(IReadOnlyList<string> targets, CancellationToken cancellationToken) =>
        Enumerate(targets.Select(t => new ScanTarget(t)).ToList(), [], cancellationToken);

    /// <summary>
    /// Percorre os alvos (já normalizados). Pastas excluídas não são percorridas; uma subpasta que é alvo próprio é
    /// deixada para ele. Em alvos restritos, só saem executáveis e scripts (e falhas ao listar pastas).
    /// </summary>
    public static IEnumerable<ScanItem> Enumerate(IReadOnlyList<ScanTarget> targets, IReadOnlyList<string> excludedFolders, CancellationToken cancellationToken)
    {
        var excluded = excludedFolders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(ToFullPath)
            .ToList();
        var ownTargets = new HashSet<string>(targets.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExcluded(target.Path, excluded))
            {
                continue;
            }

            var single = ClassifyPath(target.Path, out var isDirectory);
            if (!isDirectory)
            {
                if (single is { } value && Accepts(target, value))
                {
                    yield return value;
                }

                continue;
            }

            pending.Push(target.Path);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                var error = TryList(dir, out var entries);
                if (error is not null)
                {
                    yield return ScanItem.Fail(dir, error);
                    continue;
                }

                var subdirs = new List<string>();
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = Classify(entry, out var entryIsDirectory);
                    if (entryIsDirectory)
                    {
                        if (!ownTargets.Contains(entry.FullName) && !IsExcluded(entry.FullName, excluded))
                        {
                            subdirs.Add(entry.FullName);
                        }
                    }
                    else if (item is { } fileItem && Accepts(target, fileItem) && !IsExcluded(fileItem.Path, excluded))
                    {
                        yield return fileItem;
                    }
                }

                for (var i = subdirs.Count - 1; i >= 0; i--)
                {
                    pending.Push(subdirs[i]);
                }
            }
        }
    }

    private static bool Accepts(ScanTarget target, ScanItem item) => !target.ExecutablesOnly || ExecutableTypes.Matches(item.Path);

    private static bool IsExcluded(string path, List<string> excluded) =>
        excluded.Exists(folder => path.Equals(folder, StringComparison.OrdinalIgnoreCase) || IsInside(path, folder));

    /// <summary>Para um alvo escolhido pelo usuário: devolve o item (arquivo, pulado, erro) ou null se for pasta a percorrer.</summary>
    internal static ScanItem? ClassifyPath(string target, out bool isDirectory)
    {
        isDirectory = false;
        FileSystemInfo info;
        try
        {
            var attributes = File.GetAttributes(target);
            info = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(target) : new FileInfo(target);
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            return ScanItem.Fail(target, DescribeIoError(ex));
        }

        return Classify(info, out isDirectory);
    }

    private static ScanItem? Classify(FileSystemInfo entry, out bool isDirectory)
    {
        isDirectory = false;
        FileAttributes attributes;
        try
        {
            attributes = entry.Attributes;
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            return ScanItem.Fail(entry.FullName, DescribeIoError(ex));
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            string? linkTarget;
            try
            {
                linkTarget = entry.LinkTarget;
            }
            catch (Exception ex) when (IsIoError(ex))
            {
                return ScanItem.Fail(entry.FullName, "Não foi possível ler o ponto de nova análise: " + DescribeIoError(ex));
            }

            if (linkTarget is not null)
            {
                return ScanItem.Skip(entry.FullName, NotScannedReason.ReparsePoint,
                    $"Link simbólico ou junção não seguido (aponta para {linkTarget})");
            }
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            isDirectory = true;
            return null;
        }

        if ((attributes & CloudOnlyMask) != 0)
        {
            return ScanItem.Skip(entry.FullName, NotScannedReason.CloudPlaceholder,
                "Arquivo disponível só na nuvem; não foi baixado para não gerar tráfego");
        }

        return ScanItem.ForFile(entry.FullName);
    }

    private static string? TryList(string dir, out List<FileSystemInfo> entries)
    {
        entries = [];
        try
        {
            entries = new DirectoryInfo(dir)
                .EnumerateFileSystemInfos("*", ListOptions)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return null;
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            return "Não foi possível listar a pasta: " + DescribeIoError(ex);
        }
    }

    internal static bool IsIoError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
            or System.Security.SecurityException;

    internal static string DescribeIoError(Exception ex)
    {
        const int SharingViolation = unchecked((int)0x80070020);
        const int LockViolation = unchecked((int)0x80070021);
        return ex switch
        {
            UnauthorizedAccessException => "Acesso negado",
            FileNotFoundException or DirectoryNotFoundException => "Não encontrado (removido ou movido durante a varredura?)",
            PathTooLongException => "Caminho longo demais",
            IOException io when io.HResult == SharingViolation => "Arquivo em uso por outro processo (bloqueado para leitura)",
            IOException io when io.HResult == LockViolation => "Parte do arquivo está bloqueada por outro processo",
            _ => ex.Message,
        };
    }
}
