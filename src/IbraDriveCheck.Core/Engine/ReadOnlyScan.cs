using IbraDriveCheck.Core.Devices;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Engine;

public sealed class ReadOnlyOptions
{
    /// <summary>true = قراءة كامل السطح، false = عينات موزعة (أسرع).</summary>
    public bool FullSurface { get; set; } = true;
    public int ChunkBytes { get; set; } = 4 << 20;
    public int SampleCount { get; set; } = 2048;
    public int MapCells { get; set; } = 2048;
    public double SlowFraction { get; set; } = 0.15;
}

/// <summary>
/// فحص القراءة فقط: لا يكتب أي شيء. يكشف أخطاء القراءة والمناطق البطيئة،
/// لكنه لا يستطيع إثبات السعة الحقيقية (الذاكرة المزيفة تعيد بيانات لأي عنوان).
/// </summary>
public sealed class ReadOnlyScan
{
    private readonly IBlockDevice _dev;
    private readonly DriveSnapshot _drive;
    private readonly ReadOnlyOptions _opt;
    private readonly ScanSession _s;

    public ReadOnlyScan(IBlockDevice dev, DriveSnapshot drive, ReadOnlyOptions opt, ScanSession session)
    {
        _dev = dev; _drive = drive; _opt = opt; _s = session;
    }

    public ScanResult Run(CancellationToken ct)
    {
        var r = ResultBuilder.Start(ScanKind.ReadOnly, _drive, _dev.Size);
        int align = Math.Max(4096, _dev.Alignment);
        long size = _dev.Size / align * align;
        int chunk = Math.Max(align, _opt.ChunkBytes / align * align);
        var map = ScanMap.Uniform(0, size, _opt.MapCells, chunk);
        _s.Map = map;
        _s.StageCount = 1;
        _s.PauseSupported = true;

        var positions = new List<(long off, int len)>();
        if (_opt.FullSurface)
        {
            for (long off = 0; off < size; off += chunk) positions.Add((off, (int)Math.Min(chunk, size - off)));
        }
        else
        {
            int n = _opt.SampleCount;
            int len = Math.Min(chunk, 256 * 1024);
            for (int i = 0; i < n; i++)
            {
                long off = (size - len) / align * i / Math.Max(1, n - 1) * align;
                positions.Add((Math.Min(off, size - len), len));
            }
            positions = positions.Distinct().ToList();
        }

        long totalBytes = positions.Sum(p => (long)p.len);
        _s.SetStage(0, ScanPhase.Reading, totalBytes);
        using var buf = new AlignedBuffer(chunk, align);
        int errors = 0;
        long readOk = 0;

        try
        {
            foreach (var (off, len) in positions)
            {
                if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                if (_s.Pause.IsPaused) { _s.MarkPaused(true); _s.Pause.WaitIfPaused(ct); _s.MarkPaused(false); }
                int cell = map.IndexOf(off);
                map.Update(cell, c => { if (c.State == CellState.Untested) c.State = CellState.Testing; });
                long t0 = _s.Time.Timestamp;
                try
                {
                    _dev.Read(off, buf.Slice(0, len));
                    double mbps = SampledCapacityTest.Mbps(len, _s.Seconds(t0, _s.Time.Timestamp) * 1000);
                    _s.CurrentReadMBps = mbps;
                    readOk += len;
                    map.Update(cell, c => { c.ReadMBps = c.ReadMBps <= 0 ? mbps : (c.ReadMBps + mbps) / 2; c.BytesTested += len; });
                    map.Escalate(cell, CellState.Good);
                }
                catch (BlockIoException)
                {
                    errors++;
                    map.Escalate(cell, CellState.ReadError, "cell.readError");
                }
                Interlocked.Add(ref _s.BytesTested, len);
                _s.AddProgress(len);
            }
        }
        catch (DeviceGoneException)
        {
            r.DeviceDisconnected = true;
            r.Note("note.disconnected");
        }

        // المناطق البطيئة
        var speeds = map.Cells.Where(c => c.State == CellState.Good && c.ReadMBps > 0).Select(c => c.ReadMBps).OrderBy(x => x).ToList();
        if (speeds.Count >= 8)
        {
            double median = speeds[speeds.Count / 2];
            foreach (var c in map.Cells)
                if (c.State == CellState.Good && c.ReadMBps < median * _opt.SlowFraction)
                    map.Escalate(c.Index, CellState.Slow, "cell.slow");
        }

        r.BytesTested = Interlocked.Read(ref _s.BytesTested);
        r.SampleCount = positions.Count;
        r.CoveragePercent = _dev.Size > 0 ? Math.Round(100.0 * r.BytesTested / _dev.Size, 3) : 0;
        ResultBuilder.FillFromMap(r, map);

        if (r.Cancelled || r.DeviceDisconnected) r.Verdict = Verdict.Incomplete;
        else if (errors > 0) r.Verdict = Verdict.ReadWriteErrors;
        else if (r.SlowCells > 0.25 * Math.Max(1, r.GoodCells)) r.Verdict = Verdict.PoorPerformance;
        else r.Verdict = Verdict.Inconclusive;

        if (errors > 0) r.Note("note.readErrors", errors);
        r.Note("note.readOnlyLimit");
        if (r.Cancelled) r.Note("note.cancelled");
        r.FinishedUtc = DateTime.UtcNow;
        _s.Phase = ScanPhase.Done;
        return r;
    }
}
