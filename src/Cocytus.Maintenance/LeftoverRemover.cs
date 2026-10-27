using System.Globalization;
using Microsoft.Win32;

namespace Cocytus.Maintenance;

/// <summary>Remove chaves de registro que sobraram, sempre com cópia de segurança (.reg) antes.</summary>
public static class LeftoverRemover
{
    /// <summary>
    /// Exporta a chave para <paramref name="backupFolder"/> e a apaga. Chaves em HKLM são apagadas pelo reg.exe elevado.
    /// Devolve o arquivo de cópia, ou null se o usuário recusou a elevação (nada foi apagado).
    /// </summary>
    public static string? BackupAndDeleteKey(string fullKey, string backupFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullKey);
        var safeName = string.Concat(fullKey.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safeName.Length > 120)
        {
            safeName = safeName[^120..];
        }

        var backup = Path.Combine(backupFolder, $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{safeName}.reg");
        ElevatedRegistry.Export(fullKey, backup);
        if (fullKey.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
        {
            Registry.CurrentUser.DeleteSubKeyTree(fullKey[5..], throwOnMissingSubKey: false);
            return backup;
        }

        return ElevatedRegistry.DeleteKey(fullKey) ? backup : null;
    }
}
