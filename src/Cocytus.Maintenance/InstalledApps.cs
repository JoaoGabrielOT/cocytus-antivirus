using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Cocytus.Maintenance;

public enum AppKind
{
    /// <summary>Programa da lista de "Programas e Recursos" (registro Uninstall).</summary>
    Desktop,

    /// <summary>App da Microsoft Store (pacote MSIX/AppX do usuário).</summary>
    Store,
}

public sealed record InstalledApp(
    AppKind Kind,
    string Name,
    string? Publisher,
    string? Version,
    DateTime? InstallDate,
    long? SizeBytes,
    string? InstallLocation,
    string? UninstallString,
    bool IsMsi,
    string? ProductCode,
    bool AllUsers,
    string? RegistryHive,
    string? RegistryKey,
    string? PackageFullName)
{
    public string KindText => Kind == AppKind.Store ? "Microsoft Store" : IsMsi ? "Programa (Windows Installer)" : "Programa";

    public string ScopeText => AllUsers ? "Todos os usuários" : "Só este usuário";

    public bool CanUninstall => Kind == AppKind.Store ? PackageFullName is not null : (IsMsi && ProductCode is not null) || !string.IsNullOrWhiteSpace(UninstallString);

    /// <summary>Caminho completo da chave Uninstall (HKLM\... ou HKCU\...).</summary>
    public string? FullRegistryKey => RegistryHive is null || RegistryKey is null ? null : RegistryHive + "\\" + RegistryKey;
}

/// <summary>Onde ficam as chaves Uninstall. Os testes trocam por chaves descartáveis em HKCU.</summary>
public sealed record UninstallLocations(IReadOnlyList<(RegistryHive Hive, string Key)> Keys)
{
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static UninstallLocations Windows { get; } = new(
    [
        (RegistryHive.LocalMachine, Uninstall),
        (RegistryHive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser, Uninstall),
    ]);
}

/// <summary>Lista programas instalados com as mesmas regras de "Programas e Recursos" e monta o comando de desinstalação.</summary>
public static partial class InstalledApps
{
    public static IReadOnlyList<InstalledApp> ListDesktop(UninstallLocations? locations = null)
    {
        var apps = new List<InstalledApp>();
        foreach (var (hive, keyPath) in (locations ?? UninstallLocations.Windows).Keys)
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            RegistryKey? uninstall;
            try
            {
                uninstall = root.OpenSubKey(keyPath, writable: false);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            if (uninstall is null)
            {
                continue;
            }

            using (uninstall)
            {
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(name, writable: false);
                        if (key is not null && Read(key, hive, keyPath + "\\" + name, name) is { } app)
                        {
                            apps.Add(app);
                        }
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                        // Entrada sem permissão de leitura.
                    }
                }
            }
        }

        return apps
            .GroupBy(a => (a.Name.ToUpperInvariant(), a.Version, a.AllUsers))
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>A entrada ainda existe? (usado para saber se a desinstalação terminou).</summary>
    public static bool StillInstalled(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Kind == AppKind.Store || app.RegistryKey is null)
        {
            return false;
        }

        var hive = app.RegistryHive == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(app.RegistryKey, writable: false);
        return key is not null;
    }

    internal static InstalledApp? Read(RegistryKey key, RegistryHive hive, string keyPath, string keyName)
    {
        var name = (key.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrEmpty(name)
            || key.GetValue("SystemComponent") is int and 1
            || !string.IsNullOrEmpty(key.GetValue("ParentKeyName") as string)
            || (key.GetValue("ReleaseType") as string) is "Update" or "Hotfix" or "Security Update")
        {
            return null;
        }

        var uninstall = key.GetValue("UninstallString") as string;
        var isMsi = key.GetValue("WindowsInstaller") is int and 1;
        var productCode = ProductCodeRegex().IsMatch(keyName) ? keyName : null;
        if (string.IsNullOrWhiteSpace(uninstall) && !(isMsi && productCode is not null))
        {
            return null;
        }

        long? size = key.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;
        DateTime? installed = DateTime.TryParseExact(key.GetValue("InstallDate") as string, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;
        var location = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"');
        return new InstalledApp(AppKind.Desktop, name, (key.GetValue("Publisher") as string)?.Trim(), key.GetValue("DisplayVersion") as string,
            installed, size, string.IsNullOrWhiteSpace(location) ? null : location, uninstall, isMsi, productCode,
            hive == RegistryHive.LocalMachine, hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU", keyPath, null);
    }

    /// <summary>Executável e argumentos do desinstalador oficial. MSI usa msiexec /x com o código do produto.</summary>
    public static (string FileName, string Arguments)? UninstallCommand(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Kind != AppKind.Desktop)
        {
            return null;
        }

        var msiexec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
        var fromString = CommandLines.Split(app.UninstallString);
        var stringIsMsi = fromString is { } s && Path.GetFileNameWithoutExtension(s.FileName).Equals("msiexec", StringComparison.OrdinalIgnoreCase);
        if ((app.IsMsi || stringIsMsi) && (app.ProductCode ?? ProductCodeIn(app.UninstallString)) is { } code)
        {
            return (msiexec, "/x " + code);
        }

        return fromString;
    }

    private static string? ProductCodeIn(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var match = ProductCodeSearchRegex().Match(text);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex ProductCodeRegex();

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex ProductCodeSearchRegex();
}
