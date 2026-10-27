using Cocytus.Core;
using Microsoft.Win32;

namespace Cocytus.App.Services;

/// <summary>
/// Integração com o Windows no registro do usuário atual (HKCU, sem administrador): opção "Verificar com o Cocytus"
/// no menu do botão direito e início automático na bandeja. Tudo é removível pelos botões das Configurações.
/// No Windows 11, verbos registrados assim aparecem em "Mostrar mais opções" (menu clássico).
/// </summary>
public static class WindowsIntegration
{
    private const string VerbName = "Cocytus.Verificar";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Cocytus";
    private const string ApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private static readonly string[] ShellRoots =
    [
        @"Software\Classes\*\shell",
        @"Software\Classes\Directory\shell",
        @"Software\Classes\Drive\shell",
    ];

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Cocytus.exe");

    private static string DataArgument(CocytusPaths paths) => paths.UsesCustomDataRoot ? $" --dados \"{paths.DataRoot}\"" : "";

    public static string ContextMenuCommand(CocytusPaths paths) => $"\"{ExePath}\"{DataArgument(paths)} --verificar \"%1\"";

    public static string StartupCommand(CocytusPaths paths) => $"\"{ExePath}\"{DataArgument(paths)} --bandeja";

    public static string? GetContextMenuCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ShellRoots[0]}\{VerbName}\command");
        return key?.GetValue(null) as string;
    }

    public static void InstallContextMenu(CocytusPaths paths)
    {
        foreach (var root in ShellRoots)
        {
            using var verb = Registry.CurrentUser.CreateSubKey($@"{root}\{VerbName}");
            verb.SetValue(null, "Verificar com o Cocytus");
            verb.SetValue("Icon", $"{ExePath},0");
            // Sem o limite de 15 itens do modelo padrão; cada item chega à instância aberta, que junta tudo numa verificação.
            verb.SetValue("MultiSelectModel", "Player");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, ContextMenuCommand(paths));
        }
    }

    public static void RemoveContextMenu()
    {
        foreach (var root in ShellRoots)
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{VerbName}", throwOnMissingSubKey: false);
        }
    }

    public static string? GetStartupCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) as string;
    }

    public static void EnableStartup(CocytusPaths paths)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(RunValue, StartupCommand(paths));
        if (StartupDisabledByUser())
        {
            // Estava desativado na inicialização (Gerenciador de Tarefas ou tela Otimização): ativar vale para os dois.
            using var approved = Registry.CurrentUser.CreateSubKey(ApprovedRunKey);
            approved.SetValue(RunValue, new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }
    }

    /// <summary>O item existe no Run, mas está marcado como desativado em StartupApproved.</summary>
    public static bool StartupDisabledByUser()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedRunKey);
        return key?.GetValue(RunValue) is byte[] { Length: > 0 } data && (data[0] & 0x01) == 1;
    }

    public static void DisableStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(RunValue, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedRunKey, writable: true);
        approved?.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// <summary>Texto de estado: instalado para este executável, instalado para outro local ou ausente.</summary>
    public static string Describe(string? registered, string expected)
    {
        if (registered is null)
        {
            return "Desativado.";
        }

        return string.Equals(registered, expected, StringComparison.OrdinalIgnoreCase)
            ? "Ativado para este Cocytus."
            : $"Ativado, mas aponta para outro local ou pasta de dados: {registered}";
    }
}
