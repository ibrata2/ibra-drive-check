namespace IbraDriveCheck.Core.Engine;

public enum SampleOutcome
{
    Untested,
    Good,
    Slow,
    Missing,     // لا أثر للبيانات (منطقة وهمية)
    Corrupted,   // بيانات تالفة
    Aliased,     // بيانات منطقة أخرى (التفاف عناوين)
    WriteError,
    ReadError,
}

public readonly record struct Sample(long Offset, long Length, SampleOutcome Outcome, long? AliasOf = null)
{
    public bool IsTested => Outcome != SampleOutcome.Untested;
    public bool IsGood => Outcome is SampleOutcome.Good or SampleOutcome.Slow;
    public bool IsBad => IsTested && !IsGood;
}

public sealed class CapacityAnalysis
{
    public int Tested { get; init; }
    public int Good { get; init; }
    public int Bad { get; init; }
    public int Aliased { get; init; }
    public int ReadErrors { get; init; }
    public int WriteErrors { get; init; }
    public int Slow { get; init; }

    /// <summary>نمط "سليم ثم تالف حتى النهاية" — العلامة المميزة للسعة المزيفة.</summary>
    public bool StepPattern { get; init; }
    public bool FakeDetected { get; init; }
    public bool AllBad { get; init; }
    /// <summary>أخطاء متفرقة لا تشكل نمط سعة مزيفة.</summary>
    public bool ScatteredErrors { get; init; }

    /// <summary>نهاية آخر منطقة سليمة قبل بداية التلف (حد أدنى للسعة الحقيقية).</summary>
    public long? LowerBound { get; init; }
    /// <summary>بداية أول منطقة تالفة بعد الحد (حد أعلى للسعة الحقيقية).</summary>
    public long? UpperBound { get; init; }
    /// <summary>مسافة الالتفاف المكتشفة (الحجم الفعلي للذاكرة عندما تعيد العناوين العالية إلى البداية).</summary>
    public long? WrapModulus { get; init; }
    public long? EstimatedReal { get; init; }
    /// <summary>طول البداية السليمة المتصلة (من أول عينة) — يستخدم كـ "سعة تم التحقق منها" في الفحص الكامل.</summary>
    public long ContiguousGoodEnd { get; init; }
}

/// <summary>
/// يحلل نتائج المناطق المرتبة حسب العنوان ويقرر هل يوجد تلاعب بالسعة.
/// لا يصدر نتيجة قطعية من عينات قليلة: يتطلب منطقتين تالفتين على الأقل بعد الحد، أو دليل التفاف صريح.
/// </summary>
public static class CapacityAnalyzer
{
    public static CapacityAnalysis Analyze(IReadOnlyList<Sample> samplesInput, long declaredBytes)
    {
        var samples = samplesInput.Where(s => s.IsTested).OrderBy(s => s.Offset).ToList();
        int n = samples.Count;
        int good = samples.Count(s => s.IsGood);
        int bad = n - good;
        int aliased = samples.Count(s => s.Outcome == SampleOutcome.Aliased);

        long contiguous = 0;
        foreach (var s in samples)
        {
            if (!s.IsGood) break;
            contiguous = s.Offset + s.Length;
        }

        if (n == 0)
            return new CapacityAnalysis { ContiguousGoodEnd = 0 };

        // أفضل نقطة تقسيم b: كل ما قبلها سليم وكل ما بعدها تالف (أعلى درجة اتفاق).
        var goodPrefix = new int[n + 1];
        for (int i = 0; i < n; i++) goodPrefix[i + 1] = goodPrefix[i] + (samples[i].IsGood ? 1 : 0);
        int bestB = 0, bestScore = -1;
        for (int b = 0; b <= n; b++)
        {
            int goodBefore = goodPrefix[b];
            int badAfter = (n - b) - (goodPrefix[n] - goodPrefix[b]);
            int score = goodBefore + badAfter;
            if (score > bestScore || (score == bestScore && b > bestB)) { bestScore = score; bestB = b; }
        }

        int tail = n - bestB;
        int badAfterB = tail - (goodPrefix[n] - goodPrefix[bestB]);
        int badBeforeB = bestB - goodPrefix[bestB];
        bool step = bestB >= 1 && tail >= 2 && badAfterB >= 2
                    && badAfterB >= 0.85 * tail
                    && badBeforeB <= Math.Max(1, (int)(0.03 * bestB));

        // نمط الالتفاف: المنطقة عند X تعيد بيانات المنطقة عند Y → الحجم الحقيقي ≈ X − Y
        // نأخذ أصغر مسافة التفاف ونتأكد أن أغلب المسافات مضاعفات لها (اللفة الثانية، الثالثة...).
        long? modulus = null;
        var deltas = samples.Where(s => s.Outcome == SampleOutcome.Aliased && s.AliasOf is not null && s.AliasOf != s.Offset)
                            .Select(s => Math.Abs(s.Offset - s.AliasOf!.Value))
                            .ToList();
        if (deltas.Count > 0)
        {
            long m = deltas.Min();
            int multiples = deltas.Count(d => d % m == 0);
            if (m > 0 && m < declaredBytes && multiples >= Math.Max(1, (int)Math.Ceiling(deltas.Count * 0.6)))
                modulus = m;
        }

        bool allBad = good == 0 && bad > 0;
        bool fake = !allBad && (step || (aliased >= 1 && good >= 1));

        long? lower = null, upper = null, estimate = null;
        if (fake)
        {
            int firstBad = step ? bestB : samples.FindIndex(s => s.IsBad);
            if (firstBad < 0) firstBad = n;
            lower = firstBad > 0 ? samples[firstBad - 1].Offset + samples[firstBad - 1].Length : 0;
            upper = firstBad < n ? samples[firstBad].Offset : declaredBytes;
            // الالتفاف دليل مباشر على الحجم الحقيقي؛ بدونه نعتمد الحد الأدنى الآمن.
            estimate = modulus ?? lower;
        }

        return new CapacityAnalysis
        {
            Tested = n,
            Good = good,
            Bad = bad,
            Aliased = aliased,
            ReadErrors = samples.Count(s => s.Outcome == SampleOutcome.ReadError),
            WriteErrors = samples.Count(s => s.Outcome == SampleOutcome.WriteError),
            Slow = samples.Count(s => s.Outcome == SampleOutcome.Slow),
            StepPattern = step,
            FakeDetected = fake,
            AllBad = allBad,
            ScatteredErrors = bad > 0 && !fake && !allBad,
            LowerBound = lower,
            UpperBound = upper,
            WrapModulus = modulus,
            EstimatedReal = estimate,
            ContiguousGoodEnd = contiguous,
        };
    }

    public static SampleOutcome FromCheck(BlockStatus s) => s switch
    {
        BlockStatus.Ok => SampleOutcome.Good,
        BlockStatus.Aliased => SampleOutcome.Aliased,
        BlockStatus.Corrupted => SampleOutcome.Corrupted,
        _ => SampleOutcome.Missing,
    };
}
