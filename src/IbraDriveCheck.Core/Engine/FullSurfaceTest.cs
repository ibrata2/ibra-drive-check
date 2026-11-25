using IbraDriveCheck.Core.Devices;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Engine;

public sealed class FullSurfaceOptions
{
    public int ChunkBytes { get; set; } = 4 << 20;   // 4MB لكل عملية
    public int MapCells { get; set; } = 2048;
    public double SlowFraction { get; set; } = 0.15; // أبطأ من 15% من الوسيط = منطقة بطيئة
}

/// <summary>
/// فحص السعة الكامل المدمر: يكتب نمطاً فريداً على كل كتلة 4KB في كامل الوحدة ثم يقرأ الكل ويتحقق.
/// يمسح كل البيانات — لا يُشغل إلا بعد موافقة صريحة وقفل وفصل كل الأقسام.
/// </summary>
public sealed class FullSurfaceTest
{
    private readonly IBlockDevice _dev;
    private readonly DriveSnapshot _drive;
    private readonly FullSurfaceOptions _opt;
    private readonly ScanSession _s;

    public FullSurfaceTest(IBlockDevice dev, DriveSnapshot drive, FullSurfaceOptions opt, ScanSession session)
    {
        _dev = dev; _drive = drive; _opt = opt; _s = session;
    }

    public ScanResult Run(CancellationToken ct)
    {
        var r = ResultBuilder.Start(ScanKind.FullDestructive, _drive, _dev.Size);
        int blk = TestPattern.BlockSize;
        long size = _dev.Size / blk * blk;
        int chunk = Math.Max(blk, _opt.ChunkBytes / blk * blk);
        var map = ScanMap.Uniform(0, size, _opt.MapCells, chunk);
        _s.Map = map;
        var nonce = TestPattern.NewNonce();
        _s.StageCount = 2;
        using var buf = new AlignedBuffer(chunk, Math.Max(4096, _dev.Alignment));
        long writtenStart = size;

        var chunkOutcome = new Dictionary<long, (SampleOutcome o, long? alias, int len)>();

        try
        {
            // 1) الكتابة على كامل السعة — من النهاية إلى البداية حتى تبقى بيانات المنطقة الحقيقية هي الأحدث
            _s.SetStage(0, ScanPhase.Writing, size);
            long chunks = (size + chunk - 1) / chunk;
            for (long k = chunks - 1; k >= 0; k--)
            {
                long off = k * chunk;
                if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                WaitPause(ct);
                int len = (int)Math.Min(chunk, size - off);
                var span = buf.Slice(0, len);
                TestPattern.FillRange(span, nonce, off / blk, off);
                int cell = map.IndexOf(off);
                map.Update(cell, c => { if (c.State == CellState.Untested) c.State = CellState.Testing; });
                long t0 = _s.Time.Timestamp;
                try
                {
                    _dev.Write(off, span);
                    double ms = _s.Seconds(t0, _s.Time.Timestamp) * 1000;
                    double mbps = SampledCapacityTest.Mbps(len, ms);
                    _s.CurrentWriteMBps = mbps;
                    map.Update(cell, c => c.WriteMBps = Avg(c.WriteMBps, mbps));
                }
                catch (BlockIoException)
                {
                    chunkOutcome[off] = (SampleOutcome.WriteError, null, len);
                    map.Escalate(cell, CellState.WriteError, "cell.writeError");
                }
                writtenStart = off;
                Interlocked.Add(ref _s.BytesTested, len);
                _s.AddProgress(len);
            }
            _dev.Flush();

            // 2) القراءة والتحقق من كل كتلة
            if (!r.Cancelled)
            {
                _s.SetStage(1, ScanPhase.Verifying, size - writtenStart);
                for (long off = writtenStart; off < size; off += chunk)
                {
                    if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                    WaitPause(ct);
                    int len = (int)Math.Min(chunk, size - off);
                    int cell = map.IndexOf(off);
                    _s.AddProgress(len);
                    if (chunkOutcome.ContainsKey(off)) continue;
                    var span = buf.Slice(0, len);
                    long t0 = _s.Time.Timestamp;
                    try
                    {
                        _dev.Read(off, span);
                    }
                    catch (BlockIoException)
                    {
                        // نحدد مكان الخطأ بدقة أكبر: كتلة كتلة
                        var o = ReadPerBlock(off, len, nonce, cell, map);
                        chunkOutcome[off] = (o, null, len);
                        continue;
                    }
                    double ms = _s.Seconds(t0, _s.Time.Timestamp) * 1000;
                    double mbps = SampledCapacityTest.Mbps(len, ms);
                    _s.CurrentReadMBps = mbps;
                    map.Update(cell, c => { c.ReadMBps = Avg(c.ReadMBps, mbps); c.BytesTested += len; });
                    Interlocked.Add(ref _s.BytesTested, len);

                    var worst = SampleOutcome.Good;
                    long? aliasOf = null;
                    string? detail = null;
                    for (int b = 0; b < len / blk; b++)
                    {
                        long boff = off + (long)b * blk;
                        var chk = TestPattern.Verify(span.Slice(b * blk, blk), nonce, boff / blk, boff);
                        if (chk.Status == BlockStatus.Ok) continue;
                        var o = CapacityAnalyzer.FromCheck(chk.Status);
                        if (worst == SampleOutcome.Good) { worst = o; detail = chk.Detail; if (o == SampleOutcome.Aliased) aliasOf = chk.FoundOffset - (long)b * blk; }
                        break;
                    }
                    chunkOutcome[off] = (worst, aliasOf, len);
                    if (worst != SampleOutcome.Good)
                        map.Escalate(cell, ResultBuilder.ToCell(worst), ResultBuilder.NoteFor(worst, detail), aliasOf);
                    else
                        map.Escalate(cell, CellState.Good);
                }
                MarkSlow(map);
            }
        }
        catch (DeviceGoneException)
        {
            r.DeviceDisconnected = true;
            r.Note("note.disconnected");
        }

        var samples = chunkOutcome.OrderBy(k => k.Key)
            .Select(k => new Sample(k.Key, k.Value.len, k.Value.o, k.Value.alias)).ToList();
        // المربعات البطيئة
        foreach (var i in Enumerable.Range(0, samples.Count))
        {
            var sm = samples[i];
            int cell = map.IndexOf(sm.Offset);
            if (sm.Outcome == SampleOutcome.Good && cell >= 0 && map.Cells[cell].State == CellState.Slow)
                samples[i] = sm with { Outcome = SampleOutcome.Slow };
        }
        var analysis = CapacityAnalyzer.Analyze(samples, _dev.Size);
        bool complete = !r.Cancelled && !r.DeviceDisconnected;
        r.VerifiedBytes = analysis.ContiguousGoodEnd;
        r.BytesTested = Interlocked.Read(ref _s.BytesTested);
        r.SampleCount = samples.Count;
        long verifiedSpan = samples.Sum(s => s.Length);
        r.CoveragePercent = _dev.Size > 0 ? Math.Round(100.0 * verifiedSpan / _dev.Size, 3) : 0;
        ResultBuilder.FillFromMap(r, map);
        ResultBuilder.ApplyVerdict(r, analysis, fullyVerified: complete);
        if (complete && r.Verdict == Verdict.Genuine) r.Note("note.fullVerified");
        r.Note("note.destructiveDone");
        if (r.Cancelled) r.Note("note.cancelled");
        r.FinishedUtc = DateTime.UtcNow;
        _s.Phase = ScanPhase.Done;
        return r;
    }

    private SampleOutcome ReadPerBlock(long off, int len, byte[] nonce, int cell, ScanMap map)
    {
        int blk = TestPattern.BlockSize;
        var worst = SampleOutcome.Good;
        using var one = new AlignedBuffer(blk, Math.Max(4096, _dev.Alignment));
        for (int b = 0; b < len / blk; b++)
        {
            long boff = off + (long)b * blk;
            try
            {
                _dev.Read(boff, one.Span);
                var chk = TestPattern.Verify(one.Span, nonce, boff / blk, boff);
                if (chk.Status != BlockStatus.Ok && worst == SampleOutcome.Good) worst = CapacityAnalyzer.FromCheck(chk.Status);
            }
            catch (BlockIoException)
            {
                worst = SampleOutcome.ReadError;
                break;
            }
        }
        map.Escalate(cell, ResultBuilder.ToCell(worst), ResultBuilder.NoteFor(worst, null));
        return worst;
    }

    private void MarkSlow(ScanMap map)
    {
        var speeds = map.Cells.Where(c => c.State == CellState.Good && c.WriteMBps > 0).Select(c => c.WriteMBps).OrderBy(x => x).ToList();
        if (speeds.Count < 8) return;
        double median = speeds[speeds.Count / 2];
        foreach (var c in map.Cells)
            if (c.State == CellState.Good && c.WriteMBps > 0 && c.WriteMBps < median * _opt.SlowFraction)
                map.Escalate(c.Index, CellState.Slow, "cell.slow");
    }

    private static double Avg(double old, double v) => old <= 0 ? v : (old + v) / 2;

    private void WaitPause(CancellationToken ct)
    {
        if (!_s.Pause.IsPaused) return;
        _s.MarkPaused(true);
        _s.Pause.WaitIfPaused(ct);
        _s.MarkPaused(false);
    }
}
