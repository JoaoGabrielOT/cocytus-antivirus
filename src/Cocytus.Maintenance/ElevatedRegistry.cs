using System.ComponentModel;
using System.Diagnostics;

namespace Cocytus.Maintenance;

/// <summary>
/// Alterações no registro de todos os usuários (HKLM). O Cocytus não roda como administrador: a alteração é feita
/// pelo reg.exe do Windows, iniciado com pedido de elevação (o usuário confirma no aviso do Windows).
/// </summary>
public static class ElevatedRegistry
{
    private const int ErrorCancelled = 1223;

    public static bool SetBinary(string fullKey, string valueName, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var hex = Convert.ToHexString(data);
        return RunElevated($"add {Quote(fullKey)} /v {Quote(valueName)} /t REG_BINARY /d {hex} /f");
    }

    public static bool DeleteKey(string fullKey) => RunElevated($"delete {Quote(fullKey)} /f");

    /// <summary>Exporta uma chave para um arquivo .reg (cópia de segurança). Não precisa de administrador.</summary>
    public static void Export(string fullKey, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var start = new ProcessStartInfo(RegExe, $"export {Quote(fullKey)} {Quote(file)} /y")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(start) ?? throw new IOException("Não foi possível iniciar o reg.exe.");
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException($"Falha ao exportar {fullKey}: {error.Trim()}");
        }
    }

    private static string RegExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");

    private static bool RunElevated(string arguments)
    {
        var start = new ProcessStartInfo(RegExe, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Não foi possível iniciar o reg.exe.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new IOException($"O reg.exe terminou com código {process.ExitCode}.");
            }

            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    internal static string Quote(string value)
    {
        if (value.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("Nome com aspas não é suportado: " + value, nameof(value));
        }

        // Barra invertida no fim escaparia a aspa de fechamento.
        return value.EndsWith('\\') ? $"\"{value}\\\"" : $"\"{value}\"";
    }
}
