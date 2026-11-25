using IbraDriveCheck.Core.Devices;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Engine;

public sealed class FreeSpaceOptions
{
    public int ChunkBytes { get; set; } = 4 << 20;
    public long FileBytes { get; set; } = 1L << 30;         // ملفات 1GB
    public long ReserveBytes { get; set; } = 64L << 20;     // نترك 64MB فارغة لنظام الملفات
    public long? MaxTestBytes { get; set; }                 // لتحديد حجم الاختبار (للاختبارات)
    public int MapCells { get; set; } = 2048;
    public double SlowFraction { get; set; } = 0.15;
}

/// <summary>
/// فحص السعة الكامل غير المدمر: يملأ المساحة الفارغة بملفات اختبار فريدة، يقرؤها كلها ويتحقق، ثم يحذفها.
/// لا يلمس الملفات الموجودة مباشرة — لكن على وحدة مزيفة قد تتلف ملفات موجودة لأن الذاكرة تكتب فوق نفسها.
/// </summary>
public sealed class FreeSpaceFileTest
{
    public const string FolderPrefix = "IBRA_DRIVE_CHECK_TEST_";
    private readonly ITestFileSpace _fs;
    private readonly DriveSnapshot _drive;
    private readonly FreeSpaceOptions _opt;
    private readonly ScanSession _s;

    public FreeSpaceFileTest(ITestFileSpace fs, DriveSnapshot drive, FreeSpaceOptions opt, ScanSession session)
    {
        _fs = fs; _drive = drive; _opt = opt; _s = session;
    }

    public ScanResult Run(CancellationToken ct)
    {
        long total = _fs.TotalBytes;
        long free = _fs.FreeBytes;
        long used = Math.Max(0, total - free);
        var r = ResultBuilder.Start(ScanKind.FullFreeSpace, _drive, total);
        int chunk = _opt.ChunkBytes / TestPattern.BlockSize * TestPattern.BlockSize;
        long plan = free - _opt.ReserveBytes;
        if (_opt.MaxTestBytes is long cap) plan = Math.Min(plan, cap);
        plan = Math.Max(0, plan / chunk * chunk);
        long fileBytes = Math.Min(_opt.FileBytes, _fs.MaxFileBytes) / chunk * chunk;
        if (fileBytes <= 0) fileBytes = chunk;

        if (plan < chunk)
        {
            r.Note("note.noFreeSpace");
            r.Verdict = Verdict.Inconclusive;
            r.FinishedUtc = DateTime.UtcNow;
            return r;
        }

        var map = ScanMap.Uniform(0, plan, _opt.MapCells, chunk);
        _s.Map = map;
        _s.StageCount = 2;
        var nonce = TestPattern.NewNonce();
        string dir = Path.Combine(_fs.Root, FolderPrefix + Convert.ToHexString(nonce, 0, 4));
        var files = new List<(string path, long start, long len)>();
        var outcome = new Dictionary<long, (SampleOutcome o, long? alias)>();
        long writtenBytes = 0;
        using var buf = new AlignedBuffer(chunk, 4096);

        try
        {
            _fs.CreateDirectory(dir);
            r.Note("note.tempFolder", dir);

            // 1) كتابة ملفات الاختبار
            _s.SetStage(0, ScanPhase.Writing, plan);
            long pos = 0;
            int fileIndex = 0;
            bool full = false;
            while (pos < plan && !full)
            {
                if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                long flen = Math.Min(fileBytes, plan - pos);
                string path = Path.Combine(dir, $"IBRA_{fileIndex:D5}.dat");
                using (var f = _fs.Create(path, flen))
                {
                    files.Add((path, pos, 0));
                    long written = 0;
                    for (long o = 0; o < flen; o += chunk)
                    {
                        if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                        WaitPause(ct);
                        int len = (int)Math.Min(chunk, flen - o);
                        long g = pos + o;
                        var span = buf.Slice(0, len);
                        TestPattern.FillRange(span, nonce, g / TestPattern.BlockSize, g);
                        int cell = map.IndexOf(g);
                        map.Update(cell, c => { if (c.State == CellState.Untested) c.State = CellState.Testing; });
                        long t0 = _s.Time.Timestamp;
                        try
                        {
                            f.Write(o, span);
                            double mbps = SampledCapacityTest.Mbps(len, _s.Seconds(t0, _s.Time.Timestamp) * 1000);
                            _s.CurrentWriteMBps = mbps;
                            map.Update(cell, c => c.WriteMBps = c.WriteMBps <= 0 ? mbps : (c.WriteMBps + mbps) / 2);
                            written += len;
                        }
                        catch (DiskFullException)
                        {
                            full = true;
                            r.Note("note.diskFullEarly");
                            break;
                        }
                        catch (BlockIoException)
                        {
                            outcome[g] = (SampleOutcome.WriteError, null);
                            map.Escalate(cell, CellState.WriteError, "cell.writeError");
                            written += len;
                        }
                        Interlocked.Add(ref _s.BytesTested, len);
                        _s.AddProgress(len);
                    }
                    f.Flush();
                    files[^1] = (path, pos, written);
                    writtenBytes = pos + written;
                    pos += flen;
                }
                fileIndex++;
            }

            // 2) القراءة والتحقق
            if (!r.Cancelled)
            {
                _s.SetStage(1, ScanPhase.Verifying, writtenBytes);
                foreach (var (path, start, len) in files)
                {
                    if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                    if (len <= 0) continue;
                    using var f = _fs.OpenRead(path);
                    for (long o = 0; o < len; o += chunk)
                    {
                        if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                        WaitPause(ct);
                        int l = (int)Math.Min(chunk, len - o);
                        long g = start + o;
                        int cell = map.IndexOf(g);
                        _s.AddProgress(l);
                        if (outcome.ContainsKey(g)) continue;
                        var span = buf.Slice(0, l);
                        long t0 = _s.Time.Timestamp;
                        try
                        {
                            f.Read(o, span);
                        }
                        catch (BlockIoException)
                        {
                            outcome[g] = (SampleOutcome.ReadError, null);
                            map.Escalate(cell, CellState.ReadError, "cell.readError");
                            continue;
                        }
                        double mbps = SampledCapacityTest.Mbps(l, _s.Seconds(t0, _s.Time.Timestamp) * 1000);
                        _s.CurrentReadMBps = mbps;
                        map.Update(cell, c => { c.ReadMBps = c.ReadMBps <= 0 ? mbps : (c.ReadMBps + mbps) / 2; c.BytesTested += l; });
                        Interlocked.Add(ref _s.BytesTested, l);

                        var worst = SampleOutcome.Good; long? aliasOf = null; string? detail = null;
                        for (int b = 0; b < l / TestPattern.BlockSize; b++)
                        {
                            long gb = g + (long)b * TestPattern.BlockSize;
                            var chk = TestPattern.Verify(span.Slice(b * TestPattern.BlockSize, TestPattern.BlockSize), nonce, gb / TestPattern.BlockSize, gb);
                            if (chk.Status == BlockStatus.Ok) continue;
                            worst = CapacityAnalyzer.FromCheck(chk.Status);
                            detail = chk.Detail;
                            if (chk.Status == BlockStatus.Aliased) aliasOf = chk.FoundOffset - (long)b * TestPattern.BlockSize;
                            break;
                        }
                        outcome[g] = (worst, aliasOf);
                        map.Escalate(cell, ResultBuilder.ToCell(worst), ResultBuilder.NoteFor(worst, detail), aliasOf);
                    }
                }
                MarkSlow(map);
            }
        }
        catch (DeviceGoneException)
        {
            r.DeviceDisconnected = true;
            r.Note("note.disconnected");
        }
        finally
        {
            // 3) حذف ملفات الاختبار دائماً
            _s.Phase = ScanPhase.Cleaning;
            try
            {
                foreach (var f in files) { try { _fs.Delete(f.path); } catch { } }
                _fs.DeleteDirectory(dir);
                r.Note("note.tempCleaned");
            }
            catch
            {
                r.Note("note.tempCleanFailed", dir);
            }
        }

        var samples = outcome.OrderBy(k => k.Key).Select(k =>
        {
            int cell = map.IndexOf(k.Key);
            var o = k.Value.o;
            if (o == SampleOutcome.Good && cell >= 0 && map.Cells[cell].State == CellState.Slow) o = SampleOutcome.Slow;
            return new Sample(k.Key, Math.Min(chunk, plan - k.Key), o, k.Value.alias);
        }).ToList();

        var a = CapacityAnalyzer.Analyze(samples, plan);
        bool complete = !r.Cancelled && !r.DeviceDisconnected;
        long goodBytes = samples.Where(s => s.IsGood).Sum(s => s.Length);
        r.VerifiedBytes = used + a.ContiguousGoodEnd;
        r.BytesTested = Interlocked.Read(ref _s.BytesTested);
        r.SampleCount = samples.Count;
        r.CoveragePercent = total > 0 ? Math.Round(100.0 * samples.Sum(s => s.Length) / total, 3) : 0;
        ResultBuilder.FillFromMap(r, map);
        ResultBuilder.ApplyVerdict(r, a, fullyVerified: complete);

        if (a.FakeDetected)
        {
            // السعة الحقيقية = المساحة المستخدمة + ما تم تخزينه واسترجاعه بشكل صحيح
            long est = a.Aliased > 0 ? used + goodBytes : used + (a.EstimatedReal ?? a.ContiguousGoodEnd);
            r.EstimatedRealBytes = Math.Min(total, est);
            r.EstimatedFakeBytes = Math.Max(0, total - r.EstimatedRealBytes.Value);
            r.EstimateFromWrap = false;
            r.Notes.RemoveAll(n => n.Key is "note.estimateRange" or "note.wrapDetected");
            r.Note("note.existingFilesRisk");
        }
        if (complete && r.Verdict == Verdict.Genuine) r.Note("note.freeSpaceVerified", used);
        if (r.Cancelled) r.Note("note.cancelled");
        r.FinishedUtc = DateTime.UtcNow;
        _s.Phase = ScanPhase.Done;
        return r;
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

    private void WaitPause(CancellationToken ct)
    {
        if (!_s.Pause.IsPaused) return;
        _s.MarkPaused(true);
        _s.Pause.WaitIfPaused(ct);
        _s.MarkPaused(false);
    }

    /// <summary>بقايا مجلدات اختبار من تشغيل سابق انقطع (تُعرض على المستخدم لحذفها).</summary>
    public static string[] FindLeftovers(string root)
    {
        try { return Directory.GetDirectories(root, FolderPrefix + "*"); } catch { return Array.Empty<string>(); }
    }
}
