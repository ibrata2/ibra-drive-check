using System.Runtime.Versioning;

namespace IbraDriveCheck.Core.Devices.Windows;

/// <summary>
/// استعلامات WMI عبر COM (WbemScripting) بدون أي حزمة خارجية.
/// أي فشل يعيد قائمة فارغة — لا نختلق معلومات.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Wmi
{
    /// <summary>آخر خطأ WMI (للتشخيص فقط).</summary>
    public static string? LastError { get; private set; }

    public static List<Dictionary<string, object?>> Query(string ns, string wql)
    {
        var list = new List<Dictionary<string, object?>>();
#if HAS_SYSMGMT
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(ns, wql);
            searcher.Options.Timeout = TimeSpan.FromSeconds(15);
            using var results = searcher.Get();
            foreach (System.Management.ManagementBaseObject row in results)
            {
                using (row)
                {
                    var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in row.Properties)
                    {
                        try { d[p.Name] = p.Value; } catch { }
                    }
                    list.Add(d);
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
        }
        return list;
#else
        try
        {
            var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (t == null) return list;
            dynamic locator = Activator.CreateInstance(t)!;
            dynamic svc = locator.ConnectServer(".", ns);
            dynamic rows = svc.ExecQuery(wql, "WQL", 0x30); // wbemFlagReturnImmediately | wbemFlagForwardOnly
            foreach (dynamic row in rows)
            {
                var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (dynamic p in row.Properties_)
                {
                    try { d[(string)p.Name] = (object?)p.Value; } catch { }
                }
                list.Add(d);
            }
        }
        catch (Exception ex)
        {
            // WMI غير متاح أو يحتاج صلاحيات
            LastError = ex.GetType().Name + ": " + ex.Message;
        }
        return list;
#endif
    }

    /// <summary>خصائص جهاز PnP بالاسم (مثل DEVPKEY_PciDevice_CurrentLinkSpeed) عبر Win32_PnPEntity.GetDeviceProperties.</summary>
    public static Dictionary<string, object?> DeviceProperties(string pnpId, params string[] keys)
    {
        var res = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
#if HAS_SYSMGMT
        try
        {
            string escaped = pnpId.Replace("\\", "\\\\").Replace("'", "\\'");
            using var searcher = new System.Management.ManagementObjectSearcher(@"root\cimv2", $"SELECT * FROM Win32_PnPEntity WHERE DeviceID = '{escaped}'");
            foreach (System.Management.ManagementObject dev in searcher.Get())
            {
                using (dev)
                {
                    var inParams = dev.GetMethodParameters("GetDeviceProperties");
                    inParams["devicePropertyKeys"] = keys;
                    var outParams = dev.InvokeMethod("GetDeviceProperties", inParams, null);
                    if (outParams?["deviceProperties"] is System.Management.ManagementBaseObject[] props)
                        foreach (var p in props)
                        {
                            try { res[(string)p["KeyName"]] = p["Data"]; } catch { }
                        }
                }
                break;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
        }
#endif
        return res;
    }

    public static string? Str(this Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v == null) return null;
        var s = Convert.ToString(v)?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public static long? Long(this Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v == null) return null;
        try { return Convert.ToInt64(v); } catch { return null; }
    }

    public static bool? Bool(this Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v == null) return null;
        try { return Convert.ToBoolean(v); } catch { return null; }
    }
}
