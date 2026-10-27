using System.Diagnostics;
using Cocytus.Core.Scanning;
using Cocytus.Maintenance.Native;
using Microsoft.Win32;

namespace Cocytus.Maintenance;

/// <summary>Arquivos de processos e de inicialização encontrados no sistema (entrada da verificação rápida).</summary>
public sealed record QuickScanSources(
    IReadOnlyList<string> ProcessImages,
    IReadOnlyList<string> StartupFiles,
    IReadOnlyList<string> StartupFolders,
    IReadOnlyList<string> UserFolders,
    IReadOnlyList<string> AppDataFolders);

/// <summary>Monta os pedidos das verificações rápida e completa.</summary>
public static class ScanLocations
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>Lê processos em execução, itens de inicialização e pastas do usuário. Só leitura.</summary>
    public static QuickScanSources CollectQuickSources()
    {
        var startupFiles = new List<string>();
        foreach (var item in new StartupManager().List())
        {
            if (item.Source != StartupSource.Folder)
            {
                startupFiles.AddRange(CommandLines.ExtractFiles(item.Command));
            }
        }

        startupFiles.AddRange(RunOnceFiles(Registry.CurrentUser));
        startupFiles.AddRange(RunOnceFiles(Registry.LocalMachine));
        startupFiles.AddRange(AutoStartServiceFiles());

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new QuickScanSources(
            ProcessImages(),
            startupFiles,
            [Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)],
            [
                MaintenanceNative.GetDownloadsFolder() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Path.GetTempPath(),
            ],
            [
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                local,
                Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
            ]);
    }

    /// <summary>Verificação rápida: processos e inicialização (arquivos), pastas do usuário inteiras e AppData só executáveis.</summary>
    public static ScanRequest Quick(QuickScanSources sources, IReadOnlyList<string> excludedFolders, Func<string, bool>? fileExists = null, Func<string, bool>? folderExists = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        fileExists ??= File.Exists;
        folderExists ??= Directory.Exists;
        var files = sources.ProcessImages.Concat(sources.StartupFiles)
            .Where(f => !string.IsNullOrWhiteSpace(f) && fileExists(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var targets = new List<ScanTarget>();
        targets.AddRange(files.Select(f => new ScanTarget(f)));
        targets.AddRange(sources.StartupFolders.Concat(sources.UserFolders).Where(f => !string.IsNullOrWhiteSpace(f) && folderExists(f)).Select(f => new ScanTarget(f)));
        targets.AddRange(sources.AppDataFolders.Where(f => !string.IsNullOrWhiteSpace(f) && folderExists(f)).Select(f => new ScanTarget(f, ExecutablesOnly: true)));

        var description = new List<string>
        {
            $"Verificação rápida: {sources.ProcessImages.Distinct(StringComparer.OrdinalIgnoreCase).Count()} programa(s) em execução e "
                + $"{sources.StartupFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count()} arquivo(s) de inicialização",
        };
        description.AddRange(sources.StartupFolders.Concat(sources.UserFolders).Where(f => !string.IsNullOrWhiteSpace(f) && folderExists(f)));
        description.AddRange(sources.AppDataFolders.Where(f => !string.IsNullOrWhiteSpace(f) && folderExists(f)).Select(f => f + " (só executáveis e scripts)"));
        return new ScanRequest(ScanKind.Quick, targets, excludedFolders, description);
    }

    /// <summary>Unidades fixas prontas (C:\, D:\…). Pendrives, rede e unidades ópticas ficam de fora.</summary>
    public static IReadOnlyList<string> FixedDrives() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && SafeIsReady(d))
            .Select(d => d.RootDirectory.FullName)
            .ToList();

    public static ScanRequest Full(IReadOnlyList<string> drives, IReadOnlyList<string> excludedFolders)
    {
        ArgumentNullException.ThrowIfNull(drives);
        return new ScanRequest(ScanKind.Full, drives.Select(d => new ScanTarget(d)).ToList(), excludedFolders,
            ["Verificação completa: " + string.Join(", ", drives)]);
    }

    private static bool SafeIsReady(DriveInfo drive)
    {
        try
        {
            return drive.IsReady;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static List<string> ProcessImages()
    {
        var images = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id is 0 or 4)
                {
                    continue;
                }

                var path = MaintenanceNative.GetProcessImagePath(process.Id);
                if (path is not null && !images.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    images.Add(path);
                }
            }
        }

        return images;
    }

    private static IEnumerable<string> RunOnceFiles(RegistryKey hive)
    {
        const string RunOnce = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
        var files = new List<string>();
        try
        {
            using var key = hive.OpenSubKey(RunOnce, writable: false);
            if (key is null)
            {
                return files;
            }

            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is string command)
                {
                    files.AddRange(CommandLines.ExtractFiles(command));
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Sem acesso: segue sem esses itens.
        }

        return files;
    }

    /// <summary>Executáveis de serviços com início automático (não inclui drivers nem serviços hospedados no svchost).</summary>
    private static IEnumerable<string> AutoStartServiceFiles()
    {
        const int Win32OwnProcess = 0x10;
        const int Win32ShareProcess = 0x20;
        const int AutoStart = 2;
        var files = new List<string>();
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(ServicesKey, writable: false);
            if (services is null)
            {
                return files;
            }

            foreach (var name in services.GetSubKeyNames())
            {
                try
                {
                    using var service = services.OpenSubKey(name, writable: false);
                    if (service?.GetValue("Start") is not int start || start != AutoStart
                        || service.GetValue("Type") is not int type || (type & (Win32OwnProcess | Win32ShareProcess)) == 0
                        || service.GetValue("ImagePath") is not string image)
                    {
                        continue;
                    }

                    files.AddRange(CommandLines.ExtractFiles(image));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // Serviço sem permissão de leitura: ignorado.
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Sem acesso à lista de serviços.
        }

        return files;
    }
}
