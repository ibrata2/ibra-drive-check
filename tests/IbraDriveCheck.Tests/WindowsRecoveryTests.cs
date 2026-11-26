using System.Diagnostics;
using IbraDriveCheck.Core.Devices.Windows;
using IbraDriveCheck.Core.Engine;
using IbraDriveCheck.Core.Recovery;

namespace IbraDriveCheck.Tests;

/// <summary>
/// اختبارات الاستعادة وإزالة الآثار وإعادة التقسيم على ويندوز حقيقي (قرص VHD بحرف T: في البناء الآلي):
/// الحذف يتم بواجهات ويندوز العادية (File.Delete / Directory.Delete) على NTFS و exFAT و FAT32.
/// </summary>
public static partial class Tests
{
    static string TestLetter => Environment.GetEnvironmentVariable("IBRA_TEST_VOLUME") ?? "T:";

    static void FormatT(string fs)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"Format-Volume -DriveLetter {TestLetter[0]} -FileSystem {fs} -NewFileSystemLabel IBRATEST -Force -Confirm:$false | Out-Null\"")
        { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception("Format-Volume failed: " + err);
        Thread.Sleep(1500);
    }

    static void FlushVolume()
    {
        using var v = RawDiskDevice.OpenVolume(TestLetter, write: true);
        v.Flush();
    }

    static Dictionary<string, byte[]> WriteTestFiles(int seed)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["photo one.jpg"] = SampleFiles.Jpeg(400_000, seed),
            ["tiny note.txt"] = "small file — stays inside the MFT record on NTFS"u8.ToArray(),
            ["تقرير.pdf"] = SampleFiles.Pdf(120_000, seed + 1),
            ["Folder A/Sub B/clip.mp4"] = SampleFiles.Mp4(2_500_000, seed + 2),
            ["Folder A/doc.docx"] = SampleFiles.Docx(80_000, seed + 3),
        };
        foreach (var (rel, data) in files)
        {
            string path = Path.Combine(TestLetter + "\\", rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough);
            fs.Write(data);
            fs.Flush(true);
        }
        File.WriteAllText(Path.Combine(TestLetter + "\\", "keep.txt"), "keep");
        FlushVolume();
        return files;
    }

    static void DeleteTestFiles()
    {
        File.Delete(Path.Combine(TestLetter + "\\", "photo one.jpg"));
        File.Delete(Path.Combine(TestLetter + "\\", "tiny note.txt"));
        File.Delete(Path.Combine(TestLetter + "\\", "تقرير.pdf"));
        Directory.Delete(Path.Combine(TestLetter + "\\", "Folder A"), recursive: true);
        FlushVolume();
        Thread.Sleep(1000);
    }

    static void WinRecoverRoundTrip(string fs)
    {
        RequireWindows();
        FormatT(fs);
        var files = WriteTestFiles(fs.Length * 7);
        DeleteTestFiles();

        RecoveryScan scan;
        string outDir = Temp("winrec_" + fs);
        using (var src = VolumeTools.OpenVolumeSource(TestLetter))
        {
            var p = new RecoveryProgress();
            scan = RecoveryEngine.Scan(src, deep: false, p, default);
            Console.WriteLine($"        {fs}: {scan.Items.Count} items — " + string.Join(", ", scan.Items.Select(i => $"{i.FullPath}[{i.Chance},{i.Size}]")));
            var res = RecoveryEngine.Recover(src, scan.Items, outDir, p, default);
            Assert.Eq(0, res.Failed, "recover errors " + string.Join(";", res.Errors));
        }
        Assert.True(!scan.Items.Any(i => i.Name == "keep.txt"), "live file not listed");
        foreach (var (rel, data) in files)
        {
            string path = Path.Combine(new[] { outDir }.Concat(rel.Split('/')).ToArray());
            if (!File.Exists(path))
            {
                string alt = Path.Combine(Path.GetDirectoryName(path)!, "_" + Path.GetFileName(path)[1..]);
                if (File.Exists(alt)) path = alt;
            }
            Assert.True(File.Exists(path), $"{fs}: recovered {rel}");
            Assert.Eq(SampleFiles.Hash(data), SampleFiles.Hash(path), $"{fs}: content {rel}");
        }
    }

    public static void T_Win_Recover_NTFS() => WinRecoverRoundTrip("NTFS");
    public static void T_Win_Recover_exFAT() => WinRecoverRoundTrip("exFAT");
    public static void T_Win_Recover_FAT32() => WinRecoverRoundTrip("FAT32");

    static void WinWipeTraces(string fs)
    {
        RequireWindows();
        FormatT(fs);
        var files = WriteTestFiles(99);
        DeleteTestFiles();

        RecoveryScan before;
        using (var src = VolumeTools.OpenVolumeSource(TestLetter)) before = RecoveryEngine.Scan(src, false, new RecoveryProgress(), default);
        Assert.True(before.Items.Any(i => i.Name == "photo one.jpg"), "recoverable before wipe");

        var p = new RecoveryProgress();
        if (fs == "NTFS")
        {
            int made = TraceCleaner.FillMftRecords(TestLetter + "\\", before.UnusedMftRecords, p, default);
            Console.WriteLine($"        MFT fill: {made} records; USN deleted: {VolumeTools.DeleteUsnJournal(TestLetter)}");
        }
        else
        {
            int n = VolumeTools.ScrubDeletedNames(TestLetter, fs);
            Console.WriteLine($"        scrubbed entries: {n}");
            Thread.Sleep(1500);
        }
        long wiped = TraceCleaner.WipeFreeSpace(new RealFileSpace(TestLetter + "\\"), p, default);
        Console.WriteLine($"        wiped {wiped / 1048576} MB");
        FlushVolume();
        Assert.True(File.ReadAllText(Path.Combine(TestLetter + "\\", "keep.txt")) == "keep", "live file intact");

        using (var src = VolumeTools.OpenVolumeSource(TestLetter))
        {
            var after = RecoveryEngine.Scan(src, deep: true, new RecoveryProgress(), default);
            var names = files.Keys.Select(k => k.Split('/')[^1]).ToHashSet();
            var leftNames = after.Items.Where(i => names.Contains(i.Name)).Select(i => i.FullPath).ToList();
            Assert.True(leftNames.Count == 0, $"{fs}: names still visible: {string.Join(", ", leftNames)}");
            // ولا محتوى قابل للاستعادة حتى بالبحث العميق
            string outDir = Temp("afterwipe");
            RecoveryEngine.Recover(src, after.Items, outDir, new RecoveryProgress(), default);
            var hashes = files.Values.Select(SampleFiles.Hash).ToHashSet();
            var leaked = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Where(f => hashes.Contains(SampleFiles.Hash(f))).ToList();
            Assert.True(leaked.Count == 0, $"{fs}: content still recoverable: {string.Join(", ", leaked)}");
            Console.WriteLine($"        after wipe: {after.Items.Count} items (none matching)");
        }
    }

    public static void T_Win_Wipe_Traces_NTFS() => WinWipeTraces("NTFS");
    public static void T_Win_Wipe_Traces_exFAT() => WinWipeTraces("exFAT");

    public static void T_Win_Repartition_To_Real_Size()
    {
        RequireWindows();
        var d = VhdDrive();
        var r = VolumeTools.Repartition(d, 1000L * 1024 * 1024, "exFAT", "IBRAFIX", bypassSafetyForTests: true, letter: TestLetter[0]);
        Console.WriteLine($"        repartition: ok={r.Ok} letter={r.Letter} size={r.PartitionBytes} fs={r.FileSystem}");
        Assert.True(r.Ok, "diskpart ok: " + r.Log);
        Assert.Eq("exFAT", r.FileSystem, "fs");
        Assert.True(r.PartitionBytes > 950L * 1024 * 1024 && r.PartitionBytes <= 1000L * 1024 * 1024, "size " + r.PartitionBytes);

        // إرجاع الحجم الكامل
        var d2 = VhdDrive();
        var full = VolumeTools.Repartition(d2, null, "exFAT", "IBRATEST", bypassSafetyForTests: true, letter: TestLetter[0]);
        Assert.True(full.Ok && full.PartitionBytes > 1900L * 1024 * 1024, "full size again " + full.PartitionBytes);
        Assert.True(!VolumeTools.CanRepartition(VhdDrive()).Allowed, "virtual disk blocked without bypass");
    }
}
