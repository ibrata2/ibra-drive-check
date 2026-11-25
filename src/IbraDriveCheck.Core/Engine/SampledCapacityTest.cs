using IbraDriveCheck.Core.Devices;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Safety;

namespace IbraDriveCheck.Core.Engine;

public sealed class SampledOptions
{
    public int Regions { get; set; } = 576;
    public int BlockSize { get; set; } = TestPattern.BlockSize;
    public long ProtectedHead { get; set; } = 1L << 20;   // لا نلمس أول 1MB (جدول الأقسام وقطاع الإقلاع)
    public long ProtectedTail { get; set; } = 1L << 20;   // ولا آخر 1MB (نسخة GPT الاحتياطية)
    public int MaxMapCells { get; set; } = 2048;
    public double SlowFactor { get; set; } = 8;
    public double SlowMinMs { get; set; } = 150;

    public static SampledOptions Quick() => new() { Regions = 576 };
    public static SampledOptions Deep() => new() { Regions = 4096 };
}

/// <summary>
/// الفحص السريع والعميق: يقرأ القطاعات الأصلية في مناطق موزعة على كامل السعة، يحفظها بسجل استعادة،
/// يكتب نمطاً فريداً في كل منطقة، ثم يقرأ الجميع ويتحقق (يكشف الالتفاف والمناطق الوهمية)، ثم يعيد البيانات الأصلية ويتحقق منها.
/// </summary>
public sealed class SampledCapacityTest
{
    private readonly IBlockDevice _dev;
    private readonly DriveSnapshot _drive;
    private readonly ScanKind _kind;
    private readonly SampledOptions _opt;
    private readonly IBackupJournal _journal;
    private readonly ScanSession _s;

    public SampledCapacityTest(IBlockDevice dev, DriveSnapshot drive, ScanKind kind, SampledOptions opt, IBackupJournal journal, ScanSession session)
    {
        _dev = dev; _drive = drive; _kind = kind; _opt = opt; _journal = journal; _s = session;
    }

    /// <summary>
    /// العينات على شبكة منتظمة بخطوة = قوة للعدد 2 (start + k·S). الذاكرة المزيفة عادةً تلتف بحجم قوة للعدد 2،
    /// فكل عينة فوق الحجم الحقيقي تقع بالضبط على عينة أخرى أدنى منها → يظهر الالتفاف بشكل مؤكد وليس بالصدفة.
    /// </summary>
    public static List<long> PlanOffsets(long size, SampledOptions opt, int alignment)
    {
        int blk = opt.BlockSize;
        long align = Math.Max(blk, Math.Max(512, alignment));
        long start = AlignUp(opt.ProtectedHead, align);
        long end = AlignDown(size - opt.ProtectedTail, align);   // آخر بايت مسموح (حصري)
        var list = new List<long>();
        if (end - start < blk) return list;

        long span = end - start;
        long target = Math.Max(1, opt.Regions);
        long stride = align;
        while (stride * 2 <= span / target) stride *= 2;      // أكبر قوة للعدد 2 تعطي ≥ العدد المطلوب من العينات
        for (long off = start; off + blk <= end; off += stride) list.Add(off);
        // عينة إضافية عند نهاية السعة المعلنة تماماً
        long last = AlignDown(end - blk, align);
        if (list.Count == 0 || last > list[^1]) list.Add(last);
        return list;
    }

    private static long AlignUp(long v, long a) => (v + a - 1) / a * a;
    private static long AlignDown(long v, long a) => v / a * a;

    public ScanResult Run(CancellationToken ct)
    {
        var r = ResultBuilder.Start(_kind, _drive, _dev.Size);
        var offsets = PlanOffsets(_dev.Size, _opt, _dev.Alignment);
        int n = offsets.Count;
        int blk = _opt.BlockSize;
        r.SampleCount = n;

        int cells = Math.Min(n, _opt.MaxMapCells);
        var ranges = new List<(long, long)>(cells);
        for (int c = 0; c < cells; c++)
        {
            int i0 = (int)((long)n * c / cells);
            int i1 = (int)((long)n * (c + 1) / cells) - 1;
            long a = c == 0 ? 0 : offsets[i0];
            long b = c == cells - 1 ? _dev.Size : offsets[i1 + 1];
            ranges.Add((a, b - a));
        }
        var map = new ScanMap(ranges);
        _s.Map = map;
        int CellOf(int i) => (int)((long)i * cells / Math.Max(1, n));

        var outcomes = new SampleOutcome[n];
        var details = new string?[n];
        var alias = new long?[n];
        var written = new bool[n];
        var skip = new bool[n];
        var writeLat = new double[n];
        var readLat = new double[n];
        var nonce = TestPattern.NewNonce();

        _s.StageCount = 4;
        using var buf = new AlignedBuffer(blk, Math.Max(4096, _dev.Alignment));
        bool anyWritten = false;
        bool restoredAll = false;

        try
        {
            // 1) قراءة البيانات الأصلية لكل المناطق قبل أي كتابة
            _s.SetStage(0, ScanPhase.ReadingOriginals, (long)n * blk);
            _journal.Begin(_drive.HardwareKey, blk, offsets);
            for (int i = 0; i < n; i++)
            {
                if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                WaitPause(ct);
                map.Update(CellOf(i), c => { if (c.State == CellState.Untested) c.State = CellState.Testing; });
                try
                {
                    _dev.Read(offsets[i], buf.Span);
                    _journal.Save(i, buf.Span);
                }
                catch (BlockIoException)
                {
                    // لا نكتب على منطقة لم نستطع حفظ محتواها
                    skip[i] = true;
                    outcomes[i] = SampleOutcome.ReadError;
                    map.Escalate(CellOf(i), CellState.ReadError, "cell.readError");
                }
                _s.AddProgress(blk);
            }

            if (!r.Cancelled)
            {
                _journal.Commit();
                if (_journal.Location != null) r.Note("note.journalSaved");

                // 2) كتابة نمط فريد في كل منطقة — من الأعلى إلى الأسفل، حتى تبقى بيانات المنطقة الحقيقية هي الأخيرة
                //    وتظهر المناطق العالية الملتفة بأنها تحمل بيانات منطقة أدنى (التفاف مؤكد).
                _s.SetStage(1, ScanPhase.Writing, (long)n * blk);
                for (int i = n - 1; i >= 0; i--)
                {
                    if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                    WaitPause(ct);
                    if (skip[i]) { _s.AddProgress(blk); continue; }
                    TestPattern.Fill(buf.Span, nonce, i, offsets[i]);
                    long t0 = _s.Time.Timestamp;
                    written[i] = true;
                    anyWritten = true;
                    try
                    {
                        _dev.Write(offsets[i], buf.Span);
                        writeLat[i] = _s.Seconds(t0, _s.Time.Timestamp) * 1000;
                        _s.CurrentWriteMBps = Mbps(blk, writeLat[i]);
                    }
                    catch (BlockIoException)
                    {
                        outcomes[i] = SampleOutcome.WriteError;
                        map.Escalate(CellOf(i), CellState.WriteError, "cell.writeError");
                    }
                    Interlocked.Add(ref _s.BytesTested, blk);
                    _s.AddProgress(blk);
                }
                _dev.Flush();

                // 3) قراءة كل المناطق والتحقق بعد انتهاء كل الكتابات (يكشف الكتابة فوق المناطق السابقة)
                if (!r.Cancelled)
                {
                    _s.SetStage(2, ScanPhase.Verifying, (long)n * blk);
                    for (int i = 0; i < n; i++)
                    {
                        if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                        WaitPause(ct);
                        _s.AddProgress(blk);
                        if (!written[i] || outcomes[i] == SampleOutcome.WriteError) continue;
                        long t0 = _s.Time.Timestamp;
                        try
                        {
                            _dev.Read(offsets[i], buf.Span);
                            readLat[i] = _s.Seconds(t0, _s.Time.Timestamp) * 1000;
                            _s.CurrentReadMBps = Mbps(blk, readLat[i]);
                            var chk = TestPattern.Verify(buf.Span, nonce, i, offsets[i]);
                            outcomes[i] = CapacityAnalyzer.FromCheck(chk.Status);
                            details[i] = chk.Detail;
                            if (chk.Status == BlockStatus.Aliased) alias[i] = chk.FoundOffset;
                        }
                        catch (BlockIoException)
                        {
                            outcomes[i] = SampleOutcome.ReadError;
                        }
                        Interlocked.Add(ref _s.BytesTested, blk);
                        PaintSample(map, CellOf(i), outcomes[i], details[i], alias[i], writeLat[i], readLat[i], blk);
                    }
                    MarkSlow(map, CellOf, outcomes, writeLat, readLat, n);
                }
            }

            // 4) إعادة البيانات الأصلية (حتى عند الإلغاء) ثم التحقق منها
            if (anyWritten)
            {
                restoredAll = Restore(r, offsets, written, outcomes, buf, blk);
            }
            else
            {
                _journal.Complete();
            }
        }
        catch (DeviceGoneException)
        {
            r.DeviceDisconnected = true;
            r.Note(anyWritten && !restoredAll ? "note.disconnectedRestorePending" : "note.disconnected");
            if (!anyWritten) _journal.Complete();
        }

        // الحصيلة
        var samples = new List<Sample>(n);
        for (int i = 0; i < n; i++)
        {
            var o = outcomes[i];
            if (o == SampleOutcome.Untested && !written[i] && !skip[i]) continue;
            if (o == SampleOutcome.Untested) continue;
            samples.Add(new Sample(offsets[i], blk, o, alias[i]));
        }
        var analysis = CapacityAnalyzer.Analyze(samples, _dev.Size);
        r.BytesTested = Interlocked.Read(ref _s.BytesTested);
        r.CoveragePercent = _dev.Size > 0 ? Math.Round(100.0 * samples.Count * blk / _dev.Size, 6) : 0;
        ResultBuilder.FillFromMap(r, map);
        ResultBuilder.ApplyVerdict(r, analysis, fullyVerified: false);
        r.Note("note.sampledCoverage", samples.Count, n);
        if (r.Cancelled) r.Note("note.cancelled");
        r.FinishedUtc = DateTime.UtcNow;
        _s.Phase = ScanPhase.Done;
        return r;
    }

    private bool Restore(ScanResult r, List<long> offsets, bool[] written, SampleOutcome[] outcomes, AlignedBuffer buf, int blk)
    {
        _s.SetStage(3, ScanPhase.Restoring, (long)offsets.Count * blk);
        int writeFailures = 0;
        // بالترتيب العكسي
        for (int i = offsets.Count - 1; i >= 0; i--)
        {
            if (!written[i]) continue;
            if (!_journal.TryLoad(i, buf.Span)) { writeFailures++; continue; }
            try { _dev.Write(offsets[i], buf.Span); }
            catch (BlockIoException) { writeFailures++; }
            _s.AddProgress(blk / 2);
        }
        _dev.Flush();

        int verifyFailuresGood = 0, verifyFailuresBad = 0;
        var expected = new byte[blk];
        for (int i = 0; i < offsets.Count; i++)
        {
            if (!written[i]) continue;
            _journal.TryLoad(i, expected);
            bool ok;
            try
            {
                _dev.Read(offsets[i], buf.Span);
                ok = buf.Span.SequenceEqual(expected);
            }
            catch (BlockIoException) { ok = false; }
            if (!ok)
            {
                if (outcomes[i] is SampleOutcome.Good or SampleOutcome.Slow) verifyFailuresGood++;
                else verifyFailuresBad++;
            }
            _s.AddProgress(blk / 2);
        }

        r.RestoreFailures = verifyFailuresGood + writeFailures;
        r.RestoreOk = r.RestoreFailures == 0;
        if (verifyFailuresBad > 0) r.Note("note.restoreFakeArea", verifyFailuresBad);
        if (r.RestoreOk == true)
        {
            _journal.Complete();
            r.Note("note.restored");
        }
        else
        {
            r.Note("note.restoreFailed", r.RestoreFailures);
        }
        return r.RestoreOk == true;
    }

    private static void PaintSample(ScanMap map, int cell, SampleOutcome o, string? detail, long? aliasOf, double wLat, double rLat, int blk)
    {
        map.Update(cell, c =>
        {
            c.BytesTested += blk;
            if (wLat > 0) { c.WriteLatencyMs = Math.Max(c.WriteLatencyMs, wLat); c.WriteMBps = Mbps(blk, wLat); }
            if (rLat > 0) { c.ReadLatencyMs = Math.Max(c.ReadLatencyMs, rLat); c.ReadMBps = Mbps(blk, rLat); }
        });
        map.Escalate(cell, ResultBuilder.ToCell(o), ResultBuilder.NoteFor(o, detail), aliasOf);
    }

    private void MarkSlow(ScanMap map, Func<int, int> cellOf, SampleOutcome[] outcomes, double[] w, double[] rd, int n)
    {
        var lat = new List<double>();
        for (int i = 0; i < n; i++)
            if (outcomes[i] == SampleOutcome.Good) lat.Add(w[i] + rd[i]);
        if (lat.Count < 8) return;
        lat.Sort();
        double median = lat[lat.Count / 2];
        double threshold = Math.Max(median * _opt.SlowFactor, _opt.SlowMinMs);
        for (int i = 0; i < n; i++)
        {
            if (outcomes[i] == SampleOutcome.Good && w[i] + rd[i] > threshold)
            {
                outcomes[i] = SampleOutcome.Slow;
                map.Escalate(cellOf(i), CellState.Slow, "cell.slow");
            }
        }
    }

    private void WaitPause(CancellationToken ct)
    {
        if (!_s.Pause.IsPaused) return;
        _s.MarkPaused(true);
        _s.Pause.WaitIfPaused(ct);
        _s.MarkPaused(false);
    }

    internal static double Mbps(long bytes, double ms) => ms <= 0 ? 0 : bytes / 1_000_000.0 / (ms / 1000.0);

    /// <summary>استعادة بيانات من سجل فحص سابق انقطع (بعد إعادة توصيل نفس الوحدة).</summary>
    public static (int restored, int failed) RestoreFromJournal(IBlockDevice dev, JournalHeader header, byte[] blob)
    {
        int ok = 0, fail = 0;
        int blk = header.BlockSize;
        using var buf = new AlignedBuffer(blk, Math.Max(4096, dev.Alignment));
        for (int i = header.Offsets.Count - 1; i >= 0; i--)
        {
            blob.AsSpan(i * blk, blk).CopyTo(buf.Span);
            try { dev.Write(header.Offsets[i], buf.Span); } catch (BlockIoException) { }
        }
        dev.Flush();
        for (int i = 0; i < header.Offsets.Count; i++)
        {
            try
            {
                dev.Read(header.Offsets[i], buf.Span);
                if (buf.Span.SequenceEqual(blob.AsSpan(i * blk, blk))) ok++; else fail++;
            }
            catch (BlockIoException) { fail++; }
        }
        return (ok, fail);
    }
}
