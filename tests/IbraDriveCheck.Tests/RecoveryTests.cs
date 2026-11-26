using IbraDriveCheck.Core.Model;
using System.Diagnostics;
using IbraDriveCheck.Core.Recovery;

namespace IbraDriveCheck.Tests;

/// <summary>
/// اختبارات الاستعادة على صور أقراص حقيقية تُصنع بأدوات لينكس (mtools / mkntfs).
/// على ويندوز تُتخطى، وهناك اختبارات مكافئة على قرص افتراضي حقيقي.
/// </summary>
public static partial class Tests
{
    static string Tool(string name)
    {
        foreach (var dir in new[] { "/usr/bin", "/usr/sbin", "/bin", "/sbin" })
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        throw new SkipException(name + " not installed");
    }

    static void Run(string tool, string args, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(Tool(tool), args) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception($"{tool} {args} failed: {err}");
    }

    static Dictionary<string, string> MtoolsEnv => new() { ["MTOOLS_SKIP_CHECK"] = "1", ["LANG"] = "C.UTF-8", ["LC_ALL"] = "C.UTF-8" };

    static void MCopy(string img, string local, string dest) => Run("mcopy", $"-i \"{img}\" -o \"{local}\" \"::{dest}\"", MtoolsEnv);

    static (RecoveryScan scan, string outDir) ScanAndRecover(string img, bool deep, Func<RecoveryItem, bool>? pick = null)
    {
        using var src = new FileVolumeSource(img);
        var p = new RecoveryProgress();
        var scan = RecoveryEngine.Scan(src, deep, p, default);
        string outDir = Temp("recovered");
        var items = scan.Items.Where(i => !i.IsDirectory && (pick?.Invoke(i) ?? true)).ToList();
        var res = RecoveryEngine.Recover(src, items, outDir, p, default);
        Assert.Eq(0, res.Failed, "recover failures: " + string.Join("; ", res.Errors));
        return (scan, outDir);
    }

    static Dictionary<string, byte[]> TestSet(int seed) => new()
    {
        ["photo one.jpg"] = SampleFiles.Jpeg(90_000, seed),
        ["تقرير الفحص.pdf"] = SampleFiles.Pdf(40_000, seed + 1),
        ["small.txt"] = "hello IBRA"u8.ToArray(),
        ["Docs/Sub Folder/very long file name for lfn test.docx"] = SampleFiles.Docx(30_000, seed + 2),
        ["Docs/notes.png"] = SampleFiles.Png(20_000, seed + 3),
        ["Video/clip.mp4"] = SampleFiles.Mp4(150_000, seed + 4),
    };

    static void FatDeleteRecoverRoundTrip(string fatType)
    {
        string dir = Temp("fat" + fatType);
        string img = Path.Combine(dir, "disk.img");
        Run("mkfs.fat", $"-F {fatType} -C -n IBRATEST \"{img}\" {(fatType == "32" ? 65536 : 32768)}");
        var files = TestSet(fatType == "32" ? 10 : 20);
        Run("mmd", $"-i \"{img}\" ::Docs \"::Docs/Sub Folder\" ::Video", MtoolsEnv);
        foreach (var (rel, data) in files)
        {
            string local = Path.Combine(dir, Path.GetFileName(rel));
            File.WriteAllBytes(local, data);
            MCopy(img, local, rel);
        }
        // ملف يبقى موجوداً (يجب ألا يظهر كمحذوف)
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "keep");
        MCopy(img, Path.Combine(dir, "keep.txt"), "keep.txt");

        Run("mdel", $"-i \"{img}\" \"::photo one.jpg\" \"::تقرير الفحص.pdf\" ::small.txt", MtoolsEnv);
        Run("mdeltree", $"-i \"{img}\" ::Docs ::Video", MtoolsEnv);

        var (scan, outDir) = ScanAndRecover(img, deep: false);
        Assert.Eq("FAT" + fatType, scan.FileSystem, "fs");
        Assert.True(!scan.Items.Any(i => i.Name == "keep.txt"), "existing file not listed as deleted");
        foreach (var (rel, data) in files)
        {
            string path = Path.Combine(new[] { outDir }.Concat(rel.Split('/')).ToArray());
            // الأسماء القصيرة 8.3 بدون اسم طويل يضيع حرفها الأول عند الحذف (سلوك FAT الطبيعي)
            if (!File.Exists(path))
            {
                string alt = Path.Combine(Path.GetDirectoryName(path)!, "_" + Path.GetFileName(path)[1..]);
                if (File.Exists(alt)) path = alt;
            }
            Assert.True(File.Exists(path), $"recovered {rel} (found: {string.Join(", ", scan.Items.Select(i => i.FullPath))})");
            Assert.Eq(SampleFiles.Hash(data), SampleFiles.Hash(path), "content " + rel);
        }
        Assert.True(scan.Items.Where(i => !i.IsDirectory).All(i => i.Chance == RecoveryChance.Good), "chance good");
        Assert.True(scan.Items.Any(i => i.IsDirectory && i.FullPath == "Docs/Sub Folder"), "deleted folder tree");
    }

    public static void T_Recovery_FAT32_Deleted_Files_And_Folders() => FatDeleteRecoverRoundTrip("32");
    public static void T_Recovery_FAT16_Deleted_Files_And_Folders() => FatDeleteRecoverRoundTrip("16");

    public static void T_Recovery_Carving_After_Quick_Format()
    {
        string dir = Temp("carve");
        string img = Path.Combine(dir, "disk.img");
        Run("mkfs.fat", $"-F 32 -C \"{img}\" 65536");
        var files = new Dictionary<string, byte[]>
        {
            ["a.jpg"] = SampleFiles.Jpeg(120_000, 1),
            ["b.png"] = SampleFiles.Png(50_000, 2),
            ["c.pdf"] = SampleFiles.Pdf(70_000, 3),
            ["d.docx"] = SampleFiles.Docx(40_000, 4),
            ["e.mp4"] = SampleFiles.Mp4(200_000, 5),
            ["f.wav"] = SampleFiles.Wav(60_000, 6),
            ["g.gif"] = SampleFiles.Gif(7),
        };
        foreach (var (name, data) in files)
        {
            File.WriteAllBytes(Path.Combine(dir, name), data);
            MCopy(img, Path.Combine(dir, name), name);
        }
        // تهيئة سريعة: يمسح جدول الملفات ويبقي البيانات
        Run("mkfs.fat", $"-F 32 \"{img}\"");

        var (scan, outDir) = ScanAndRecover(img, deep: true);
        var carved = Directory.GetFiles(Path.Combine(outDir, "$Carved"), "*", SearchOption.AllDirectories).Select(SampleFiles.Hash).ToHashSet();
        foreach (var (name, data) in files)
            Assert.True(carved.Contains(SampleFiles.Hash(data)), $"carved {name} ({string.Join(", ", scan.Items.Select(i => i.FullPath + ":" + i.Size))})");
        Assert.True(scan.Items.Any(i => i.Name.EndsWith(".docx")), "docx detected from zip");
    }

    public static void T_Recovery_NTFS_Deleted_Records()
    {
        string dir = Temp("ntfs");
        string img = Path.Combine(dir, "ntfs.img");
        using (var f = File.Create(img)) f.SetLength(64L << 20);
        Run("mkntfs", $"-F -Q -q -s 512 -c 4096 \"{img}\"");
        var files = new Dictionary<string, byte[]>
        {
            ["big photo.jpg"] = SampleFiles.Jpeg(300_000, 11),
            ["tiny.txt"] = "resident data inside MFT record"u8.ToArray(),
            ["ملف عربي.pdf"] = SampleFiles.Pdf(90_000, 12),
        };
        foreach (var (name, data) in files)
        {
            File.WriteAllBytes(Path.Combine(dir, "src_" + files.Keys.ToList().IndexOf(name)), data);
            Run("ntfscp", $"-f -q \"{img}\" \"{Path.Combine(dir, "src_" + files.Keys.ToList().IndexOf(name))}\" \"{name}\"");
        }

        // نحذف بطريقة NTFS: نطفئ علامة "مستخدم" في سجل MFT ونحرر كتله في $Bitmap
        using (var src = new FileVolumeSource(img))
        {
            var s = new NtfsScanner(src);
            s.Scan(default);
        }
        MarkNtfsDeleted(img, files.Keys.ToHashSet());

        var (scan, outDir) = ScanAndRecover(img, deep: false);
        Assert.Eq("NTFS", scan.FileSystem, "fs");
        foreach (var (name, data) in files)
        {
            string path = Path.Combine(outDir, name);
            Assert.True(File.Exists(path), $"recovered {name} (found {string.Join(", ", scan.Items.Select(i => i.FullPath))})");
            Assert.Eq(SampleFiles.Hash(data), SampleFiles.Hash(path), "content " + name);
        }
        Assert.True(scan.Items.First(i => i.Name == "tiny.txt").Resident != null, "resident file");
        Assert.True(scan.UnusedMftRecords > 0, "unused MFT records counted");
    }

    /// <summary>يطفئ علامة in-use لسجلات بالاسم (يحاكي الحذف) — لاختبار المحلل فقط.</summary>
    static void MarkNtfsDeleted(string img, HashSet<string> names)
    {
        var boot = new byte[512];
        using var fs = new FileStream(img, FileMode.Open, FileAccess.ReadWrite);
        fs.ReadExactly(boot);
        int bps = BitConverter.ToUInt16(boot, 11), spc = boot[13], cs = bps * spc;
        long mft = BitConverter.ToInt64(boot, 0x30) * cs;
        int cpr = (sbyte)boot[0x40], rs = cpr > 0 ? cpr * cs : 1 << -cpr;
        var rec = new byte[rs];
        int marked = 0;
        for (int i = 16; i < 400; i++)
        {
            fs.Position = mft + (long)i * rs;
            fs.ReadExactly(rec);
            if (rec[0] != 'F') continue;
            var copy = (byte[])rec.Clone();
            NtfsScanner.ApplyFixup(copy);
            int pos = BitConverter.ToUInt16(copy, 0x14);
            string? name = null;
            while (pos + 16 < rs && BitConverter.ToUInt32(copy, pos) != 0xFFFFFFFF)
            {
                uint type = BitConverter.ToUInt32(copy, pos);
                int len = BitConverter.ToInt32(copy, pos + 4);
                if (len <= 0) break;
                if (type == 0x30)
                {
                    int vo = BitConverter.ToUInt16(copy, pos + 0x14);
                    int nl = copy[pos + vo + 0x40];
                    name = System.Text.Encoding.Unicode.GetString(copy, pos + vo + 0x42, nl * 2);
                }
                pos += len;
            }
            if (name == null || !names.Contains(name)) continue;
            ushort flags = BitConverter.ToUInt16(rec, 0x16);
            BitConverter.GetBytes((ushort)(flags & ~1)).CopyTo(rec, 0x16);
            ushort seq = BitConverter.ToUInt16(rec, 0x10);
            BitConverter.GetBytes((ushort)(seq + 1)).CopyTo(rec, 0x10);
            fs.Position = mft + (long)i * rs;
            fs.Write(rec);
            marked++;
        }
        Assert.Eq(names.Count, marked, "records marked deleted");
    }
}

public static partial class Tests
{
    public static void T_Trace_Scrub_FAT_Deleted_Names()
    {
        string dir = Temp("scrub");
        string img = Path.Combine(dir, "disk.img");
        Run("mkfs.fat", $"-F 32 -C \"{img}\" 65536");
        Run("mmd", $"-i \"{img}\" ::Keep", MtoolsEnv);
        File.WriteAllBytes(Path.Combine(dir, "secret.jpg"), SampleFiles.Jpeg(20000, 3));
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "keep me");
        MCopy(img, Path.Combine(dir, "secret.jpg"), "Keep/my secret photo.jpg");
        MCopy(img, Path.Combine(dir, "secret.jpg"), "secret root file.jpg");
        MCopy(img, Path.Combine(dir, "keep.txt"), "Keep/keep.txt");
        Run("mdel", $"-i \"{img}\" \"::Keep/my secret photo.jpg\" \"::secret root file.jpg\"", MtoolsEnv);

        int scrubbed;
        using (var src = new FileVolumeSource(img))
        using (var w = new FileStream(img, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            Assert.True(RecoveryEngine.Scan(src, false, new RecoveryProgress(), default).Items.Any(i => i.Name.Contains("secret")), "found before scrub");
            scrubbed = TraceCleaner.ScrubDirectoryEntries(src, (off, data) => { w.Position = off; w.Write(data); w.Flush(); }, "FAT32");
        }
        Assert.True(scrubbed >= 2, "entries scrubbed " + scrubbed);
        using (var src = new FileVolumeSource(img))
        {
            var after = RecoveryEngine.Scan(src, false, new RecoveryProgress(), default);
            Assert.True(!after.Items.Any(i => i.Name.Contains("secret")), "names gone: " + string.Join(",", after.Items.Select(i => i.Name)));
        }
        // الملفات الموجودة سليمة: mtools يقرأ الملف بعد التعديل
        string back = Path.Combine(dir, "back.txt");
        Run("mcopy", $"-i \"{img}\" -o ::Keep/keep.txt \"{back}\"", MtoolsEnv);
        Assert.Eq("keep me", File.ReadAllText(back), "live file intact");
        Run("fsck.fat", $"-n \"{img}\"");
    }

    public static void T_Trace_Shred_File_And_Folder()
    {
        string dir = Temp("shred");
        string f = Path.Combine(dir, "a.txt");
        File.WriteAllText(f, "secret");
        TraceCleaner.ShredFile(f);
        Assert.True(!File.Exists(f) && Directory.GetFiles(dir).Length == 0, "file gone");
        string sub = Path.Combine(dir, "folder", "deep");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "x.bin"), "x");
        File.WriteAllText(Path.Combine(dir, "folder", "y.bin"), "y");
        var (ok, err) = TraceCleaner.ShredDirectory(Path.Combine(dir, "folder"), 1, null, default);
        Assert.Eq(2, ok, "files shredded");
        Assert.Eq(0, err, "errors");
        Assert.True(!Directory.Exists(Path.Combine(dir, "folder")) && Directory.GetDirectories(dir).Length == 0, "folder gone");
        Assert.True(!TraceCleaner.IsShredAllowed(Path.GetPathRoot(dir)!), "root blocked");
    }

    public static void T_Capacity_Fix_Plan()
    {
        var d = UsbDrive(2 * TB);
        var clock = new SimClock();
        var fake = RunSampled(new SimulatedDevice(2 * TB, 64 * GB, FakeMode.Wrap, clock), clock: clock).r;
        fake.Drive = d;
        var plan = IbraDriveCheck.Core.Engine.CapacityFixPlanner.Plan(d, new[] { fake });
        Assert.Eq(IbraDriveCheck.Core.Engine.CapacitySource.WrapExact, plan.Source, "source");
        Assert.True(plan.RecommendedBytes < 64 * GB && plan.RecommendedBytes > 62 * GB, "size " + plan.RecommendedBytes);
        Assert.Eq(0L, plan.RecommendedBytes % MB, "MiB aligned");

        var genuine = RunSampled(new SimulatedDevice(32 * GB, 32 * GB, FakeMode.Genuine, clock), clock: clock).r;
        var g = UsbDrive(32 * GB); genuine.Drive = g;
        plan = IbraDriveCheck.Core.Engine.CapacityFixPlanner.Plan(g, new[] { genuine });
        Assert.Eq(IbraDriveCheck.Core.Engine.CapacitySource.Genuine, plan.Source, "genuine");
        Assert.True(plan.RecommendedBytes >= 32 * GB - 2 * MB, "full size");

        plan = IbraDriveCheck.Core.Engine.CapacityFixPlanner.Plan(g, Array.Empty<ScanResult>());
        Assert.Eq(IbraDriveCheck.Core.Engine.CapacitySource.None, plan.Source, "none");
    }
}

public static partial class Tests
{
    static Dictionary<string, byte[]> MakeFatWithFiles(string img, int seed)
    {
        Run("mkfs.fat", $"-F 32 -C -n IBRA \"{img}\" 65536");
        var files = new Dictionary<string, byte[]>
        {
            ["photo.jpg"] = SampleFiles.Jpeg(70_000, seed),
            ["Work/report.pdf"] = SampleFiles.Pdf(30_000, seed + 1),
        };
        Run("mmd", $"-i \"{img}\" ::Work", MtoolsEnv);
        foreach (var (rel, data) in files)
        {
            string local = img + "." + Path.GetFileName(rel);
            File.WriteAllBytes(local, data);
            MCopy(img, local, rel);
        }
        return files;
    }

    static void RescueAndCompare(string diskImg, Dictionary<string, byte[]> files, string expectFs, string expectBoot)
    {
        using var disk = new FileVolumeSource(diskImg);
        Assert.True(RecoveryEngine.DetectFileSystem(disk) != expectFs || expectBoot == "table", "really damaged/unreadable at offset 0");
        var cands = RescueEngine.Find(disk);
        var c = cands.FirstOrDefault(x => x.FileSystem == expectFs);
        Assert.True(c != null, "candidate found: " + string.Join(", ", cands.Select(x => $"{x.FileSystem}@{x.Offset}/{x.Boot}")));
        Assert.Eq(expectBoot, c!.Boot, "boot source");
        using var vol = RescueEngine.Open(disk, c);
        var scan = RecoveryEngine.Scan(vol, false, new RecoveryProgress(), default, includeLive: true);
        string outDir = Temp("rescued");
        RecoveryEngine.Recover(vol, scan.Items, outDir, new RecoveryProgress(), default);
        foreach (var (rel, data) in files)
        {
            string path = Path.Combine(new[] { outDir }.Concat(rel.Split('/')).ToArray());
            Assert.True(File.Exists(path), $"rescued {rel} ({string.Join(", ", scan.Items.Select(i => i.FullPath))})");
            Assert.Eq(SampleFiles.Hash(data), SampleFiles.Hash(path), "content " + rel);
        }
        Assert.True(scan.Items.All(i => !i.Deleted), "live files listed as existing");
    }

    public static void T_Rescue_FAT32_Destroyed_Boot_Sector()
    {
        string img = Path.Combine(Temp("rescue1"), "flash.img");
        var files = MakeFatWithFiles(img, 31);
        using (var fs = new FileStream(img, FileMode.Open, FileAccess.Write)) fs.Write(new byte[512]);   // "تطلب تهيئة"
        RescueAndCompare(img, files, "FAT32", "backup");
    }

    public static void T_Rescue_Lost_Partition_Table()
    {
        string dir = Temp("rescue2");
        string part = Path.Combine(dir, "part.img");
        var files = MakeFatWithFiles(part, 41);
        string disk = Path.Combine(dir, "disk.img");
        using (var o = File.Create(disk))
        {
            var mbr = new byte[1 << 20];      // جدول أقسام ممسوح بالكامل
            o.Write(mbr);
            using var i = File.OpenRead(part);
            i.CopyTo(o);
        }
        RescueAndCompare(disk, files, "FAT32", "primary");
    }

    public static void T_Rescue_NTFS_Backup_Boot_From_Partition_Table()
    {
        string dir = Temp("rescue3");
        string part = Path.Combine(dir, "ntfs.img");
        using (var f = File.Create(part)) f.SetLength(64L << 20);
        Run("mkntfs", $"-F -Q -q -s 512 -c 4096 \"{part}\"");
        var data = SampleFiles.Pdf(200_000, 77);
        File.WriteAllBytes(Path.Combine(dir, "x"), data);
        Run("ntfscp", $"-f -q \"{part}\" \"{Path.Combine(dir, "x")}\" \"important.pdf\"");
        string disk = Path.Combine(dir, "disk.img");
        using (var o = File.Create(disk))
        {
            var mbr = new byte[1 << 20];
            mbr[446 + 4] = 0x07;
            BitConverter.GetBytes(2048u).CopyTo(mbr, 446 + 8);
            BitConverter.GetBytes((uint)((64L << 20) / 512)).CopyTo(mbr, 446 + 12);
            mbr[510] = 0x55; mbr[511] = 0xAA;
            o.Write(mbr);
            var bytes = File.ReadAllBytes(part);
            Array.Clear(bytes, 0, 512);        // قطاع إقلاع NTFS تالف
            o.Write(bytes);
        }
        RescueAndCompare(disk, new() { ["important.pdf"] = data }, "NTFS", "backup");
    }
}
