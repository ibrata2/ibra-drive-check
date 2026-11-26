using System.Text.RegularExpressions;

namespace IbraDriveCheck.Core.Tools;

/// <summary>مواصفات موديل حسب الشركة المصنعة (السرعات القصوى المعلنة، والتحمل TBW حسب السعة).</summary>
public sealed record ModelSpec(string Name, string Interface, int ReadMBps, int WriteMBps, Dictionary<int, int>? TbwByGb, string Pattern)
{
    /// <summary>TBW الأقرب للسعة (بالتيرابايت المكتوبة).</summary>
    public int? TbwFor(long bytes)
    {
        if (TbwByGb == null || TbwByGb.Count == 0) return null;
        double gb = bytes / 1e9;
        return TbwByGb.OrderBy(kv => Math.Abs(kv.Key - gb)).First().Value;
    }
}

public static class ModelDatabase
{
    private static Dictionary<int, int> T(params int[] gbTbw) { var d = new Dictionary<int, int>(); for (int i = 0; i + 1 < gbTbw.Length; i += 2) d[gbTbw[i]] = gbTbw[i + 1]; return d; }

    // القيم تقريبية حسب صفحات المواصفات المنشورة للشركات (القراءة/الكتابة المتسلسلة القصوى)
    public static readonly List<ModelSpec> All = new()
    {
        new("Samsung 990 PRO", "NVMe PCIe 4.0", 7450, 6900, T(1000, 600, 2000, 1200, 4000, 2400), @"990\s*PRO"),
        new("Samsung 990 EVO Plus", "NVMe PCIe 4.0", 7250, 6300, T(1000, 600, 2000, 1200, 4000, 2400), @"990\s*EVO\s*PLUS"),
        new("Samsung 990 EVO", "NVMe PCIe 4.0/5.0", 5000, 4200, T(1000, 600, 2000, 1200), @"990\s*EVO"),
        new("Samsung 980 PRO", "NVMe PCIe 4.0", 7000, 5100, T(500, 300, 1000, 600, 2000, 1200), @"980\s*PRO"),
        new("Samsung 980", "NVMe PCIe 3.0", 3500, 3000, T(500, 300, 1000, 600), @"SSD\s*980\b"),
        new("Samsung 970 EVO Plus", "NVMe PCIe 3.0", 3500, 3300, T(500, 300, 1000, 600, 2000, 1200), @"970\s*EVO\s*PLUS"),
        new("Samsung 870 EVO", "SATA", 560, 530, T(500, 300, 1000, 600, 2000, 1200, 4000, 2400), @"870\s*EVO"),
        new("Samsung 870 QVO", "SATA", 560, 530, T(1000, 360, 2000, 720, 4000, 1440), @"870\s*QVO"),
        new("Samsung T7 / T7 Shield", "USB 3.2 Gen 2", 1050, 1000, null, @"PORTABLE\s*SSD\s*T7|\bT7\b"),
        new("Samsung T9", "USB 3.2 Gen 2x2", 2000, 2000, null, @"PORTABLE\s*SSD\s*T9|\bT9\b"),
        new("WD Black SN850X", "NVMe PCIe 4.0", 7300, 6600, T(1000, 600, 2000, 1200, 4000, 2400), @"SN850X"),
        new("WD Black SN770", "NVMe PCIe 4.0", 5150, 4900, T(500, 300, 1000, 600, 2000, 1200), @"SN770"),
        new("WD Blue SN580", "NVMe PCIe 4.0", 4150, 4150, T(500, 300, 1000, 600, 2000, 900), @"SN580"),
        new("WD Blue SN570", "NVMe PCIe 3.0", 3500, 3000, T(500, 300, 1000, 600, 2000, 900), @"SN570"),
        new("WD Blue SA510", "SATA", 560, 530, T(500, 200, 1000, 400, 2000, 600), @"SA510"),
        new("WD Green SN350", "NVMe PCIe 3.0", 3200, 2500, T(480, 60, 1000, 100), @"SN350"),
        new("Crucial T500", "NVMe PCIe 4.0", 7400, 7000, T(500, 300, 1000, 600, 2000, 1200), @"T500|CT\d+T500"),
        new("Crucial P3 Plus", "NVMe PCIe 4.0", 5000, 4200, T(500, 110, 1000, 220, 2000, 440, 4000, 800), @"P3\s*PLUS|P3PSSD"),
        new("Crucial P3", "NVMe PCIe 3.0", 3500, 3000, T(500, 110, 1000, 220, 2000, 440, 4000, 800), @"\bP3\b|P3SSD"),
        new("Crucial MX500", "SATA", 560, 510, T(500, 180, 1000, 360, 2000, 700, 4000, 1000), @"MX500"),
        new("Crucial BX500", "SATA", 540, 500, T(500, 120, 1000, 360, 2000, 720), @"BX500"),
        new("Kingston KC3000", "NVMe PCIe 4.0", 7000, 7000, T(512, 400, 1024, 800, 2048, 1600, 4096, 3200), @"KC3000|SKC3000"),
        new("Kingston NV3", "NVMe PCIe 4.0", 6000, 5000, T(500, 160, 1000, 320, 2000, 640, 4000, 1280), @"\bNV3\b|SNV3S"),
        new("Kingston NV2", "NVMe PCIe 4.0", 3500, 2800, T(500, 160, 1000, 320, 2000, 640, 4000, 1280), @"\bNV2\b|SNV2S"),
        new("Kingston A400", "SATA", 500, 450, T(240, 80, 480, 160, 960, 300), @"A400|SA400"),
        new("Lexar NM790", "NVMe PCIe 4.0", 7400, 6500, T(1000, 1000, 2000, 1500, 4000, 3000), @"NM790"),
        new("Lexar NM620", "NVMe PCIe 3.0", 3300, 3000, T(512, 250, 1000, 500, 2000, 1000), @"NM620"),
        new("SanDisk Extreme Portable SSD", "USB 3.2 Gen 2", 1050, 1000, null, @"EXTREME\s*(PORTABLE|55AE|PRO\s*55AF)|SANDISK\s*EXTREME"),
        new("SanDisk Ultra USB 3.0 (flash)", "USB 3.0", 130, 40, null, @"SANDISK\s*ULTRA|ULTRA\s*USB"),
        new("SanDisk Ultra Fit (flash)", "USB 3.1", 130, 40, null, @"ULTRA\s*FIT"),
        new("Kingston DataTraveler Exodia (flash)", "USB 3.2 Gen 1", 100, 30, null, @"DATATRAVELER\s*EXODIA|DT\s*EXODIA"),
        new("Kingston DataTraveler (flash)", "USB 3.x", 100, 20, null, @"DATATRAVELER|DT\s*\d"),
        new("Seagate BarraCuda (HDD)", "SATA", 190, 190, null, @"ST\d+DM\d+|BARRACUDA"),
        new("WD Blue (HDD)", "SATA", 180, 180, null, @"WD\d+EZ[A-Z]{2}"),
        new("WD Elements / My Passport (HDD)", "USB 3.0", 130, 130, null, @"ELEMENTS|MY\s*PASSPORT"),
        new("Seagate Expansion / One Touch (HDD)", "USB 3.0", 130, 130, null, @"EXPANSION|ONE\s*TOUCH"),
        new("Toshiba Canvio (HDD)", "USB 3.0", 130, 130, null, @"CANVIO"),
    };

    public static ModelSpec? Match(string? manufacturer, string? model)
    {
        string text = $"{manufacturer} {model}".ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return All.FirstOrDefault(m => Regex.IsMatch(text, m.Pattern, RegexOptions.IgnoreCase));
    }
}
