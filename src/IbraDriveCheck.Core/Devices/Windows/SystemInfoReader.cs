using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Devices.Windows;

/// <summary>مواصفات الحاسبة الحالية وسرعة منفذ PCIe للوحدة (لقسم التوافق في تقرير الشراء).</summary>
[SupportedOSPlatform("windows")]
public static class SystemInfoReader
{
    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(out int type);

    public static SystemSnapshot Read(DriveSnapshot? drive)
    {
        var s = new SystemSnapshot { ComputerName = Environment.MachineName };
        var os = Wmi.Query(@"root\cimv2", "SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem").FirstOrDefault();
        if (os != null) s.Os = $"{os.Str("Caption")} ({os.Str("Version")})".Trim();
        var cpu = Wmi.Query(@"root\cimv2", "SELECT Name FROM Win32_Processor").FirstOrDefault();
        s.Cpu = cpu?.Str("Name");
        var cs = Wmi.Query(@"root\cimv2", "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        s.RamBytes = cs?.Long("TotalPhysicalMemory");
        var bb = Wmi.Query(@"root\cimv2", "SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        s.BoardMaker = bb?.Str("Manufacturer");
        s.Board = bb?.Str("Product");
        var bios = Wmi.Query(@"root\cimv2", "SELECT Manufacturer, SMBIOSBIOSVersion FROM Win32_BIOS").FirstOrDefault();
        if (bios != null) s.Bios = $"{bios.Str("Manufacturer")} {bios.Str("SMBIOSBIOSVersion")}".Trim();
        try
        {
            if (GetFirmwareType(out int ft)) s.FirmwareType = ft switch { 1 => "Legacy", 2 => "UEFI", _ => null };
        }
        catch { }

        if (drive?.PnpDeviceId != null && !drive.IsExternalBus) ReadPcieLink(drive.PnpDeviceId, s);
        return s;
    }

    /// <summary>يصعد من القرص إلى جهاز PCI (متحكم NVMe) ويقرأ سرعة الوصلة الحالية والقصوى بالاسم.</summary>
    private static void ReadPcieLink(string pnp, SystemSnapshot s)
    {
        try
        {
            if (Native.CM_Locate_DevNodeW(out uint dev, pnp, 0) != 0) return;
            string? pciId = null;
            for (int i = 0; i < 4 && pciId == null; i++)
            {
                if (Native.CM_Get_Parent(out uint parent, dev, 0) != 0) return;
                var buf = new char[512];
                if (Native.CM_Get_Device_IDW(parent, buf, buf.Length, 0) != 0) return;
                string id = new string(buf).TrimEnd('\0');
                if (id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) pciId = id;
                dev = parent;
            }
            if (pciId == null) return;

            var props = Wmi.DeviceProperties(pciId,
                "DEVPKEY_PciDevice_CurrentLinkSpeed", "DEVPKEY_PciDevice_CurrentLinkWidth",
                "DEVPKEY_PciDevice_MaxLinkSpeed", "DEVPKEY_PciDevice_MaxLinkWidth");
            int? Get(string k)
            {
                if (!props.TryGetValue(k, out var v) || v == null) return null;
                try { int x = Convert.ToInt32(v); return x > 0 ? x : null; } catch { return null; }
            }
            s.LinkGenCurrent = Get("DEVPKEY_PciDevice_CurrentLinkSpeed");
            s.LinkWidthCurrent = Get("DEVPKEY_PciDevice_CurrentLinkWidth");
            s.LinkGenMax = Get("DEVPKEY_PciDevice_MaxLinkSpeed");
            s.LinkWidthMax = Get("DEVPKEY_PciDevice_MaxLinkWidth");
        }
        catch
        {
            // غير مدعوم على هذا النظام — يبقى "غير متوفر"
        }
    }
}
