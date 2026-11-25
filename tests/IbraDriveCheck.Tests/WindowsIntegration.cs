using System.Security.Cryptography;
using IbraDriveCheck.Core.Devices.Windows;
using IbraDriveCheck.Core.Engine;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Safety;

namespace IbraDriveCheck.Tests;

/// <summary>
/// اختبارات على ويندوز حقيقي (تعمل في GitHub Actions على قرص افتراضي VHD بحرف T:).
/// تستخدم نفس واجهات ويندوز التي يستخدمها البرنامج: IOCTL، WMI، قفل الأقسام، القراءة/الكتابة المباشرة.
/// </summary>
public static partial class Tests
{
    static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("IBRA_WIN_INTEGRATION") != "1")
            throw new SkipException("Windows integration disabled");
    }

    static DriveSnapshot VhdDrive()
    {
        string letter = Environment.GetEnvironmentVariable("IBRA_TEST_VOLUME") ?? "T:";
        var d = DriveEnumerator.Enumerate().FirstOrDefault(x => x.Volumes.Any(v => v.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase)));
        Assert.True(d != null, "test VHD not found on " + letter);
        return d!;
    }

    public static void T_Win_Enumerate_And_Info()
    {
        RequireWindows();
        var drives = DriveEnumerator.Enumerate();
        Assert.True(drives.Count >= 2, "at least system disk + VHD, got " + drives.Count);
        Assert.True(drives.Any(d => d.IsSystemDisk), "system disk detected");
        foreach (var d in drives)
        {
            Console.WriteLine($"        disk{d.DiskNumber} {d.DisplayName} bus={d.BusType} kind={d.Kind} size={d.DeclaredBytes} sys={d.IsSystemDisk} wp={d.WriteProtected} style={d.PartitionStyle} vols={d.Letters}");
            Assert.True(d.DeclaredBytes > 0, "size");
        }
        Assert.True(DriveEnumerator.IsElevated, "CI runs elevated");
    }

    public static void T_Win_Health_And_System()
    {
        RequireWindows();
        var sys = DriveEnumerator.Enumerate().First(d => d.IsSystemDisk);
        var h = HealthReader.Read(sys);
        Console.WriteLine($"        health status={h.HealthStatus} smart={h.SmartSupported} nvme={h.NvmeLogAvailable} temp={h.TemperatureC} hours={h.PowerOnHours}");
        var s = SystemInfoReader.Read(sys);
        Console.WriteLine($"        os={s.Os} cpu={s.Cpu} fw={s.FirmwareType} board={s.Board}");
        Assert.True(s.Os != null, "OS name via WMI");
    }

    public static void T_Win_Safety_Blocks_System_Disk_Writes()
    {
        RequireWindows();
        var sys = DriveEnumerator.Enumerate().First(d => d.IsSystemDisk);
        try
        {
            ScanRunner.Run(new ScanRequest { Drive = sys, Kind = ScanKind.Quick }, new ScanSession(), default);
            throw new Exception("system disk write was not blocked");
        }
        catch (SafetyException ex)
        {
            Assert.Eq("safety.systemDisk", ex.ReasonKey, "reason");
        }
    }

    public static void T_Win_Raw_Quick_Scan_On_VHD_Preserves_Files()
    {
        RequireWindows();
        var d = VhdDrive();
        string letter = d.Volumes[0].Letter;
        // ملف موجود مسبقاً يجب أن يبقى سليماً بعد الفحص
        string file = Path.Combine(letter + "\\", "keep_me.bin");
        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        File.WriteAllBytes(file, data);
        var before = SHA256.HashData(data);

        ScanResult r;
        string jdir = Path.Combine(Path.GetTempPath(), "ibra-journal-" + Guid.NewGuid().ToString("N")[..6]);
        using (var locker = VolumeLocker.LockDisk(d.DiskNumber))
        using (var dev = RawDiskDevice.Open(d.DiskNumber, write: true))
        {
            Assert.Eq(d.DeclaredBytes, dev.Size, "size via IOCTL");
            r = new SampledCapacityTest(dev, d, ScanKind.Quick, SampledOptions.Quick(), new FileJournal(jdir, d.DisplayName), new ScanSession()).Run(default);
        }
        Console.WriteLine("        " + Describe(r) + $" samples={r.SampleCount} write={r.AvgWriteMBps} read={r.AvgReadMBps}");
        Assert.Eq(Verdict.Genuine, r.Verdict, Describe(r));
        Assert.True(r.RestoreOk == true, "restore verified");
        Assert.Eq(0, FileJournal.FindPending(jdir).Count, "journal removed after restore");

        Thread.Sleep(1500); // ويندوز يعيد تركيب القسم بعد فك القفل
        var after = SHA256.HashData(File.ReadAllBytes(file));
        Assert.True(before.AsSpan().SequenceEqual(after), "existing file intact after the raw scan");
        File.Delete(file);
    }

    public static void T_Win_ReadOnly_Sampled_On_VHD()
    {
        RequireWindows();
        var d = VhdDrive();
        using var dev = RawDiskDevice.Open(d.DiskNumber, write: false);
        var r = new ReadOnlyScan(dev, d, new ReadOnlyOptions { FullSurface = false, SampleCount = 256 }, new ScanSession()).Run(default);
        Assert.Eq(Verdict.Inconclusive, r.Verdict, Describe(r));
        Assert.Eq(0, r.ReadErrorCells, "no read errors");
    }

    public static void T_Win_FreeSpace_And_Speed_On_VHD()
    {
        RequireWindows();
        var d = VhdDrive();
        var v = d.Volumes[0];
        var fs = new RealFileSpace(v.Letter + "\\", v.FileSystem);
        var r = new FreeSpaceFileTest(fs, d, new FreeSpaceOptions { MaxTestBytes = 256L << 20, FileBytes = 128L << 20 }, new ScanSession()).Run(default);
        Console.WriteLine("        free-space: " + Describe(r));
        Assert.Eq(Verdict.Genuine, r.Verdict, Describe(r));
        Assert.Eq(0, FreeSpaceFileTest.FindLeftovers(v.Letter + "\\").Length, "test files cleaned");

        var s = new SpeedTest(fs, d, new SpeedOptions { FileBytes = 128L << 20, RandomOps = 500 }, new ScanSession()).Run(default);
        Console.WriteLine($"        speed: seqR={s.Speed!.SeqRead.AvgMBps} seqW={s.Speed.SeqWrite.AvgMBps} 4kR={s.Speed.RandRead4K.Iops}iops 4kW={s.Speed.RandWrite4K.Iops}iops");
        Assert.True(s.Speed.SeqRead.Measured && s.Speed.SeqWrite.Measured, "speed measured");

        using var dev = RawDiskDevice.Open(d.DiskNumber, write: false);
        var raw = SpeedTest.RunRawRead(dev, d, new SpeedOptions { FileBytes = 128L << 20, RandomOps = 300 }, new ScanSession(), default);
        Assert.True(raw.Speed!.SeqRead.Measured, "raw read speed");
    }

    public static void T_Win_Virtual_Disk_Blocked_By_Policy()
    {
        RequireWindows();
        var d = VhdDrive();
        // القرص الافتراضي ليس USB → الكتابة المباشرة عبر البرنامج ممنوعة (القواعد تحمي الأقراص غير الخارجية)
        var dec = SafetyPolicy.Check(d, ScanKind.Quick);
        Console.WriteLine($"        VHD bus={d.BusType} quick allowed={dec.Allowed} ({dec.ReasonKey})");
        Assert.True(!dec.Allowed || d.IsExternalBus, "policy");
    }
}
