using System.Runtime.InteropServices;

namespace Cocytus.App.Services;

internal static partial class NativeMethods
{
    private const int AsfwAny = -1;
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>Permite que a instância já aberta traga a janela para a frente (chamado pela instância que vai encerrar).</summary>
    internal static void AllowAnyProcessToSetForeground() => AllowSetForegroundWindow(AsfwAny);

    /// <summary>Pasta Downloads real do usuário (pode ter sido movida para outro disco).</summary>
    internal static string? GetDownloadsFolder()
    {
        if (SHGetKnownFolderPath(in FolderIdDownloads, 0, IntPtr.Zero, out var pointer) != 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int dwProcessId);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
