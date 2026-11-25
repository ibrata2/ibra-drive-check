using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Devices.Windows;

/// <summary>يكتشف وحدات التخزين المتصلة ويجمع معلوماتها من ويندوز (IOCTL + WMI + PnP).</summary>
[SupportedOSPlatform("windows")]
public static class DriveEnumerator
{
    private static readonly string[] BusNames =
    {
        "Unknown", "SCSI", "ATAPI", "ATA", "IEEE1394", "SSA", "Fibre", "USB", "RAID", "iSCSI",
        "SAS", "SATA", "SD", "MMC", "Virtual", "FileBackedVirtual", "Spaces", "NVMe", "SCM", "UFS",
    };

    private static readonly Guid GptMsr = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    private static readonly Guid GptRecovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");

    public static bool IsElevated
    {
        get { try { return Native.IsUserAnAdmin(); } catch { return false; } }
    }

    public static List<DriveSnapshot> Enumerate(bool includeWmi = true)
    {
        var volumes = EnumerateVolumes();
        int systemDisk = SystemDiskNumber(volumes);
        var wmiDisks = includeWmi ? Wmi.Query(@"root\cimv2", "SELECT Index, Model, Manufacturer, SerialNumber, FirmwareRevision, PNPDeviceID, InterfaceType, MediaType FROM Win32_DiskDrive") : new();
        var msftDisks = includeWmi ? Wmi.Query(@"root\Microsoft\Windows\Storage", "SELECT Number, IsSystem, IsBoot, PartitionStyle FROM MSFT_Disk") : new();

        var list = new List<DriveSnapshot>();
        for (int n = 0; n < 64; n++)
        {
            var d = Query(n, volumes);
            if (d == null) continue;
            d.IsSystemDisk |= n == systemDisk;

            var w = wmiDisks.FirstOrDefault(x => x.Long("Index") == n);
            if (w != null)
            {
                d.Model ??= w.Str("Model");
                d.Manufacturer = Clean(w.Str("Manufacturer"));
                d.SerialNumber ??= w.Str("SerialNumber")?.Trim();
                d.Firmware ??= w.Str("FirmwareRevision");
                d.PnpDeviceId = w.Str("PNPDeviceID");
                if (d.Model != null) d.FriendlyName = d.Model;
            }
            var m = msftDisks.FirstOrDefault(x => x.Long("Number") == n);
            if (m != null)
            {
                if (m.Bool("IsSystem") == true || m.Bool("IsBoot") == true) d.IsSystemDisk = true;
                d.PartitionStyle ??= m.Long("PartitionStyle") switch { 1 => "MBR", 2 => "GPT", 0 => "RAW", _ => null };
            }
            if (d.PnpDeviceId != null) FillUsbIds(d);
            d.Kind = Classify(d);
            list.Add(d);
        }
        return list;
    }

    /// <summary>يقرأ معلومات قرص واحد مباشرة (يستخدم أيضاً للتحقق من الهوية قبل الكتابة).</summary>
    public static DriveSnapshot? Query(int n, List<VolumeEntry>? volumes = null)
    {
        using var h = Native.Open($@"\\.\PhysicalDrive{n}", 0);
        if (h.IsInvalid) return null;
        var d = new DriveSnapshot { DiskNumber = n, DevicePath = $@"\\.\PhysicalDrive{n}" };

        var desc = new byte[1024];
        if (Native.Ioctl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, Native.PropertyQuery(0), desc, out int ret) && ret >= 32)
        {
            d.Removable = desc[10] != 0;
            string? vendor = Native.AnsiAt(desc, BitConverter.ToInt32(desc, 12));
            string? product = Native.AnsiAt(desc, BitConverter.ToInt32(desc, 16));
            d.Firmware = Native.AnsiAt(desc, BitConverter.ToInt32(desc, 20));
            d.SerialNumber = Native.AnsiAt(desc, BitConverter.ToInt32(desc, 24));
            d.HardwareSerial = d.SerialNumber;
            int bus = BitConverter.ToInt32(desc, 28);
            d.BusType = bus >= 0 && bus < BusNames.Length ? BusNames[bus] : $"Bus{bus}";
            d.Manufacturer = Clean(vendor);
            d.Model = product;
            d.HardwareProduct = product;
            d.FriendlyName = string.Join(" ", new[] { vendor, product }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(d.FriendlyName)) d.FriendlyName = null;
        }

        var geo = new byte[256];
        if (Native.Ioctl(h, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, geo, out _))
        {
            int mediaType = BitConverter.ToInt32(geo, 8);
            d.SectorSize = Math.Max(512, BitConverter.ToInt32(geo, 20));
            d.DeclaredBytes = BitConverter.ToInt64(geo, 24);
            if (mediaType == 11) d.Removable = true;
        }
        else
        {
            return null; // لا يوجد وسيط (قارئ بطاقات فارغ)
        }
        if (d.DeclaredBytes <= 0) return null;

        var align = new byte[64];
        if (Native.Ioctl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, Native.PropertyQuery(6), align, out ret) && ret >= 24)
            d.PhysicalSectorSize = BitConverter.ToInt32(align, 20);
        var seek = new byte[16];
        if (Native.Ioctl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, Native.PropertyQuery(7), seek, out ret) && ret >= 9)
            d.SeekPenalty = seek[8] != 0;

        if (Native.IoctlNoData(h, Native.IOCTL_DISK_IS_WRITABLE)) d.WriteProtected = false;
        else if (Marshal.GetLastWin32Error() == Native.ERROR_WRITE_PROTECT) d.WriteProtected = true;

        ReadLayout(n, d);

        volumes ??= EnumerateVolumes();
        foreach (var v in volumes.Where(v => v.DiskNumbers.Contains(n) && v.Letter != null))
        {
            d.Volumes.Add(new VolumeSnapshot { Letter = v.Letter!, Label = v.Label, FileSystem = v.FileSystem, TotalBytes = v.Total, FreeBytes = v.Free });
            if (v.HasWindows) d.HasProtectedPartitions = true;
        }
        return d;
    }

    private static void ReadLayout(int n, DriveSnapshot d)
    {
        // يحتاج صلاحية قراءة؛ بدون صلاحيات مسؤول قد يفشل، وعندها نعتمد على معلومات الأقسام من WMI
        using var h = Native.Open($@"\\.\PhysicalDrive{n}", Native.GENERIC_READ);
        if (h.IsInvalid) return;
        var buf = new byte[48 + 144 * 128];
        if (!Native.Ioctl(h, Native.IOCTL_DISK_GET_DRIVE_LAYOUT_EX, null, buf, out _)) return;
        int style = BitConverter.ToInt32(buf, 0);
        int count = BitConverter.ToInt32(buf, 4);
        d.PartitionStyle = style switch { 0 => "MBR", 1 => "GPT", _ => "RAW" };
        for (int i = 0; i < Math.Min(count, 128); i++)
        {
            int p = 48 + i * 144;
            int pstyle = BitConverter.ToInt32(buf, p);
            long length = BitConverter.ToInt64(buf, p + 16);
            if (length == 0) continue;
            if (pstyle == 1)
            {
                var type = new Guid(buf.AsSpan(p + 32, 16));
                if (type == GptMsr || type == GptRecovery) d.HasProtectedPartitions = true;
            }
            else if (pstyle == 0)
            {
                byte t = buf[p + 32];
                if (t == 0x27) d.HasProtectedPartitions = true;   // قسم استعادة ويندوز
            }
        }
    }

    public sealed class VolumeEntry
    {
        public string Guid = "";
        public string? Letter;
        public string Label = "";
        public string FileSystem = "";
        public long Total;
        public long Free;
        public bool HasWindows;
        public List<int> DiskNumbers = new();
    }

    public static List<VolumeEntry> EnumerateVolumes()
    {
        var list = new List<VolumeEntry>();
        var sb = new StringBuilder(1024);
        IntPtr fh = Native.FindFirstVolumeW(sb, sb.Capacity);
        if (fh == new IntPtr(-1)) return list;
        try
        {
            do
            {
                string vol = sb.ToString();              // \\?\Volume{guid}\
                var e = new VolumeEntry { Guid = vol };
                var paths = new char[1024];
                if (Native.GetVolumePathNamesForVolumeNameW(vol, paths, paths.Length, out _))
                {
                    var first = new string(paths).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(p => p.Length == 3 && p[1] == ':');
                    if (first != null) e.Letter = first[..2].ToUpperInvariant();
                }
                var label = new StringBuilder(261);
                var fs = new StringBuilder(261);
                if (Native.GetVolumeInformationW(vol, label, label.Capacity, out _, out _, out _, fs, fs.Capacity))
                {
                    e.Label = label.ToString();
                    e.FileSystem = fs.ToString();
                }
                if (Native.GetDiskFreeSpaceExW(vol, out ulong freeCaller, out ulong total, out _))
                {
                    e.Total = (long)total;
                    e.Free = (long)freeCaller;
                }
                try { e.HasWindows = Directory.Exists(Path.Combine(vol, @"Windows\System32\config")); } catch { }

                using (var h = Native.Open(vol.TrimEnd('\\'), 0))
                {
                    if (!h.IsInvalid)
                    {
                        var ext = new byte[8 + 24 * 32];
                        if (Native.Ioctl(h, Native.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, ext, out _))
                        {
                            int cnt = BitConverter.ToInt32(ext, 0);
                            for (int i = 0; i < Math.Min(cnt, 32); i++)
                                e.DiskNumbers.Add(BitConverter.ToInt32(ext, 8 + i * 24));
                        }
                    }
                }
                list.Add(e);
                sb.Clear();
            } while (Native.FindNextVolumeW(fh, sb, sb.Capacity));
        }
        finally
        {
            Native.FindVolumeClose(fh);
        }
        return list;
    }

    private static int SystemDiskNumber(List<VolumeEntry> volumes)
    {
        string sys = Path.GetPathRoot(Environment.SystemDirectory)?.Substring(0, 2).ToUpperInvariant() ?? "C:";
        var v = volumes.FirstOrDefault(x => x.Letter == sys);
        return v != null && v.DiskNumbers.Count > 0 ? v.DiskNumbers[0] : -1;
    }

    private static void FillUsbIds(DriveSnapshot d)
    {
        try
        {
            if (Native.CM_Locate_DevNodeW(out uint dev, d.PnpDeviceId!, 0) != 0) return;
            for (int i = 0; i < 4; i++)
            {
                if (Native.CM_Get_Parent(out uint parent, dev, 0) != 0) return;
                var buf = new char[512];
                if (Native.CM_Get_Device_IDW(parent, buf, buf.Length, 0) != 0) return;
                string id = new string(buf).TrimEnd('\0');
                var m = Regex.Match(id, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    d.VendorId = m.Groups[1].Value.ToUpperInvariant();
                    d.ProductId = m.Groups[2].Value.ToUpperInvariant();
                    return;
                }
                dev = parent;
            }
        }
        catch { }
    }

    private static MediaKind Classify(DriveSnapshot d)
    {
        string name = $"{d.Manufacturer} {d.Model}".ToUpperInvariant();
        switch (d.BusType)
        {
            case "SD":
            case "MMC":
                return MediaKind.SdCard;
            case "USB":
                if (d.Removable == true)
                    return name.Contains("SD") || name.Contains("CARD") || name.Contains("READER") || name.Contains("MMC") ? MediaKind.SdCard : MediaKind.UsbFlash;
                return d.SeekPenalty == false ? MediaKind.ExternalSsd : d.SeekPenalty == true ? MediaKind.ExternalHdd : MediaKind.ExternalHdd;
            case "Virtual":
            case "FileBackedVirtual":
                return MediaKind.Virtual;
            default:
                return d.SeekPenalty == true ? MediaKind.InternalHdd : MediaKind.InternalSsd;
        }
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Equals("(Standard disk drives)", StringComparison.OrdinalIgnoreCase) ? null : s;
    }
}
