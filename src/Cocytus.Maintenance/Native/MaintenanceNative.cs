using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cocytus.Maintenance.Native;

internal static partial class MaintenanceNative
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ShErbNoConfirmation = 0x1;
    private const int EUnexpected = unchecked((int)0x8000FFFF);
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>Caminho do executável de um processo (funciona também para a maioria dos processos elevados).</summary>
    internal static unsafe string? GetProcessImagePath(int processId)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[32_768];
        var size = (uint)buffer.Length;
        fixed (char* pointer = buffer)
        {
            return QueryFullProcessImageNameW(handle, 0, pointer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
    }

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

    internal static (long Items, long Bytes) QueryRecycleBin()
    {
        var info = new ShQueryRbInfo { Size = (uint)Marshal.SizeOf<ShQueryRbInfo>() };
        var hr = SHQueryRecycleBinW(null, ref info);
        Marshal.ThrowExceptionForHR(hr);
        return (info.NumItems, info.TotalBytes);
    }

    /// <summary>Esvazia a Lixeira de todas as unidades. A confirmação é feita antes, pelo Cocytus.</summary>
    internal static void EmptyRecycleBin(IntPtr owner)
    {
        var hr = SHEmptyRecycleBinW(owner, null, ShErbNoConfirmation);
        if (hr != EUnexpected)
        {
            // E_UNEXPECTED só indica Lixeira já vazia.
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    internal static (ulong Total, ulong Available, uint LoadPercent) GetMemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        return (status.TotalPhys, status.AvailPhys, status.MemoryLoad);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public uint Size;
        public long TotalBytes;
        public long NumItems;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBinW(string? rootPath, ref ShQueryRbInfo info);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);
}
