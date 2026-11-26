using System.Text.Json;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Storage;

namespace IbraDriveCheck.Core.Tools;

public sealed record LifeEstimate(int? HealthPercent, double? YearsLeft, int? TbwRated, long? WrittenBytes, double? GbPerDay, string BasisKey);

/// <summary>أول قراءة مسجلة لكل وحدة (لحساب معدل الكتابة الفعلي مع الوقت).</summary>
public sealed class DriveObservation
{
    public DateTime FirstUtc { get; set; }
    public long? FirstWritten { get; set; }
    public long? FirstHours { get; set; }
}

public static class SsdLife
{
    public static LifeEstimate Estimate(HealthInfo h, long capacityBytes, ModelSpec? spec, DriveObservation? obs, DateTime nowUtc)
    {
        int? tbw = spec?.TbwFor(capacityBytes);
        long? written = h.DataWrittenBytes;
        int? used = h.PercentUsed ?? h.WearPercent;
        int? health = used is int u ? Math.Clamp(100 - u, 0, 100) : null;
        string basis = "life.basis.wear";
        if (health == null && tbw is int t && written is long w)
        {
            health = (int)Math.Clamp(100 - w / (t * 1e12) * 100, 0, 100);
            basis = "life.basis.tbw";
        }
        if (health == null) return new LifeEstimate(null, null, tbw, written, null, "life.basis.none");

        // معدل الكتابة اليومي: من المراقبة الفعلية إذا مر أسبوع، وإلا تقديري من ساعات التشغيل (8 ساعات يومياً)
        double? gbPerDay = null;
        if (obs != null && written is long w2 && obs.FirstWritten is long w0 && (nowUtc - obs.FirstUtc).TotalDays >= 7 && w2 > w0)
            gbPerDay = (w2 - w0) / 1e9 / (nowUtc - obs.FirstUtc).TotalDays;
        else if (written is long w3 && h.PowerOnHours is long hrs && hrs >= 24)
        {
            gbPerDay = w3 / 1e9 / (hrs / 8.0);
            basis += "|life.basis.hours";
        }

        double? years = null;
        if (gbPerDay is double rate && rate > 0.01)
        {
            double remainingGb;
            if (tbw is int t2 && written is long w4) remainingGb = Math.Max(0, t2 * 1000.0 - w4 / 1e9);
            else if (used is int u2 && u2 > 0 && written is long w5) remainingGb = w5 / 1e9 / u2 * (100 - u2);
            else remainingGb = -1;
            if (remainingGb >= 0) years = Math.Min(50, remainingGb / rate / 365.0);
        }
        return new LifeEstimate(health, years, tbw, written, gbPerDay, basis);
    }

    private static string Path => System.IO.Path.Combine(AppPaths.Root, "observations.json");

    public static DriveObservation Observe(string hardwareKey, HealthInfo h)
    {
        Dictionary<string, DriveObservation> all;
        try { all = File.Exists(Path) ? JsonSerializer.Deserialize<Dictionary<string, DriveObservation>>(File.ReadAllText(Path)) ?? new() : new(); }
        catch { all = new(); }
        if (!all.TryGetValue(hardwareKey, out var o))
        {
            o = new DriveObservation { FirstUtc = DateTime.UtcNow, FirstWritten = h.DataWrittenBytes, FirstHours = h.PowerOnHours };
            all[hardwareKey] = o;
            try { Directory.CreateDirectory(AppPaths.Root); File.WriteAllText(Path, JsonSerializer.Serialize(all)); } catch { }
        }
        return o;
    }
}
