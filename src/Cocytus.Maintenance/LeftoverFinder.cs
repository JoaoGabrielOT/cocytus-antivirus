using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Cocytus.Maintenance;

public enum LeftoverKind
{
    Folder,
    RegistryKey,
}

/// <summary>Resto de um programa desinstalado.</summary>
/// <param name="Path">Pasta ou chave completa (HKCU\..., HKLM\...).</param>
/// <param name="NeedsAdmin">Pasta em Program Files/ProgramData ou chave em HKLM.</param>
public sealed record LeftoverCandidate(LeftoverKind Kind, string Path, string Reason, long? SizeBytes, bool NeedsAdmin);

/// <summary>Pastas e chaves onde procurar. Os testes trocam por pastas e chaves descartáveis.</summary>
public sealed record LeftoverRoots(
    IReadOnlyList<(string Folder, bool NeedsAdmin)> Folders,
    IReadOnlyList<(RegistryHive Hive, string Key)> RegistryKeys,
    IReadOnlyList<string> ProtectedFolders)
{
    public static LeftoverRoots Windows
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new LeftoverRoots(
                [
                    (programFiles, true),
                    (programFilesX86, true),
                    (programData, true),
                    (roaming, false),
                    (local, false),
                    (Path.Combine(local, "Programs"), false),
                    (Path.Combine(System.IO.Path.GetDirectoryName(local) ?? local, "LocalLow"), false),
                ],
                [
                    (RegistryHive.CurrentUser, "Software"),
                    (RegistryHive.LocalMachine, "SOFTWARE"),
                    (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node"),
                ],
                [
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    profile,
                    Path.Combine(profile, "Desktop"),
                    Path.Combine(profile, "Documents"),
                    Path.Combine(profile, "Downloads"),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                ]);
        }
    }
}

/// <summary>
/// Procura restos de um programa já desinstalado: só nomes exatos (nome do programa, ou Editor\Programa) nas pastas
/// e chaves de software comuns, mais a pasta de instalação registrada. Nunca sugere pastas do sistema, do perfil,
/// pastas de editores inteiras, nem pastas que contenham outro programa ainda instalado.
/// </summary>
public static partial class LeftoverFinder
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Common Files", "Microsoft Shared", "Windows NT", "Windows Defender", "WindowsApps",
        "Packages", "Temp", "Programs", "Microsoft.NET", "dotnet", "Internet Explorer", "Windows Mail", "Windows Media Player",
        "Windows Photo Viewer", "Windows Sidebar", "WindowsPowerShell", "Reference Assemblies", "Installer", "Package Cache",
        "Classes", "Policies", "Clients", "RegisteredApplications", "WOW6432Node", "Wow6432Node", "Intel", "NVIDIA Corporation",
        "AMD", "Google", "Mozilla", "Adobe", "Apple", "Oracle", "Java",
    };

    private static readonly string[] PublisherSuffixes =
    [
        ", Inc.", " Inc.", ", Inc", " Inc", " LLC", " Ltd.", " Ltd", " Limited", " Corporation", " Corp.", " Corp", " GmbH", " S.A.", " SA",
        " Co., Ltd.", " B.V.", " AB", " AG", " s.r.o.", " Software",
    ];

    public static IReadOnlyList<LeftoverCandidate> Find(InstalledApp app, IReadOnlyList<InstalledApp> stillInstalled, LeftoverRoots? roots = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(stillInstalled);
        roots ??= LeftoverRoots.Windows;
        var products = ProductNames(app.Name);
        var publishers = PublisherNames(app.Publisher);
        var otherLocations = stillInstalled
            .Where(a => !ReferenceEquals(a, app) && a.InstallLocation is not null)
            .Select(a => SafeFull(a.InstallLocation!))
            .OfType<string>()
            .ToList();

        var found = new List<LeftoverCandidate>();
        if (app.InstallLocation is { } location && SafeFull(location) is { } installFolder && Directory.Exists(installFolder))
        {
            var root = roots.Folders.FirstOrDefault(r => IsInside(installFolder, r.Folder));
            if (root.Folder is not null && IsAllowedFolder(installFolder, roots, otherLocations))
            {
                found.Add(new LeftoverCandidate(LeftoverKind.Folder, installFolder, "Pasta de instalação registrada pelo programa",
                    FolderSize(installFolder), root.NeedsAdmin));
            }
        }

        foreach (var (folder, needsAdmin) in roots.Folders)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            foreach (var product in products)
            {
                AddFolder(found, Path.Combine(folder, product), $"Pasta com o nome do programa em {folder}", needsAdmin, roots, otherLocations);
                foreach (var publisher in publishers)
                {
                    AddFolder(found, Path.Combine(folder, publisher, product), $"Pasta do editor com o nome do programa em {folder}", needsAdmin, roots, otherLocations);
                }
            }
        }

        foreach (var (hive, key) in roots.RegistryKeys)
        {
            foreach (var product in products)
            {
                AddKey(found, hive, key, [product], "Chave com o nome do programa");
                foreach (var publisher in publishers)
                {
                    AddKey(found, hive, key, [publisher, product], "Chave do editor com o nome do programa");
                }
            }
        }

        var unique = found
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Uma pasta (ou chave) dentro de outra já sugerida sai junto com ela.
        return unique.Where(c => !unique.Any(o => !ReferenceEquals(o, c) && o.Kind == c.Kind && IsInside(c.Path, o.Path))).ToList();
    }

    /// <summary>Nome exato, sem a versão no fim e sem marcações de arquitetura ("(x64)", "64-bit").</summary>
    internal static IReadOnlyList<string> ProductNames(string displayName)
    {
        var names = new List<string>();
        void Add(string value)
        {
            var trimmed = value.Trim().TrimEnd('-', ',', '.').Trim();
            if (trimmed.Length >= 3 && !ProtectedNames.Contains(trimmed) && trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && !names.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(trimmed);
            }
        }

        Add(displayName);
        var noArch = ArchitectureRegex().Replace(displayName, "");
        Add(noArch);
        Add(VersionRegex().Replace(noArch, ""));
        return names;
    }

    internal static IReadOnlyList<string> PublisherNames(string? publisher)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return names;
        }

        void Add(string value)
        {
            var trimmed = value.Trim().TrimEnd(',', '.').Trim();
            if (trimmed.Length >= 2 && trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !names.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(trimmed);
            }
        }

        Add(publisher);
        foreach (var suffix in PublisherSuffixes)
        {
            if (publisher.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                Add(publisher[..^suffix.Length]);
            }
        }

        return names;
    }

    private static void AddFolder(List<LeftoverCandidate> found, string folder, string reason, bool needsAdmin, LeftoverRoots roots, List<string> otherLocations)
    {
        if (Directory.Exists(folder) && IsAllowedFolder(folder, roots, otherLocations))
        {
            found.Add(new LeftoverCandidate(LeftoverKind.Folder, folder, reason, FolderSize(folder), needsAdmin));
        }
    }

    private static void AddKey(List<LeftoverCandidate> found, RegistryHive hive, string rootKey, string[] parts, string reason)
    {
        if (parts.Any(part => ProtectedNames.Contains(part) || part.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var key = rootKey + "\\" + string.Join('\\', parts);

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var opened = root.OpenSubKey(key, writable: false);
            if (opened is null)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return;
        }

        var prefix = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
        found.Add(new LeftoverCandidate(LeftoverKind.RegistryKey, prefix + "\\" + key, reason, null, hive != RegistryHive.CurrentUser));
    }

    /// <summary>Regras de segurança para sugerir uma pasta.</summary>
    internal static bool IsAllowedFolder(string folder, LeftoverRoots roots, IReadOnlyList<string> otherInstallLocations)
    {
        var full = SafeFull(folder);
        if (full is null || Path.GetPathRoot(full)?.TrimEnd('\\').Length >= full.TrimEnd('\\').Length)
        {
            return false;
        }

        // Precisa estar dentro de uma das pastas de software, nunca ser a própria pasta-base.
        if (!roots.Folders.Any(r => IsInside(full, r.Folder)))
        {
            return false;
        }

        if (roots.Folders.Any(r => Same(full, r.Folder)) || roots.ProtectedFolders.Any(p => Same(full, p) || IsInside(p, full)))
        {
            return false;
        }

        if (ProtectedNames.Contains(Path.GetFileName(full)))
        {
            return false;
        }

        // Pasta que é (ou contém) a instalação de outro programa: fica.
        if (otherInstallLocations.Any(other => Same(full, other) || IsInside(other, full)))
        {
            return false;
        }

        try
        {
            return !new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long? FolderSize(string folder)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return new DirectoryInfo(folder).EnumerateFiles("*", options).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? SafeFull(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool Same(string a, string b) =>
        Path.TrimEndingDirectorySeparator(a).Equals(Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    internal static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return false;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        var prefix = trimmed + "\\";
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\s*\((x64|x86|64-bit|32-bit|64 bits|32 bits|amd64|arm64)\)|\s+(x64|x86|64-bit|32-bit)$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureRegex();

    [GeneratedRegex(@"\s+v?\d+(\.\d+)+.*$|\s+v?\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
}
