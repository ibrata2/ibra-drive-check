using System.Diagnostics;
using System.Reflection;
using IbraDriveCheck.Core.Engine;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Reports;
using IbraDriveCheck.Core.Safety;
using IbraDriveCheck.Core.Storage;
using IbraDriveCheck.Tests;

// مشغل اختبارات بسيط: كل دالة تبدأ بـ T_ هي اختبار
var tests = typeof(Tests).GetMethods(BindingFlags.Public | BindingFlags.Static)
    .Where(m => m.Name.StartsWith("T_")).OrderBy(m => m.MetadataToken).ToList();
string? filter = args.FirstOrDefault();
int pass = 0, fail = 0, skip = 0;
var total = Stopwatch.StartNew();
foreach (var t in tests)
{
    if (filter != null && !t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
    var sw = Stopwatch.StartNew();
    try
    {
        t.Invoke(null, null);
        pass++;
        Console.WriteLine($"  PASS  {t.Name}  ({sw.ElapsedMilliseconds} ms)");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is SkipException)
    {
        skip++;
        Console.WriteLine($"  SKIP  {t.Name}: {ex.InnerException.Message}");
    }
    catch (TargetInvocationException ex)
    {
        fail++;
        Console.WriteLine($"  FAIL  {t.Name}: {ex.InnerException?.Message}");
        foreach (var line in (ex.InnerException?.StackTrace ?? "").Split('\n').Take(6)) Console.WriteLine("        " + line.Trim());
    }
}
Console.WriteLine($"\n{pass} passed, {fail} failed, {skip} skipped in {total.Elapsed.TotalSeconds:0.0}s");
return fail == 0 ? 0 : 1;

namespace IbraDriveCheck.Tests
{
    public sealed class SkipException : Exception { public SkipException(string m) : base(m) { } }

    public static class Assert
    {
        public static void True(bool c, string msg) { if (!c) throw new Exception(msg); }
        public static void Eq<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
        }
    }

    public static partial class Tests
    {
        public const long KB = 1024, MB = 1024 * KB, GB = 1024 * MB, TB = 1024 * GB;

        public static DriveSnapshot UsbDrive(long size) => new()
        {
            DiskNumber = 7,
            FriendlyName = "SIM USB",
            Model = "SIM USB",
            HardwareProduct = "SIM USB",
            HardwareSerial = "SIM123",
            SerialNumber = "SIM123",
            BusType = "USB",
            Kind = MediaKind.UsbFlash,
            DeclaredBytes = size,
            WriteProtected = false,
            Volumes = { new VolumeSnapshot { Letter = "Z:", FileSystem = "exFAT", TotalBytes = size, FreeBytes = size / 2 } },
        };

        public static ScanSession Session(SimClock clock) => new(clock);

        public static string Describe(ScanResult r) =>
            $"{r.Verdict} est={r.EstimatedRealBytes} verified={r.VerifiedBytes} good={r.GoodCells} bad={r.FailedCells} we={r.WriteErrorCells} re={r.ReadErrorCells} slow={r.SlowCells} restore={r.RestoreOk} notes=[{string.Join(", ", r.Notes.Select(n => n.Key))}]";

        static string Temp(string name)
        {
            var p = Path.Combine(Path.GetTempPath(), "ibra-dc-tests", name + "_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(p);
            return p;
        }

        // ───────────────────────── النمط ─────────────────────────

        public static void T_Pattern_RoundTrip_And_Detection()
        {
            var nonce = TestPattern.NewNonce();
            var b = new byte[4096];
            TestPattern.Fill(b, nonce, 42, 42 * 4096);
            Assert.Eq(BlockStatus.Ok, TestPattern.Verify(b, nonce, 42, 42 * 4096).Status, "ok");
            var alias = TestPattern.Verify(b, nonce, 99, 99 * 4096);
            Assert.Eq(BlockStatus.Aliased, alias.Status, "alias");
            Assert.Eq(42L * 4096, alias.FoundOffset, "alias offset");
            b[1000] ^= 0x01;
            Assert.Eq(BlockStatus.Corrupted, TestPattern.Verify(b, nonce, 42, 42 * 4096).Status, "bit flip");
            Assert.Eq("zeros", TestPattern.Verify(new byte[4096], nonce, 1, 4096).Detail, "zeros");
            var other = TestPattern.NewNonce();
            TestPattern.Fill(b, other, 42, 42 * 4096);
            Assert.Eq(BlockStatus.Missing, TestPattern.Verify(b, nonce, 42, 42 * 4096).Status, "stale data from another session");
        }

        // ───────────────────────── التحليل ─────────────────────────

        public static void T_Analyzer_Rules()
        {
            var good = Enumerable.Range(0, 10).Select(i => new Sample(i * GB, 4096, SampleOutcome.Good)).ToList();
            var a = CapacityAnalyzer.Analyze(good, 10 * GB);
            Assert.True(!a.FakeDetected && !a.ScatteredErrors, "all good");

            var step = Enumerable.Range(0, 10).Select(i => new Sample(i * GB, 4096, i < 4 ? SampleOutcome.Good : SampleOutcome.Missing)).ToList();
            a = CapacityAnalyzer.Analyze(step, 10 * GB);
            Assert.True(a.FakeDetected && a.StepPattern, "step => fake");
            Assert.Eq(3 * GB + 4096, a.EstimatedReal, "lower bound");

            var one = Enumerable.Range(0, 10).Select(i => new Sample(i * GB, 4096, i == 9 ? SampleOutcome.Missing : SampleOutcome.Good)).ToList();
            a = CapacityAnalyzer.Analyze(one, 10 * GB);
            Assert.True(!a.FakeDetected, "a single bad sample is not enough for a fake verdict");
            Assert.True(a.ScatteredErrors, "single bad => errors");

            var allBad = Enumerable.Range(0, 5).Select(i => new Sample(i * GB, 4096, SampleOutcome.WriteError)).ToList();
            a = CapacityAnalyzer.Analyze(allBad, 5 * GB);
            Assert.True(a.AllBad && !a.FakeDetected, "all bad");

            Assert.Eq(0, CapacityAnalyzer.Analyze(new List<Sample>(), GB).Tested, "empty");
        }

        public static void T_Plan_Offsets_On_Power_Of_Two_Lattice()
        {
            var opt = SampledOptions.Quick();
            var offs = SampledCapacityTest.PlanOffsets(2 * TB, opt, 4096);
            Assert.True(offs.Count >= 576 && offs.Count <= 2 * 576 + 1, $"count {offs.Count}");
            long stride = offs[1] - offs[0];
            Assert.True((stride & (stride - 1)) == 0, "stride power of two");
            for (int i = 1; i < offs.Count - 1; i++) Assert.Eq(stride, offs[i] - offs[i - 1], "uniform");
            Assert.True(offs[0] >= MB, "protected head");
            Assert.True(offs[^1] + 4096 <= 2 * TB - MB, "protected tail");
            Assert.True(offs.All(o => o % 4096 == 0), "aligned");
        }

        // ───────────────────────── الفحص السريع/العميق ─────────────────────────

        static (ScanResult r, SimulatedDevice dev, MemoryJournal j) RunSampled(SimulatedDevice dev, ScanKind kind = ScanKind.Quick, CancellationToken ct = default, ScanSession? s = null, SimClock? clock = null)
        {
            var j = new MemoryJournal();
            var opt = kind == ScanKind.Deep ? SampledOptions.Deep() : SampledOptions.Quick();
            var r = new SampledCapacityTest(dev, UsbDrive(dev.Size), kind, opt, j, s ?? new ScanSession(clock)).Run(ct);
            return (r, dev, j);
        }

        public static void T_Sampled_Genuine_Drive()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(32 * GB, 32 * GB, FakeMode.Genuine, clock);
            var (r, _, j) = RunSampled(dev, clock: clock);
            Assert.Eq(Verdict.Genuine, r.Verdict, Describe(r));
            Assert.True(r.RestoreOk == true && j.Completed && j.Committed, "restore + journal");
            Assert.Eq(0, dev.Media.CountModified(), "original data preserved");
            Assert.True(r.EstimatedRealBytes == null, "no estimate for genuine");
            Assert.True(r.Notes.Any(n => n.Key == "note.genuineSampled"), "sampled disclaimer");
        }

        public static void T_Sampled_Fake_2TB_Wrapping_To_64GB()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(2 * TB, 64 * GB, FakeMode.Wrap, clock);
            var (r, _, _) = RunSampled(dev, clock: clock);
            Assert.Eq(Verdict.FakeCapacity, r.Verdict, Describe(r));
            Assert.Eq(64 * GB, r.EstimatedRealBytes, "estimated real via wrap");
            Assert.True(r.EstimateFromWrap, "wrap flag");
            Assert.Eq(2 * TB - 64 * GB, r.EstimatedFakeBytes, "fake bytes");
            Assert.True(r.RestoreOk == true, Describe(r));
            Assert.Eq(0, dev.Media.CountModified(), "original data preserved on the real part");
        }

        public static void T_Deep_Fake_Wrapping_Non_Power_Of_Two_Declared()
        {
            var clock = new SimClock();
            long declared = 1_000_000_000_000L / 4096 * 4096; // "1TB" على العلبة
            var dev = new SimulatedDevice(declared, 32 * GB, FakeMode.Wrap, clock);
            var (r, _, _) = RunSampled(dev, ScanKind.Deep, clock: clock);
            Assert.Eq(Verdict.FakeCapacity, r.Verdict, Describe(r));
            Assert.Eq(32 * GB, r.EstimatedRealBytes, "wrap modulus");
            Assert.Eq(0, dev.Media.CountModified(), "data preserved");
        }

        public static void T_Sampled_Fake_Dropped_Writes_Zeros()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(256 * GB, 30 * GB, FakeMode.DropZeros, clock);
            var (r, _, _) = RunSampled(dev, clock: clock);
            Assert.Eq(Verdict.FakeCapacity, r.Verdict, Describe(r));
            Assert.True(r.EstimatedRealBytes <= 30 * GB && r.EstimatedRealBytes >= 30 * GB - 512 * MB, $"estimate {r.EstimatedRealBytes}");
            Assert.True(r.Map.Any(c => c.NoteKey == "cell.zeros"), "zeros note");
            Assert.Eq(0, dev.Media.CountModified(), "data preserved");
        }

        public static void T_Sampled_Fake_Rejects_Writes_Beyond_Real()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(128 * GB, 16 * GB, FakeMode.WriteReject, clock);
            var (r, _, _) = RunSampled(dev, clock: clock);
            Assert.Eq(Verdict.FakeCapacity, r.Verdict, Describe(r));
            Assert.True(r.WriteErrorCells > 0, "write errors shown orange");
        }

        public static void T_Sampled_Scattered_Read_Errors()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(16 * GB, 16 * GB, FakeMode.Genuine, clock)
            {
                ReadErrorAt = off => off is >= 3 * GB and < 3 * GB + 64 * MB || off is >= 9 * GB and < 9 * GB + 64 * MB,
            };
            var (r, _, _) = RunSampled(dev, clock: clock);
            Assert.Eq(Verdict.ReadWriteErrors, r.Verdict, Describe(r));
            Assert.True(r.ReadErrorCells > 0, "purple cells");
            Assert.Eq(0, dev.Media.CountModified(), "unreadable areas were never written");
        }

        public static void T_Sampled_Disconnect_Then_Restore_From_Journal()
        {
            var clock = new SimClock();
            var media = new SimMedia();
            var dev = new SimulatedDevice(64 * GB, 64 * GB, FakeMode.Genuine, clock, media);
            long readsBefore = SampledCapacityTest.PlanOffsets(dev.Size, SampledOptions.Quick(), 4096).Count;
            dev.DisconnectAfterOps = readsBefore + 200; // ينقطع أثناء الكتابة
            string dir = Temp("journal");
            var drive = UsbDrive(dev.Size);
            var journal = new FileJournal(dir, drive.DisplayName);
            var r = new SampledCapacityTest(dev, drive, ScanKind.Quick, SampledOptions.Quick(), journal, new ScanSession(clock)).Run(default);
            Assert.Eq(Verdict.Incomplete, r.Verdict, Describe(r));
            Assert.True(r.DeviceDisconnected, "disconnected flag");
            Assert.True(r.Notes.Any(n => n.Key == "note.disconnectedRestorePending"), "pending restore note");
            Assert.True(media.CountModified() > 0, "test data left on device after disconnect");

            var pending = FileJournal.FindPending(dir);
            Assert.Eq(1, pending.Count, "journal kept on disk");
            Assert.Eq(drive.HardwareKey, pending[0].Header.IdentityKey, "journal identity");
            var again = new SimulatedDevice(64 * GB, 64 * GB, FakeMode.Genuine, clock, media); // إعادة توصيل
            var (ok, failed) = SampledCapacityTest.RestoreFromJournal(again, pending[0].Header, FileJournal.LoadBlob(pending[0].BasePath));
            Assert.Eq(0, failed, "restore failures");
            Assert.True(ok > 0, "restored count");
            Assert.Eq(0, media.CountModified(), "all original data back");
            FileJournal.Delete(pending[0].BasePath);
            Assert.Eq(0, FileJournal.FindPending(dir).Count, "journal removed");
        }

        public static void T_Sampled_Cancel_During_Write_Restores_Data()
        {
            var clock = new SimClock();
            var cts = new CancellationTokenSource();
            var dev = new SimulatedDevice(64 * GB, 64 * GB, FakeMode.Genuine, clock);
            dev.OnOp = (ops, write) => { if (write && dev.Writes > 100) cts.Cancel(); };
            var (r, _, j) = RunSampled(dev, ct: cts.Token, clock: clock);
            Assert.True(r.Cancelled, "cancelled");
            Assert.Eq(Verdict.Incomplete, r.Verdict, Describe(r));
            Assert.True(r.RestoreOk == true && j.Completed, "restored after cancel");
            Assert.Eq(0, dev.Media.CountModified(), "data intact after cancel");
        }

        public static void T_Sampled_Cancel_Before_Writing_Touches_Nothing()
        {
            var clock = new SimClock();
            var cts = new CancellationTokenSource();
            var dev = new SimulatedDevice(64 * GB, 64 * GB, FakeMode.Genuine, clock);
            dev.OnOp = (ops, _) => { if (ops == 10) cts.Cancel(); };
            var (r, _, _) = RunSampled(dev, ct: cts.Token, clock: clock);
            Assert.True(r.Cancelled && dev.Writes == 0, "no writes");
            Assert.Eq(Verdict.Incomplete, r.Verdict, Describe(r));
        }

        public static void T_Sampled_Pause_And_Resume()
        {
            var clock = new SimClock();
            var s = new ScanSession(clock);
            var dev = new SimulatedDevice(16 * GB, 16 * GB, FakeMode.Genuine, clock);
            bool paused = false;
            dev.OnOp = (ops, _) =>
            {
                if (ops == 300 && !paused)
                {
                    paused = true;
                    s.Pause.Pause();
                    Task.Run(async () => { await Task.Delay(150); s.Pause.Resume(); });
                }
            };
            var sw = Stopwatch.StartNew();
            var (r, _, _) = RunSampled(dev, s: s, clock: clock);
            Assert.True(paused && sw.ElapsedMilliseconds >= 140, "actually paused");
            Assert.Eq(Verdict.Genuine, r.Verdict, Describe(r));
        }

        public static void T_Sampled_Slow_Regions_Poor_Performance()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(32 * GB, 32 * GB, FakeMode.Genuine, clock)
            {
                SlowRange = (4 * GB, 20 * GB, 400_000), // 400ms إضافية لكل عملية
            };
            var (r, _, _) = RunSampled(dev, clock: clock);
            Assert.True(r.SlowCells > 0, "yellow cells");
            Assert.Eq(Verdict.PoorPerformance, r.Verdict, Describe(r));
        }

        // ───────────────────────── الفحص الكامل المدمر ─────────────────────────

        static ScanResult RunFull(SimulatedDevice dev, SimClock clock)
            => new FullSurfaceTest(dev, UsbDrive(dev.Size), new FullSurfaceOptions { ChunkBytes = 1 << 20, MapCells = 256 }, new ScanSession(clock)).Run(default);

        public static void T_Full_Genuine_Verifies_Every_Byte()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(64 * MB, 64 * MB, FakeMode.Genuine, clock);
            var r = RunFull(dev, clock);
            Assert.Eq(Verdict.Genuine, r.Verdict, Describe(r));
            Assert.Eq(64 * MB, r.VerifiedBytes, "verified = everything");
            Assert.True(r.Notes.Any(n => n.Key == "note.fullVerified"), "full note");
        }

        public static void T_Full_Fake_Wrap()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(96 * MB, 16 * MB, FakeMode.Wrap, clock);
            var r = RunFull(dev, clock);
            Assert.Eq(Verdict.FakeCapacity, r.Verdict, Describe(r));
            Assert.Eq(16 * MB, r.VerifiedBytes, "verified real part");
            Assert.Eq(16 * MB, r.EstimatedRealBytes, "estimate");
        }

        public static void T_Full_Fake_Drop_FF()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(64 * MB, 20 * MB, FakeMode.DropFF, clock);
            var r = RunFull(dev, clock);
            Assert.Eq(Verdict.FakeCapacity, r.Verdict, Describe(r));
            Assert.Eq(20 * MB, r.VerifiedBytes, "verified");
        }

        public static void T_Full_Disconnect_Is_Incomplete()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(64 * MB, 64 * MB, FakeMode.Genuine, clock) { DisconnectAfterOps = 30 };
            var r = RunFull(dev, clock);
            Assert.Eq(Verdict.Incomplete, r.Verdict, Describe(r));
            Assert.True(r.DeviceDisconnected, "flag");
        }

        // ───────────────────────── المساحة الفارغة ─────────────────────────

        public static void T_FreeSpace_Real_Files_Genuine_And_Cleaned()
        {
            string dir = Temp("free");
            var fs = new RealFileSpace(dir);
            var r = new FreeSpaceFileTest(fs, UsbDrive(64 * GB),
                new FreeSpaceOptions { MaxTestBytes = 48 * MB, FileBytes = 16 * MB, ChunkBytes = 4 << 20, MapCells = 64 },
                new ScanSession()).Run(default);
            Assert.Eq(Verdict.Genuine, r.Verdict, Describe(r));
            Assert.Eq(0, Directory.GetDirectories(dir).Length, "test folder removed");
            Assert.True(r.BytesTested >= 96 * MB, "wrote and read 48MB");
        }

        public static void T_FreeSpace_Fake_Wrap_Detected_And_Cleaned()
        {
            var fs = new SimFileSpace("/sim", total: 128 * MB, used: 8 * MB, real: 40 * MB, wrap: true);
            var r = new FreeSpaceFileTest(fs, UsbDrive(128 * MB),
                new FreeSpaceOptions { FileBytes = 16 * MB, ChunkBytes = 1 << 20, ReserveBytes = 4 * MB, MapCells = 128 },
                new ScanSession()).Run(default);
            Assert.Eq(Verdict.FakeCapacity, r.Verdict, Describe(r));
            Assert.True(r.EstimatedRealBytes is >= 30 * MB and <= 48 * MB, $"estimate {r.EstimatedRealBytes}");
            Assert.Eq(0, fs.FileCount, "files deleted");
            Assert.True(r.Notes.Any(n => n.Key == "note.existingFilesRisk"), "existing files warning");
        }

        public static void T_FreeSpace_Cancel_Cleans_Up()
        {
            var fs = new SimFileSpace("/sim", total: 64 * MB, used: 0, real: 64 * MB, wrap: false);
            var cts = new CancellationTokenSource();
            var s = new ScanSession();
            var t = new FreeSpaceFileTest(fs, UsbDrive(64 * MB), new FreeSpaceOptions { FileBytes = 8 * MB, ChunkBytes = 1 << 20, ReserveBytes = 0 }, s);
            cts.CancelAfter(0);
            var r = t.Run(cts.Token);
            Assert.True(r.Cancelled && r.Verdict == Verdict.Incomplete, Describe(r));
            Assert.Eq(0, fs.FileCount, "no files left");
        }

        // ───────────────────────── القراءة فقط ─────────────────────────

        public static void T_ReadOnly_Clean_Is_Inconclusive_And_Writes_Nothing()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(256 * MB, 256 * MB, FakeMode.Genuine, clock);
            var r = new ReadOnlyScan(dev, UsbDrive(dev.Size), new ReadOnlyOptions { ChunkBytes = 1 << 20, MapCells = 128 }, new ScanSession(clock)).Run(default);
            Assert.Eq(Verdict.Inconclusive, r.Verdict, Describe(r));
            Assert.Eq(0, dev.Writes, "no writes");
            Assert.True(r.Notes.Any(n => n.Key == "note.readOnlyLimit"), "limit note");
        }

        public static void T_ReadOnly_Errors()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(256 * MB, 256 * MB, FakeMode.Genuine, clock) { ReadErrorAt = off => off is >= 100 * MB and < 101 * MB };
            var r = new ReadOnlyScan(dev, UsbDrive(dev.Size), new ReadOnlyOptions { ChunkBytes = 1 << 20, MapCells = 128 }, new ScanSession(clock)).Run(default);
            Assert.Eq(Verdict.ReadWriteErrors, r.Verdict, Describe(r));
            Assert.True(r.ReadErrorCells >= 1, "purple");
        }

        // ───────────────────────── السرعة ─────────────────────────

        public static void T_Speed_Test_Real_File()
        {
            string dir = Temp("speed");
            var r = new SpeedTest(new RealFileSpace(dir), UsbDrive(64 * GB), new SpeedOptions { FileBytes = 32 * MB, SeqBlockBytes = 4 << 20, RandomOps = 300 }, new ScanSession()).Run(default);
            Assert.True(r.Speed!.SeqWrite.Measured && r.Speed.SeqRead.Measured, "sequential measured");
            Assert.True(r.Speed.RandRead4K.Measured && r.Speed.RandRead4K.Iops > 0, "random read measured");
            Assert.True(r.Speed.SeqWrite.MinMBps <= r.Speed.SeqWrite.AvgMBps && r.Speed.SeqWrite.AvgMBps <= r.Speed.SeqWrite.MaxMBps + 0.01, "min<=avg<=max");
            Assert.Eq(0, Directory.GetFiles(dir).Length, "temp file deleted");
        }

        // ───────────────────────── الحماية ─────────────────────────

        public static void T_Safety_Policy()
        {
            var usb = UsbDrive(64 * GB);
            Assert.True(SafetyPolicy.Check(usb, ScanKind.Quick).Allowed, "usb quick");
            Assert.True(SafetyPolicy.Check(usb, ScanKind.FullDestructive).Allowed, "usb destructive");

            var sys = UsbDrive(512 * GB); sys.BusType = "NVMe"; sys.IsSystemDisk = true; sys.Kind = MediaKind.InternalSsd;
            Assert.Eq("safety.systemDisk", SafetyPolicy.Check(sys, ScanKind.Quick).ReasonKey, "system disk raw");
            Assert.True(!SafetyPolicy.Check(sys, ScanKind.FullDestructive).Allowed, "system destructive");
            Assert.True(!SafetyPolicy.Check(sys, ScanKind.FullFreeSpace).Allowed, "system fill");
            Assert.True(SafetyPolicy.Check(sys, ScanKind.ReadOnly).Allowed, "system read-only allowed");
            Assert.True(SafetyPolicy.Check(sys, ScanKind.Speed).Allowed, "system speed allowed");

            var sata = UsbDrive(1 * TB); sata.BusType = "SATA";
            Assert.Eq("safety.internalRaw", SafetyPolicy.Check(sata, ScanKind.Deep).ReasonKey, "internal raw");

            var wp = UsbDrive(32 * GB); wp.WriteProtected = true;
            Assert.Eq("safety.writeProtected", SafetyPolicy.Check(wp, ScanKind.Quick).ReasonKey, "write protected");
            Assert.True(SafetyPolicy.Check(wp, ScanKind.ReadOnly).Allowed, "wp read-only");

            var rec = UsbDrive(32 * GB); rec.HasProtectedPartitions = true;
            Assert.Eq("safety.protectedPartitions", SafetyPolicy.Check(rec, ScanKind.Quick).ReasonKey, "recovery partitions");

            var noVol = UsbDrive(32 * GB); noVol.Volumes.Clear();
            Assert.Eq("safety.noVolume", SafetyPolicy.Check(noVol, ScanKind.FullFreeSpace).ReasonKey, "fill needs volume");
            Assert.True(SafetyPolicy.Check(noVol, ScanKind.Speed).Allowed && SafetyPolicy.NeedsRawAccess(ScanKind.Speed, noVol), "speed without volume = raw read");

            // هارد M.2 جديد مركب داخلياً بدون أقسام: الفحص السريع مسموح، المدمر لا
            var blank = UsbDrive(4 * TB); blank.BusType = "NVMe"; blank.Kind = MediaKind.InternalSsd; blank.Volumes.Clear(); blank.PartitionStyle = "RAW";
            Assert.True(SafetyPolicy.Check(blank, ScanKind.Quick).Allowed, "blank internal quick");
            Assert.True(!SafetyPolicy.Check(blank, ScanKind.FullDestructive).Allowed, "blank internal destructive blocked");
            var usedInternal = UsbDrive(4 * TB); usedInternal.BusType = "NVMe"; usedInternal.PartitionStyle = "GPT";
            Assert.Eq("safety.internalRaw", SafetyPolicy.Check(usedInternal, ScanKind.Quick).ReasonKey, "internal with data blocked");

            var other = UsbDrive(64 * GB); other.HardwareSerial = "DIFFERENT";
            Assert.True(!SafetyPolicy.SameIdentity(usb, other), "identity mismatch detected");
            Assert.True(SafetyPolicy.SameIdentity(usb, UsbDrive(64 * GB)), "identity same");
        }

        // ───────────────────────── السجل والتقارير والترجمة ─────────────────────────

        // ───────────────────────── تقرير الشراء ─────────────────────────

        static ScanResult SpeedResultOf(double read, double write, bool collapse = false)
        {
            var sp = new SpeedResult();
            sp.SeqRead = new SpeedStat { Measured = true, AvgMBps = read, MinMBps = read * 0.9, MaxMBps = read * 1.1 };
            var samples = Enumerable.Range(0, 20).Select(i => collapse && i >= 6 ? write * 0.05 : write).ToList();
            sp.SeqWrite = new SpeedStat { Measured = true, AvgMBps = samples.Average(), MinMBps = samples.Min(), MaxMBps = samples.Max(), Samples = samples };
            return new ScanResult { Kind = ScanKind.Speed, Verdict = Verdict.Genuine, Speed = sp };
        }

        public static void T_Purchase_Advice()
        {
            var clock = new SimClock();
            var ssd = UsbDrive(4_000_000_000_000L); ssd.Manufacturer = "Samsung"; ssd.Model = "SSD 990 PRO 4TB"; ssd.Kind = MediaKind.ExternalSsd; ssd.SeekPenalty = false;
            var newHealth = new HealthInfo { SmartSupported = true, NvmeLogAvailable = true, PowerOnHours = 2, DataWrittenBytes = 3_000_000_000, PercentUsed = 0, AvailableSpare = 100, CriticalWarning = 0, MediaErrors = 0, TemperatureC = 41 };
            var genuine = RunSampled(new SimulatedDevice(4 * TB, 4 * TB, FakeMode.Genuine, clock), clock: clock).r;
            var a = PurchaseAdvisor.Evaluate(ssd, genuine, SpeedResultOf(900, 800), newHealth, null, false, null);
            Assert.Eq(AdviceLevel.Buy, a.Level, string.Join(",", a.Checks.Select(c => c.TitleKey + ":" + c.Status)));

            var fake = RunSampled(new SimulatedDevice(4 * TB, 256 * GB, FakeMode.Wrap, clock), clock: clock).r;
            a = PurchaseAdvisor.Evaluate(ssd, fake, SpeedResultOf(900, 800), newHealth, null, false, null);
            Assert.Eq(AdviceLevel.DoNotBuy, a.Level, "fake => don't buy");
            Assert.Eq(CheckStatus.Fail, a.Checks[0].Status, "capacity fail first");

            var used = new HealthInfo { SmartSupported = true, NvmeLogAvailable = true, PowerOnHours = 9000, DataWrittenBytes = 80_000_000_000_000, PercentUsed = 35, AvailableSpare = 100, CriticalWarning = 0, MediaErrors = 0 };
            a = PurchaseAdvisor.Evaluate(ssd, genuine, SpeedResultOf(900, 800), used, null, false, null);
            Assert.Eq(AdviceLevel.Caution, a.Level, "used => caution");

            a = PurchaseAdvisor.Evaluate(ssd, genuine, SpeedResultOf(900, 800, collapse: true), newHealth, null, false, null);
            Assert.True(a.Checks.Any(c => c.TitleKey == "adv.sustained" && c.Status == CheckStatus.Warn), "collapse detected");

            var generic = UsbDrive(4_000_000_000_000L); generic.Manufacturer = null; generic.Model = "NVMe SSD 4TB";
            a = PurchaseAdvisor.Evaluate(generic, genuine, SpeedResultOf(900, 800), newHealth, null, false, null);
            Assert.True(a.Checks.Any(c => c.TitleKey == "adv.identity" && c.Status == CheckStatus.Warn), "generic model flagged");

            a = PurchaseAdvisor.Evaluate(ssd, null, null, null, null, false, null);
            Assert.Eq(AdviceLevel.NeedTests, a.Level, "nothing tested");

            // التوافق: 4TB بنمط MBR على حاسبة Legacy + منفذ أبطأ
            var internalSsd = UsbDrive(4_000_000_000_000L); internalSsd.BusType = "NVMe"; internalSsd.PartitionStyle = "MBR"; internalSsd.Model = "WD_BLACK SN850X 4000GB";
            var sys = new SystemSnapshot { FirmwareType = "Legacy", LinkGenCurrent = 3, LinkGenMax = 4, LinkWidthCurrent = 4, LinkWidthMax = 4 };
            a = PurchaseAdvisor.Evaluate(internalSsd, genuine, SpeedResultOf(3000, 2500), newHealth, null, true, sys);
            Assert.True(a.CompatibilityChecked, "compat on");
            Assert.True(a.Compatibility.Any(c => c.TitleKey == "adv.compatBoot" && c.Status == CheckStatus.Warn), "legacy + >2TB");
            Assert.True(a.Compatibility.Any(c => c.TitleKey == "adv.compatPartition" && c.Status == CheckStatus.Warn), "mbr > 2TB");
            Assert.True(a.Compatibility.Any(c => c.TitleKey == "adv.compatLink" && c.Status == CheckStatus.Warn), "slower link");
            a = PurchaseAdvisor.Evaluate(internalSsd, genuine, SpeedResultOf(3000, 2500), newHealth, null, false, sys);
            Assert.True(!a.CompatibilityChecked && a.Compatibility.Count == 0, "compat off");

            foreach (var c in a.Checks.Concat(PurchaseAdvisor.Evaluate(internalSsd, fake, null, used, null, true, sys).Compatibility))
            {
                Assert.True(Strings.Ar.ContainsKey(c.TitleKey) && Strings.En.ContainsKey(c.TitleKey), "title " + c.TitleKey);
                if (c.Detail != null) Assert.True(Strings.Ar.ContainsKey(c.Detail.Key) && Strings.En.ContainsKey(c.Detail.Key), "detail " + c.Detail.Key);
            }
        }

        public static void T_Raw_Read_Speed_Writes_Nothing()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(4 * GB, 4 * GB, FakeMode.Genuine, clock);
            var r = SpeedTest.RunRawRead(dev, UsbDrive(dev.Size), new SpeedOptions { FileBytes = 64 * MB, RandomOps = 200 }, new ScanSession(clock), default);
            Assert.True(r.Speed!.RawReadOnly && r.Speed.SeqRead.Measured && r.Speed.RandRead4K.Measured, "measured");
            Assert.True(!r.Speed.SeqWrite.Measured, "no write measured");
            Assert.Eq(0, dev.Writes, "nothing written");
        }

        public static void T_History_And_Reports()
        {
            var clock = new SimClock();
            var dev = new SimulatedDevice(2 * TB, 64 * GB, FakeMode.Wrap, clock);
            var (r, _, _) = RunSampled(dev, clock: clock);
            var store = new HistoryStore(Temp("history"));
            store.Save(r);
            var all = store.LoadAll();
            Assert.Eq(1, all.Count, "saved");
            Assert.Eq(r.Id, all[0].Id, "id");
            Assert.Eq(Verdict.FakeCapacity, all[0].Verdict, "verdict persisted");
            Assert.Eq(r.Map.Length, all[0].Map.Length, "map persisted");

            foreach (var lang in new[] { "ar", "en" })
            {
                var html = ReportBuilder.ToHtml(all[0], lang);
                Assert.True(html.Contains(Strings.Verdict(lang, Verdict.FakeCapacity)), "html verdict " + lang);
                Assert.True(html.Contains("ibraprogram.netlify.app"), "credit");
                var csv = ReportBuilder.ToCsv(all[0], lang);
                Assert.True(csv.Split('\n').Length > r.Map.Length, "csv rows " + lang);
            }
            Assert.True(ReportBuilder.ToJson(all[0]).Contains("FakeCapacity"), "json");
            Assert.True(store.Delete(r.Id), "delete");
            Assert.Eq(0, store.LoadAll().Count, "deleted");
        }

        public static void T_Localization_Complete()
        {
            var missing = Strings.Ar.Keys.Except(Strings.En.Keys).Concat(Strings.En.Keys.Except(Strings.Ar.Keys)).ToList();
            Assert.True(missing.Count == 0, "missing keys: " + string.Join(", ", missing));
            foreach (Verdict v in Enum.GetValues<Verdict>())
            {
                Assert.True(Strings.Ar.ContainsKey("verdict." + v) && Strings.Ar.ContainsKey("verdictHint." + v), "verdict " + v);
            }
            foreach (ScanKind k in Enum.GetValues<ScanKind>()) Assert.True(Strings.Ar.ContainsKey("kind." + k), "kind " + k);
            foreach (CellState c in Enum.GetValues<CellState>()) Assert.True(Strings.Ar.ContainsKey("state." + c), "state " + c);
            foreach (MediaKind m in Enum.GetValues<MediaKind>()) Assert.True(Strings.Ar.ContainsKey("media." + m), "media " + m);
            foreach (ScanPhase p in Enum.GetValues<ScanPhase>()) Assert.True(Strings.Ar.ContainsKey("phase." + p), "phase " + p);
            Assert.Eq("64 GB", Fmt.Bytes(64_000_000_000), "bytes format");
        }

        public static void T_All_Note_Keys_Translated()
        {
            // نجمع كل الملاحظات الممكنة من السيناريوهات السابقة ونتأكد أن لها ترجمة
            var clock = new SimClock();
            var results = new List<ScanResult>
            {
                RunSampled(new SimulatedDevice(2 * TB, 64 * GB, FakeMode.Wrap, clock), clock: clock).r,
                RunSampled(new SimulatedDevice(256 * GB, 30 * GB, FakeMode.DropZeros, clock), clock: clock).r,
                RunSampled(new SimulatedDevice(16 * GB, 16 * GB, FakeMode.Genuine, clock), clock: clock).r,
                RunFull(new SimulatedDevice(64 * MB, 16 * MB, FakeMode.Wrap, clock), clock),
            };
            foreach (var r in results)
            {
                foreach (var n in r.Notes)
                    Assert.True(Strings.Ar.ContainsKey(n.Key) && Strings.En.ContainsKey(n.Key), "note " + n.Key);
                foreach (var c in r.Map.Where(c => c.NoteKey != null))
                    Assert.True(Strings.Ar.ContainsKey(c.NoteKey!), "cell note " + c.NoteKey);
            }
        }
    }
}
