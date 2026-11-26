using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace IbraDriveCheck.Core.Devices.Windows;

/// <summary>
/// وصول مباشر لقرص فيزيائي \\.\PhysicalDriveN بدون كاش ويندوز (يتطلب صلاحيات مسؤول).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class RawDiskDevice : IBlockDevice
{
    private readonly SafeFileHandle _h;
    public int DiskNumber { get; }
    public long Size { get; }
    public int SectorSize { get; }
    public int Alignment { get; }
    public bool CanWrite { get; }

    private RawDiskDevice(SafeFileHandle h, int disk, long size, int sector, int physical, bool write)
    {
        _h = h;
        DiskNumber = disk;
        Size = size;
        SectorSize = sector;
        Alignment = Math.Max(4096, Math.Max(sector, physical));
        CanWrite = write;
    }

    internal SafeFileHandle Handle => _h;

    public static RawDiskDevice Open(int diskNumber, bool write) => OpenPath($@"\\.\PhysicalDrive{diskNumber}", write, diskNumber);

    /// <summary>يفتح قسماً بحرفه (مثل "E:") للقراءة/الكتابة المباشرة.</summary>
    public static RawDiskDevice OpenVolume(string letter, bool write) => OpenPath($@"\\.\{letter.TrimEnd('\\').TrimEnd(':')}:", write, -1);

    public static RawDiskDevice OpenPath(string path, bool write, int diskNumber)
    {
        uint access = Native.GENERIC_READ | (write ? Native.GENERIC_WRITE : 0);
        var h = Native.Open(path, access, Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            if (err == Native.ERROR_ACCESS_DENIED) throw new UnauthorizedAccessException("Administrator rights are required for direct disk access.");
            if (Native.IsGoneError(err)) throw new DeviceGoneException(err);
            throw new IOException($"Cannot open {path} (code {err})");
        }

        long size = 0;
        var len = new byte[8];
        if (Native.Ioctl(h, Native.IOCTL_DISK_GET_LENGTH_INFO, null, len, out _)) size = BitConverter.ToInt64(len, 0);
        var geo = new byte[256];
        int sector = 512;
        if (Native.Ioctl(h, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, geo, out _))
        {
            sector = Math.Max(512, BitConverter.ToInt32(geo, 20));
            if (size == 0) size = BitConverter.ToInt64(geo, 24);
        }
        int physical = sector;
        var align = new byte[64];
        if (Native.Ioctl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, Native.PropertyQuery(6), align, out int ret) && ret >= 24)
            physical = Math.Max(sector, BitConverter.ToInt32(align, 20));
        if (size <= 0)
        {
            h.Dispose();
            throw new IOException("No media / zero size");
        }
        return new RawDiskDevice(h, diskNumber, size, sector, physical, write);
    }

    public void Read(long offset, Span<byte> buffer) => Io(offset, buffer, write: false);
    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite) throw new InvalidOperationException("Opened read-only");
        Io(offset, MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(buffer), buffer.Length), write: true);
    }

    private void Io(long offset, Span<byte> buffer, bool write)
    {
        if (offset % SectorSize != 0 || buffer.Length % SectorSize != 0)
            throw new ArgumentException("Unaligned I/O");
        if (offset + buffer.Length > Size)
            throw new BlockIoException(offset, write, 38, "Beyond end of device");
        fixed (byte* p = buffer)
        {
            var ov = new NativeOverlapped
            {
                OffsetLow = (int)(offset & 0xFFFFFFFF),
                OffsetHigh = (int)(offset >> 32),
            };
            int done;
            bool ok = write
                ? Native.WriteFile(_h, p, buffer.Length, out done, &ov)
                : Native.ReadFile(_h, p, buffer.Length, out done, &ov);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (Native.IsGoneError(err)) throw new DeviceGoneException(err);
                throw new BlockIoException(offset, write, err);
            }
            if (done != buffer.Length) throw new BlockIoException(offset, write, 0, "Short transfer");
        }
    }

    public void Flush()
    {
        if (CanWrite && !Native.FlushFileBuffers(_h))
        {
            int err = Marshal.GetLastWin32Error();
            if (Native.IsGoneError(err)) throw new DeviceGoneException(err);
        }
    }

    /// <summary>يطلب من ويندوز إعادة قراءة جدول الأقسام (بعد الفحص المدمر).</summary>
    public void UpdateProperties() => Native.IoctlNoData(_h, Native.IOCTL_DISK_UPDATE_PROPERTIES);

    public void Dispose() => _h.Dispose();
}
