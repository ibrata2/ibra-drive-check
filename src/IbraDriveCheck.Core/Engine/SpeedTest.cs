using IbraDriveCheck.Core.Devices;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Engine;

public sealed class SpeedOptions
{
    public long FileBytes { get; set; } = 256L << 20;
    public int SeqBlockBytes { get; set; } = 4 << 20;
    public int RandomOps { get; set; } = 2000;
    public double RandomSeconds { get; set; } = 8;
    public long ReserveBytes { get; set; } = 64L << 20;
}

/// <summary>
/// اختبار السرعة بملف مؤقت في المساحة الفارغة (بدون كاش ويندوز): قراءة/كتابة متسلسلة، قراءة/كتابة عشوائية 4K، وزمن الاستجابة.
/// لا يحتاج صلاحيات مسؤول ولا يلمس الملفات الموجودة، ويحذف الملف بعد الانتهاء.
/// </summary>
public sealed class SpeedTest
{
    private readonly ITestFileSpace _fs;
    private readonly DriveSnapshot _drive;
    private readonly SpeedOptions _opt;
    private readonly ScanSession _s;

    public SpeedTest(ITestFileSpace fs, DriveSnapshot drive, SpeedOptions opt, ScanSession session)
    {
        _fs = fs; _drive = drive; _opt = opt; _s = session;
    }

    public ScanResult Run(CancellationToken ct)
    {
        var r = ResultBuilder.Start(ScanKind.Speed, _drive, _fs.TotalBytes);
        var res = new SpeedResult();
        r.Speed = res;
        int blk = _opt.SeqBlockBytes / 4096 * 4096;
        long size = Math.Min(_opt.FileBytes, _fs.FreeBytes - _opt.ReserveBytes);
        size = Math.Min(size, _fs.MaxFileBytes);
        size = size / blk * blk;
        if (size < blk * 4L)
        {
            r.Note("note.noFreeSpace");
            r.Verdict = Verdict.Inconclusive;
            r.FinishedUtc = DateTime.UtcNow;
            return r;
        }
        res.TestFileBytes = size;
        _s.PauseSupported = false;
        _s.StageCount = 4;
        string path = Path.Combine(_fs.Root, $"IBRA_SPEED_{Guid.NewGuid():N}.tmp");
        var nonce = TestPattern.NewNonce();
        using var buf = new AlignedBuffer(blk, 4096);
        using var small = new AlignedBuffer(4096, 4096);
        var rng = new Random();

        try
        {
            using (var f = _fs.Create(path, size))
            {
                // كتابة متسلسلة
                _s.SetStage(0, ScanPhase.SeqWrite, size);
                var samples = new List<double>();
                for (long off = 0; off < size && !ct.IsCancellationRequested; off += blk)
                {
                    TestPattern.FillRange(buf.Span, nonce, off / 4096, off);
                    long t0 = _s.Time.Timestamp;
                    f.Write(off, buf.Span);
                    double mbps = SampledCapacityTest.Mbps(blk, _s.Seconds(t0, _s.Time.Timestamp) * 1000);
                    samples.Add(mbps);
                    _s.CurrentWriteMBps = mbps;
                    _s.AddProgress(blk);
                }
                f.Flush();
                Summ(res.SeqWrite, samples, blk);

                // قراءة متسلسلة
                if (!ct.IsCancellationRequested)
                {
                    _s.SetStage(1, ScanPhase.SeqRead, size);
                    samples = new List<double>();
                    for (long off = 0; off < size && !ct.IsCancellationRequested; off += blk)
                    {
                        long t0 = _s.Time.Timestamp;
                        f.Read(off, buf.Span);
                        double mbps = SampledCapacityTest.Mbps(blk, _s.Seconds(t0, _s.Time.Timestamp) * 1000);
                        samples.Add(mbps);
                        _s.CurrentReadMBps = mbps;
                        _s.AddProgress(blk);
                    }
                    Summ(res.SeqRead, samples, blk);
                }

                // كتابة عشوائية 4K
                if (!ct.IsCancellationRequested)
                    RandomPass(f, size, small, rng, write: true, res.RandWrite4K, nonce, 2, ScanPhase.RandWrite, ct);
                // قراءة عشوائية 4K
                if (!ct.IsCancellationRequested)
                    RandomPass(f, size, small, rng, write: false, res.RandRead4K, nonce, 3, ScanPhase.RandRead, ct);
            }
        }
        catch (DeviceGoneException)
        {
            r.DeviceDisconnected = true;
            r.Note("note.disconnected");
        }
        catch (DiskFullException)
        {
            r.Note("note.noFreeSpace");
        }
        catch (BlockIoException ex)
        {
            r.Error = ex.Message;
            r.Note(ex.IsWrite ? "note.speedWriteError" : "note.speedReadError");
        }
        finally
        {
            _s.Phase = ScanPhase.Cleaning;
            try { _fs.Delete(path); } catch { r.Note("note.tempCleanFailed", path); }
        }

        r.Cancelled = ct.IsCancellationRequested;
        r.BytesTested = size * 2;
        r.AvgReadMBps = res.SeqRead.AvgMBps;
        r.AvgWriteMBps = res.SeqWrite.AvgMBps;
        r.MinReadMBps = res.SeqRead.MinMBps; r.MaxReadMBps = res.SeqRead.MaxMBps;
        r.MinWriteMBps = res.SeqWrite.MinMBps; r.MaxWriteMBps = res.SeqWrite.MaxMBps;

        if (r.Cancelled || r.DeviceDisconnected) r.Verdict = Verdict.Incomplete;
        else if (r.Error != null) r.Verdict = Verdict.ReadWriteErrors;
        else if (res.SeqWrite.Measured && (res.SeqWrite.AvgMBps < 4 || res.SeqRead.AvgMBps < 8)) { r.Verdict = Verdict.PoorPerformance; r.Note("note.slowDevice"); }
        else r.Verdict = Verdict.Genuine;
        if (r.Verdict == Verdict.Genuine) r.Note("note.speedOnly");
        r.FinishedUtc = DateTime.UtcNow;
        _s.Phase = ScanPhase.Done;
        return r;
    }

    /// <summary>
    /// اختبار سرعة للقراءة فقط مباشرة من الوحدة (للوحدات الجديدة غير المهيأة بدون قسم). لا يكتب أي شيء.
    /// </summary>
    public static ScanResult RunRawRead(IBlockDevice dev, DriveSnapshot drive, SpeedOptions opt, ScanSession s, CancellationToken ct)
    {
        var r = ResultBuilder.Start(ScanKind.Speed, drive, dev.Size);
        var res = new SpeedResult { RawReadOnly = true };
        r.Speed = res;
        int align = Math.Max(4096, dev.Alignment);
        int blk = Math.Max(align, opt.SeqBlockBytes / align * align);
        long size = Math.Min(opt.FileBytes, dev.Size / 2) / blk * blk;
        s.PauseSupported = false;
        s.StageCount = 2;
        using var buf = new AlignedBuffer(blk, align);
        using var small = new AlignedBuffer(align, align);
        try
        {
            // قراءة متسلسلة من منتصف الوحدة تقريباً (بعيداً عن جدول الأقسام)
            long start = dev.Size / 4 / blk * blk;
            s.SetStage(0, ScanPhase.SeqRead, size);
            var samples = new List<double>();
            for (long off = start; off < start + size && !ct.IsCancellationRequested; off += blk)
            {
                long t0 = s.Time.Timestamp;
                dev.Read(off, buf.Span);
                double mbps = SampledCapacityTest.Mbps(blk, s.Seconds(t0, s.Time.Timestamp) * 1000);
                samples.Add(mbps);
                s.CurrentReadMBps = mbps;
                s.AddProgress(blk);
            }
            Summ(res.SeqRead, samples, blk);

            s.SetStage(1, ScanPhase.RandRead, opt.RandomOps);
            var rng = new Random();
            long blocks = dev.Size / align;
            var lat = new List<double>();
            long begin = s.Time.Timestamp;
            for (int i = 0; i < opt.RandomOps && !ct.IsCancellationRequested; i++)
            {
                long off = rng.NextInt64(0, blocks) * align;
                long t0 = s.Time.Timestamp;
                dev.Read(off, small.Span);
                double ms = s.Seconds(t0, s.Time.Timestamp) * 1000;
                lat.Add(ms);
                s.CurrentReadMBps = SampledCapacityTest.Mbps(align, ms);
                s.AddProgress(1);
                if (s.Seconds(begin, s.Time.Timestamp) > opt.RandomSeconds) break;
            }
            if (lat.Count > 0)
            {
                var st = res.RandRead4K;
                double totalSec = lat.Sum() / 1000;
                st.Measured = true;
                st.Iops = Math.Round(lat.Count / Math.Max(totalSec, 1e-9), 1);
                st.AvgMBps = Math.Round(st.Iops * align / 1_000_000, 3);
                st.AvgLatencyMs = Math.Round(lat.Average(), 3);
                st.MinLatencyMs = Math.Round(lat.Min(), 3);
                st.MaxLatencyMs = Math.Round(lat.Max(), 3);
            }
        }
        catch (DeviceGoneException) { r.DeviceDisconnected = true; r.Note("note.disconnected"); }
        catch (BlockIoException ex) { r.Error = ex.Message; r.Note("note.speedReadError"); }

        r.Cancelled = ct.IsCancellationRequested;
        r.BytesTested = size;
        r.AvgReadMBps = res.SeqRead.AvgMBps;
        r.MinReadMBps = res.SeqRead.MinMBps; r.MaxReadMBps = res.SeqRead.MaxMBps;
        if (r.Cancelled || r.DeviceDisconnected) r.Verdict = Verdict.Incomplete;
        else if (r.Error != null) r.Verdict = Verdict.ReadWriteErrors;
        else r.Verdict = Verdict.Genuine;
        r.Note("speed.rawNote");
        r.FinishedUtc = DateTime.UtcNow;
        s.Phase = ScanPhase.Done;
        return r;
    }

    private void RandomPass(ITestFile f, long size, AlignedBuffer small, Random rng, bool write, SpeedStat stat, byte[] nonce, int stage, ScanPhase phase, CancellationToken ct)
    {
        _s.SetStage(stage, phase, _opt.RandomOps);
        long blocks = size / 4096;
        var lat = new List<double>();
        long start = _s.Time.Timestamp;
        for (int i = 0; i < _opt.RandomOps && !ct.IsCancellationRequested; i++)
        {
            long b = rng.NextInt64(0, blocks);
            long off = b * 4096;
            if (write) TestPattern.Fill(small.Span, nonce, b, off);
            long t0 = _s.Time.Timestamp;
            if (write) f.Write(off, small.Span); else f.Read(off, small.Span);
            double ms = _s.Seconds(t0, _s.Time.Timestamp) * 1000;
            lat.Add(ms);
            if (write) _s.CurrentWriteMBps = SampledCapacityTest.Mbps(4096, ms); else _s.CurrentReadMBps = SampledCapacityTest.Mbps(4096, ms);
            _s.AddProgress(1);
            if (_s.Seconds(start, _s.Time.Timestamp) > _opt.RandomSeconds) break;
        }
        if (write) f.Flush();
        if (lat.Count == 0) return;
        double totalSec = lat.Sum() / 1000;
        stat.Measured = true;
        stat.Iops = Math.Round(lat.Count / Math.Max(totalSec, 1e-9), 1);
        stat.AvgMBps = Math.Round(stat.Iops * 4096 / 1_000_000, 3);
        stat.AvgLatencyMs = Math.Round(lat.Average(), 3);
        stat.MinLatencyMs = Math.Round(lat.Min(), 3);
        stat.MaxLatencyMs = Math.Round(lat.Max(), 3);
        var mb = lat.Where(l => l > 0).Select(l => 4096 / 1_000_000.0 / (l / 1000)).ToList();
        if (mb.Count > 0) { stat.MinMBps = Math.Round(mb.Min(), 3); stat.MaxMBps = Math.Round(mb.Max(), 3); }
        stat.Samples = Downsample(mb, 120);
    }

    private static void Summ(SpeedStat st, List<double> samples, int blk)
    {
        var v = samples.Where(x => x > 0 && !double.IsInfinity(x)).ToList();
        if (v.Count == 0) return;
        st.Measured = true;
        // المتوسط الحقيقي = إجمالي البيانات ÷ إجمالي الزمن
        double totalSec = v.Sum(x => blk / 1_000_000.0 / x);
        st.AvgMBps = Math.Round(v.Count * blk / 1_000_000.0 / totalSec, 2);
        st.MinMBps = Math.Round(v.Min(), 2);
        st.MaxMBps = Math.Round(v.Max(), 2);
        var lat = v.Select(x => blk / 1_000_000.0 / x * 1000).ToList();
        st.AvgLatencyMs = Math.Round(lat.Average(), 2);
        st.MinLatencyMs = Math.Round(lat.Min(), 2);
        st.MaxLatencyMs = Math.Round(lat.Max(), 2);
        st.Samples = Downsample(v, 120);
    }

    private static List<double> Downsample(List<double> v, int max)
    {
        if (v.Count <= max) return v.Select(x => Math.Round(x, 2)).ToList();
        var o = new List<double>(max);
        for (int i = 0; i < max; i++)
        {
            int a = v.Count * i / max, b = Math.Max(a + 1, v.Count * (i + 1) / max);
            o.Add(Math.Round(v.Skip(a).Take(b - a).Average(), 2));
        }
        return o;
    }
}
