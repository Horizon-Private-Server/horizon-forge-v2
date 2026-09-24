using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Forge.Host.Domain;

internal readonly record struct FileIdentity(ulong Device, ulong File)
{
    private const int AtEmptyPath = 0x1000;
    private const uint StatxIno = 0x100;

    public static FileIdentity Read(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (OperatingSystem.IsWindows()) return ReadWindows(handle);
        if (OperatingSystem.IsLinux()) return ReadLinux(handle);
        throw new PlatformNotSupportedException("Safe ISO file-identity checks currently support Linux and Windows.");
    }

    private static FileIdentity ReadLinux(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (Statx(checked((int)handle.DangerousGetHandle()), string.Empty, AtEmptyPath, StatxIno, buffer) != 0)
                throw new IOException("Could not identify the ISO filesystem object.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            var inode = unchecked((ulong)Marshal.ReadInt64(buffer, 0x20));
            var device = (ulong)(uint)Marshal.ReadInt32(buffer, 0x88) << 32
                | (uint)Marshal.ReadInt32(buffer, 0x8c);
            return new(device, inode);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static FileIdentity ReadWindows(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException("Could not identify the ISO filesystem object.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        return new(
            information.VolumeSerialNumber,
            (ulong)information.FileIndexHigh << 32 | information.FileIndexLow);
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, IntPtr buffer);

    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
