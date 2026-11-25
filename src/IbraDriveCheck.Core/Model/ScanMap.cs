namespace IbraDriveCheck.Core.Model;

/// <summary>حالة مربع في خريطة الفحص (الألوان معرفة في الواجهة).</summary>
public enum CellState : byte
{
    Untested = 0,   // رمادي
    Testing = 1,    // أزرق
    Good = 2,       // أخضر
    Slow = 3,       // أصفر (سليمة لكن بطيئة)
    Failed = 4,     // أحمر: بيانات غير مطابقة / منطقة وهمية / التفاف
    WriteError = 5, // برتقالي
    ReadError = 6,  // بنفسجي
}

public sealed class CellInfo
{
    public int Index { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }
    public CellState State { get; set; }
    public long BytesTested { get; set; }
    public double ReadMBps { get; set; }
    public double WriteMBps { get; set; }
    public double ReadLatencyMs { get; set; }
    public double WriteLatencyMs { get; set; }
    /// <summary>إذا كانت المنطقة تعيد بيانات منطقة أخرى (التفاف عناوين) نسجل عنوان تلك المنطقة.</summary>
    public long? AliasOfOffset { get; set; }
    /// <summary>مفتاح ملاحظة قابلة للترجمة (cell.zeros / cell.ff / cell.foreign / cell.hash / cell.alias ...).</summary>
    public string? NoteKey { get; set; }
}

/// <summary>
/// خريطة المناطق المختبرة. المحرك يحدثها من خيط الفحص، والواجهة تقرأها دورياً (Version يتغير مع كل تعديل).
/// </summary>
public sealed class ScanMap
{
    private readonly object _gate = new();
    private int _version;

    public CellInfo[] Cells { get; }
    public long CoveredBytes { get; }
    public long BaseOffset { get; }

    public int Version => Volatile.Read(ref _version);

    public ScanMap(IReadOnlyList<(long Offset, long Length)> cells, long baseOffset = 0)
    {
        Cells = new CellInfo[cells.Count];
        long total = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            Cells[i] = new CellInfo { Index = i, Offset = cells[i].Offset, Length = cells[i].Length };
            total += cells[i].Length;
        }
        CoveredBytes = total;
        BaseOffset = baseOffset;
    }

    /// <summary>يقسم مدى بالتساوي إلى count مربع (محاذاة على align).</summary>
    public static ScanMap Uniform(long start, long length, int count, long align = 4096)
    {
        if (length <= 0) return new ScanMap(Array.Empty<(long, long)>(), start);
        long units = Math.Max(1, length / align);
        count = (int)Math.Max(1, Math.Min(count, units));
        var list = new List<(long, long)>(count);
        for (int i = 0; i < count; i++)
        {
            long a = start + (units * i / count) * align;
            long b = i == count - 1 ? start + length : start + (units * (i + 1) / count) * align;
            list.Add((a, b - a));
        }
        return new ScanMap(list, start);
    }

    public int IndexOf(long offset)
    {
        int lo = 0, hi = Cells.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            var c = Cells[mid];
            if (offset < c.Offset) hi = mid - 1;
            else if (offset >= c.Offset + c.Length) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    public void Update(int index, Action<CellInfo> change)
    {
        if ((uint)index >= (uint)Cells.Length) return;
        lock (_gate) change(Cells[index]);
        Interlocked.Increment(ref _version);
    }

    /// <summary>يرفع حالة المربع حسب الخطورة (لا ينزل من أحمر إلى أخضر).</summary>
    public void Escalate(int index, CellState state, string? noteKey = null, long? aliasOf = null)
    {
        Update(index, c =>
        {
            if (c.State is CellState.Untested or CellState.Testing || Severity(state) > Severity(c.State))
            {
                c.State = state;
                if (noteKey != null) c.NoteKey = noteKey;
                if (aliasOf != null) c.AliasOfOffset = aliasOf;
            }
        });
    }

    public static int Severity(CellState s) => s switch
    {
        CellState.Untested => 0,
        CellState.Testing => 0,
        CellState.Good => 1,
        CellState.Slow => 2,
        CellState.Failed => 5,
        CellState.WriteError => 4,
        CellState.ReadError => 3,
        _ => 0,
    };

    public (int good, int slow, int failed, int writeErr, int readErr, int untested) Count()
    {
        int g = 0, s = 0, f = 0, w = 0, r = 0, u = 0;
        lock (_gate)
        {
            foreach (var c in Cells)
            {
                switch (c.State)
                {
                    case CellState.Good: g++; break;
                    case CellState.Slow: s++; break;
                    case CellState.Failed: f++; break;
                    case CellState.WriteError: w++; break;
                    case CellState.ReadError: r++; break;
                    default: u++; break;
                }
            }
        }
        return (g, s, f, w, r, u);
    }

    public CellRecord[] Snapshot()
    {
        lock (_gate)
            return Cells.Select(c => new CellRecord
            {
                Offset = c.Offset,
                Length = c.Length,
                State = c.State,
                ReadMBps = Math.Round(c.ReadMBps, 2),
                WriteMBps = Math.Round(c.WriteMBps, 2),
                ReadLatencyMs = Math.Round(c.ReadLatencyMs, 3),
                WriteLatencyMs = Math.Round(c.WriteLatencyMs, 3),
                AliasOfOffset = c.AliasOfOffset,
                NoteKey = c.NoteKey,
            }).ToArray();
    }
}

/// <summary>نسخة مختصرة من المربع تحفظ في السجل.</summary>
public sealed class CellRecord
{
    public long Offset { get; set; }
    public long Length { get; set; }
    public CellState State { get; set; }
    public double ReadMBps { get; set; }
    public double WriteMBps { get; set; }
    public double ReadLatencyMs { get; set; }
    public double WriteLatencyMs { get; set; }
    public long? AliasOfOffset { get; set; }
    public string? NoteKey { get; set; }
}
