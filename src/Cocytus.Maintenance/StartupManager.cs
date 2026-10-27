using Microsoft.Win32;

namespace Cocytus.Maintenance;

public enum StartupScope
{
    /// <summary>Só o usuário atual (HKCU ou pasta Inicializar do usuário). Não precisa de administrador.</summary>
    CurrentUser,

    /// <summary>Todos os usuários (HKLM ou pasta Inicializar comum). Alterar pede administrador.</summary>
    AllUsers,
}

public enum StartupSource
{
    Registry,
    Registry32,
    Folder,
}

/// <summary>Programa que inicia com o Windows, como o Gerenciador de Tarefas mostra.</summary>
public sealed record StartupItem(
    string Name,
    string Command,
    StartupScope Scope,
    StartupSource Source,
    bool Enabled,
    string? FilePath,
    string ApprovedKey,
    string ApprovedValue)
{
    public string Origin => (Scope, Source) switch
    {
        (StartupScope.CurrentUser, StartupSource.Folder) => "Pasta Inicializar do usuário",
        (StartupScope.AllUsers, StartupSource.Folder) => "Pasta Inicializar de todos os usuários",
        (StartupScope.CurrentUser, _) => "Registro do usuário (HKCU\\…\\Run)",
        (StartupScope.AllUsers, StartupSource.Registry32) => "Registro de todos os usuários, 32 bits (HKLM\\…\\WOW6432Node\\…\\Run)",
        _ => "Registro de todos os usuários (HKLM\\…\\Run)",
    };
}

/// <summary>Onde procurar os itens. Os testes trocam por chaves e pastas descartáveis.</summary>
public sealed record StartupLocations(
    string UserRunKey,
    string MachineRunKey,
    string MachineRun32Key,
    string UserApprovedRoot,
    string MachineApprovedRoot,
    string UserStartupFolder,
    string CommonStartupFolder,
    bool IncludeMachine)
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static StartupLocations Windows { get; } = new(
        Run,
        Run,
        @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        Approved,
        Approved,
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        IncludeMachine: true);
}

/// <summary>
/// Lista e ativa/desativa programas de inicialização do mesmo jeito que o Gerenciador de Tarefas: o item continua
/// onde está e o estado fica em ...\Explorer\StartupApproved (reversível). Itens de todos os usuários são alterados
/// por reg.exe elevado (o Windows pede a confirmação de administrador).
/// </summary>
public sealed class StartupManager
{
    private readonly StartupLocations _locations;

    public StartupManager(StartupLocations? locations = null)
    {
        _locations = locations ?? StartupLocations.Windows;
    }

    public IReadOnlyList<StartupItem> List()
    {
        var items = new List<StartupItem>();
        ReadRunKey(items, Registry.CurrentUser, _locations.UserRunKey, StartupScope.CurrentUser, StartupSource.Registry,
            Registry.CurrentUser, _locations.UserApprovedRoot + @"\Run");
        if (_locations.IncludeMachine)
        {
            ReadRunKey(items, Registry.LocalMachine, _locations.MachineRunKey, StartupScope.AllUsers, StartupSource.Registry,
                Registry.LocalMachine, _locations.MachineApprovedRoot + @"\Run");
            ReadRunKey(items, Registry.LocalMachine, _locations.MachineRun32Key, StartupScope.AllUsers, StartupSource.Registry32,
                Registry.LocalMachine, _locations.MachineApprovedRoot + @"\Run32");
        }

        ReadFolder(items, _locations.UserStartupFolder, StartupScope.CurrentUser, Registry.CurrentUser, _locations.UserApprovedRoot + @"\StartupFolder");
        if (_locations.IncludeMachine)
        {
            ReadFolder(items, _locations.CommonStartupFolder, StartupScope.AllUsers, Registry.LocalMachine, _locations.MachineApprovedRoot + @"\StartupFolder");
        }

        return items;
    }

    /// <summary>Ativa ou desativa. Para itens de todos os usuários, devolve false se a elevação foi recusada.</summary>
    public bool SetEnabled(StartupItem item, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(item);
        var data = ApprovedData(enabled, DateTime.UtcNow);
        if (item.Scope == StartupScope.CurrentUser)
        {
            using var key = Registry.CurrentUser.CreateSubKey(item.ApprovedKey, writable: true);
            key.SetValue(item.ApprovedValue, data, RegistryValueKind.Binary);
            return true;
        }

        return ElevatedRegistry.SetBinary(@"HKLM\" + item.ApprovedKey, item.ApprovedValue, data);
    }

    /// <summary>Formato do Gerenciador de Tarefas: 12 bytes; o bit 0 do primeiro indica "desativado"; depois, a data.</summary>
    internal static byte[] ApprovedData(bool enabled, DateTime utcNow)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled)
        {
            BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(data, 4);
        }

        return data;
    }

    internal static bool IsEnabled(object? approvedValue) =>
        approvedValue is not byte[] { Length: > 0 } bytes || (bytes[0] & 0x01) == 0;

    private static void ReadRunKey(List<StartupItem> items, RegistryKey hive, string runKey, StartupScope scope, StartupSource source,
        RegistryKey approvedHive, string approvedKey)
    {
        using var key = OpenRead(hive, runKey);
        if (key is null)
        {
            return;
        }

        using var approved = OpenRead(approvedHive, approvedKey);
        foreach (var name in SafeValueNames(key))
        {
            if (string.IsNullOrEmpty(name) || key.GetValue(name) is not string command)
            {
                continue;
            }

            var file = CommandLines.ExtractFiles(command).FirstOrDefault();
            items.Add(new StartupItem(name, command, scope, source, IsEnabled(approved?.GetValue(name)), file, approvedKey, name));
        }
    }

    private static void ReadFolder(List<StartupItem> items, string folder, StartupScope scope, RegistryKey approvedHive, string approvedKey)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        using var approved = OpenRead(approvedHive, approvedKey);
        IEnumerable<string> files;
        try
        {
            files = Directory.GetFiles(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add(new StartupItem(Path.GetFileNameWithoutExtension(file), file, scope, StartupSource.Folder,
                IsEnabled(approved?.GetValue(fileName)), file, approvedKey, fileName));
        }
    }

    private static RegistryKey? OpenRead(RegistryKey hive, string path)
    {
        try
        {
            return hive.OpenSubKey(path, writable: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string[] SafeValueNames(RegistryKey key)
    {
        try
        {
            return key.GetValueNames();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}
