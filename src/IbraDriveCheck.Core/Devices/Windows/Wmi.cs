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
