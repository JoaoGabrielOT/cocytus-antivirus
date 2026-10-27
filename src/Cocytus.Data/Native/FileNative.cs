using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cocytus.Data.Native;

internal static partial class FileNative
{
    internal const uint GenericRead = 0x80000000;
    internal const uint Delete = 0x00010000;
    internal const uint FileWriteAttributes = 0x00000100;
    internal const uint FileShareRead = 0x1;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagSequentialScan = 0x08000000;

    private const int FileDispositionInfoClass = 4;
    private const int FileDispositionInfoExClass = 21;
    private const uint FileDispositionFlagDelete = 0x1;
    private const uint FileDispositionFlagPosixSemantics = 0x2;
    private const uint FileDispositionFlagIgnoreReadonlyAttribute = 0x10;

    /// <summary>
    /// Abre o arquivo para leitura e com direito de exclusão, negando escrita e exclusão a outros
    /// processos enquanto o handle estiver aberto.
    /// </summary>
    internal static SafeFileHandle OpenForQuarantine(string fullPath)
    {
        var handle = CreateFileW(ToExtendedPath(fullPath), GenericRead | Delete | FileWriteAttributes, FileShareRead,
            IntPtr.Zero, OpenExisting, FileFlagSequentialScan, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(new Win32Exception(error).Message, HResultFromWin32(error));
        }

        return handle;
    }

    /// <summary>Marca o arquivo para exclusão; ela acontece quando o handle é fechado.</summary>
    internal static void MarkForDeletion(SafeFileHandle handle)
    {
        var ex = new FileDispositionInfoEx
        {
            Flags = FileDispositionFlagDelete | FileDispositionFlagPosixSemantics | FileDispositionFlagIgnoreReadonlyAttribute,
        };
        if (SetFileInformationByHandleEx(handle, FileDispositionInfoExClass, ref ex, (uint)Marshal.SizeOf<FileDispositionInfoEx>()))
        {
            return;
        }

        // Sistemas de arquivos sem suporte à versão estendida (FAT/exFAT): tira o "somente leitura" e usa a clássica.
        var attributes = File.GetAttributes(handle);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(handle, attributes & ~FileAttributes.ReadOnly);
        }

        var legacy = new FileDispositionInfo { DeleteFile = 1 };
        if (!SetFileInformationByHandleLegacy(handle, FileDispositionInfoClass, ref legacy, (uint)Marshal.SizeOf<FileDispositionInfo>()))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(new Win32Exception(error).Message, HResultFromWin32(error));
        }
    }

    internal static string ToExtendedPath(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
    }

    private static int HResultFromWin32(int error) => error <= 0 ? error : unchecked((int)(((uint)error & 0x0000FFFF) | 0x80070000));

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfoEx
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, ref FileDispositionInfoEx info, uint dwBufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandleLegacy(SafeFileHandle hFile, int fileInformationClass, ref FileDispositionInfo info, uint dwBufferSize);
}
