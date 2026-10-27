using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Cocytus.Maintenance;

/// <summary>Apps da Microsoft Store do usuário atual, pela API oficial do Windows (PackageManager).</summary>
public static class StoreApps
{
    /// <summary>Apps removíveis pelo usuário: sem frameworks, pacotes de recursos e componentes do sistema.</summary>
    public static IReadOnlyList<InstalledApp> List()
    {
        var manager = new PackageManager();
        var apps = new List<InstalledApp>();
        foreach (var package in manager.FindPackagesForUser(string.Empty))
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage || package.SignatureKind == PackageSignatureKind.System)
                {
                    continue;
                }

                var name = SafeGet(() => package.DisplayName);
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                {
                    name = package.Id.Name;
                }

                var version = package.Id.Version;
                apps.Add(new InstalledApp(AppKind.Store, name, SafeGet(() => package.PublisherDisplayName),
                    $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
                    SafeGet(() => (DateTime?)package.InstalledDate.LocalDateTime), null, SafeGet(() => package.InstalledPath),
                    null, false, null, false, null, null, package.Id.FullName));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException)
            {
                // Pacote com manifesto ilegível: ignorado.
            }
        }

        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Remove o pacote do usuário atual. Devolve null em caso de sucesso ou a mensagem de erro do Windows.</summary>
    public static async Task<string?> RemoveAsync(string packageFullName, CancellationToken cancellationToken)
    {
        var manager = new PackageManager();
        var result = await manager.RemovePackageAsync(packageFullName).AsTask(cancellationToken);
        return result.ExtendedErrorCode is null ? null : $"{result.ErrorText} (0x{result.ExtendedErrorCode.HResult:X8})";
    }

    public static bool StillInstalled(string packageFullName) =>
        new PackageManager().FindPackageForUser(string.Empty, packageFullName) is not null;

    private static T? SafeGet<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException or FileNotFoundException)
        {
            return default;
        }
    }
}
