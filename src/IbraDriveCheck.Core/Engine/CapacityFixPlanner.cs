using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Engine;

public enum CapacitySource
{
    None,          // لا يوجد فحص سعة سابق
    Genuine,       // الفحص لم يجد تلاعباً — الحجم الكامل
    Verified,      // سعة تم التحقق منها بالكتابة الكاملة
    WrapExact,     // سعة محسوبة من التفاف العناوين
    LowerBound,    // حد أدنى من العينات
}

public sealed record CapacityPlan(long RecommendedBytes, long RealBytes, CapacitySource Source, ScanResult? Basis);

/// <summary>يحسب حجم القسم الآمن لإصلاح وحدة مزيفة من آخر فحص سعة.</summary>
public static class CapacityFixPlanner
{
    public const long MiB = 1024 * 1024;

    public static readonly ScanKind[] CapacityKinds = { ScanKind.Quick, ScanKind.Deep, ScanKind.FullDestructive, ScanKind.FullFreeSpace, ScanKind.Purchase };

    public static CapacityPlan Plan(DriveSnapshot d, IEnumerable<ScanResult> history)
    {
        var last = history.Where(h => h.Drive.HardwareKey == d.HardwareKey && CapacityKinds.Contains(h.Kind) && h.Verdict != Verdict.Incomplete)
                          .OrderByDescending(h => h.StartedUtc).FirstOrDefault();
        long declared = d.DeclaredBytes;
        if (last == null) return new CapacityPlan(Floor(declared - MiB), declared, CapacitySource.None, null);

        bool fake = last.EstimatedRealBytes is long || last.Verdict == Verdict.FakeCapacity;
        if (!fake) return new CapacityPlan(Floor(declared - MiB), declared, CapacitySource.Genuine, last);

        long real;
        double factor;
        CapacitySource src;
        if (last.Kind == ScanKind.FullDestructive && last.VerifiedBytes is long v && v > 0)
        {
            real = v; factor = 1.0; src = CapacitySource.Verified;
        }
        else if (last.EstimateFromWrap && last.EstimatedRealBytes is long w)
        {
            real = w; factor = 0.99; src = CapacitySource.WrapExact;
        }
        else
        {
            real = last.EstimatedRealBytes ?? last.VerifiedBytes ?? declared;
            factor = 0.97; src = CapacitySource.LowerBound;
        }
        // القسم يبدأ عند 1MB؛ نترك هامش أمان من نهاية الجزء الحقيقي
        long usable = (long)((real - MiB) * factor) - MiB;
        return new CapacityPlan(Floor(Math.Max(MiB * 8, usable)), real, src, last);
    }

    public static long Floor(long bytes) => Math.Max(0, bytes / MiB * MiB);
}
