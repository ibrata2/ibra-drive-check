using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IbraDriveCheck.Core.Devices.Windows;

[SupportedOSPlatform("windows")]
internal static unsafe class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x700A0;
    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x7405C;
    public const uint IOCTL_DISK_IS_WRITABLE = 0x70024;
    public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x70050;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x70140;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x560000;
    public const uint FSCTL_LOCK_VOLUME = 0x90018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x9001C;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x90020;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_WRITE_PROTECT = 19;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_DEV_NOT_EXIST = 55;
    public const int ERROR_NO_SUCH_DEVICE = 433;
    public const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    public const int ERROR_MEDIA_CHANGED = 1110;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle h, byte* buffer, int count, out int read, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandle h, byte* buffer, int count, out int written, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FlushFileBuffers(SafeFileHandle h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindFirstVolumeW(StringBuilder name, int len);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool FindNextVolumeW(IntPtr h, StringBuilder name, int len);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FindVolumeClose(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumePathNamesForVolumeNameW(string volume, char[] buffer, int len, out int returned);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumeInformationW(string root, StringBuilder label, int labelLen, out uint serial, out uint maxComp, out uint flags, StringBuilder fs, int fsLen);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceExW(string root, out ulong freeToCaller, out ulong total, out ulong totalFree);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetDriveTypeW(string root);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, int len, uint flags);

    [DllImport("shell32.dll")]
    public static extern bool IsUserAnAdmin();

    public static SafeFileHandle Open(string path, uint access, uint flags = 0)
        => CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);

    public static bool Ioctl(SafeFileHandle h, uint code, byte[]? input, byte[] output, out int returned)
    {
        fixed (byte* i = input)
        fixed (byte* o = output)
            return DeviceIoControl(h, code, i, input?.Length ?? 0, o, output.Length, out returned, IntPtr.Zero);
    }

    public static bool IoctlNoData(SafeFileHandle h, uint code)
        => DeviceIoControl(h, code, null, 0, null, 0, out _, IntPtr.Zero);

    public static byte[] PropertyQuery(int propertyId)
    {
        var q = new byte[12];
        BitConverter.GetBytes(propertyId).CopyTo(q, 0);   // PropertyId
        BitConverter.GetBytes(0).CopyTo(q, 4);            // PropertyStandardQuery
        return q;
    }

    public static string? AnsiAt(byte[] buf, int offset)
    {
        if (offset <= 0 || offset >= buf.Length) return null;
        int end = Array.IndexOf(buf, (byte)0, offset);
        if (end < 0) end = buf.Length;
        var s = Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
        return s.Length == 0 ? null : s;
    }

    public static bool IsGoneError(int err) => err is ERROR_DEVICE_NOT_CONNECTED or ERROR_NO_SUCH_DEVICE or ERROR_DEV_NOT_EXIST
        or ERROR_NOT_READY or ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_MEDIA_CHANGED;
}
