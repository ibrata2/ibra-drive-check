using System.Text;
using IbraDriveCheck.Core.Engine;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Recovery;
using IbraDriveCheck.Core.Security;
using IbraDriveCheck.Core.Storage;
using IbraDriveCheck.Core.Tools;

namespace IbraDriveCheck.Tests;

public static partial class Tests
{
    public static void T_Image_Vhd_RoundTrip()
    {
        string dir = Temp("image");
        string src = Path.Combine(dir, "src.img");
        var data = new byte[8 << 20];
        new Random(5).NextBytes(data);
        File.WriteAllBytes(src, data);
        string vhd = Path.Combine(dir, "copy.vhd");
        ImageResult r;
        using (var s = new FileVolumeSource(src)) r = DiskImager.Create(s, vhd, vhd: true, new RecoveryProgress(), default);
        Assert.True(r.Vhd && r.Bytes == data.Length, "vhd created");
        var all = File.ReadAllBytes(vhd);
        Assert.Eq(data.Length + 512L, all.LongLength, "footer appended");
        var footer = all.AsSpan(all.Length - 512).ToArray();
        Assert.True(footer.AsSpan(0, 8).SequenceEqual("conectix"u8), "cookie");
        uint stored = (uint)(footer[64] << 24 | footer[65] << 16 | footer[66] << 8 | footer[67]);
        footer[64] = footer[65] = footer[66] = footer[67] = 0;
        uint sum = 0; foreach (var b in footer) sum += b;
        Assert.Eq(~sum, stored, "footer checksum");
        Assert.Eq((long)data.Length, DiskImager.DataSize(vhd), "data size without footer");

        var clock = new SimClock();
        var dev = new SimulatedDevice(16 << 20, 16 << 20, FakeMode.Genuine, clock);
        DiskImager.Restore(vhd, dev, new RecoveryProgress(), default);
        var back = new byte[data.Length];
        dev.Read(0, back);
        Assert.True(back.AsSpan().SequenceEqual(data), "restored content");
        var (c, h, sp) = DiskImager.Geometry(8 << 20);
        Assert.True(c > 0 && h >= 4 && sp > 0, "geometry");
    }

    public static void T_Damaged_File_Checker()
    {
        string dir = Temp("damaged");
        void W(string n, byte[] d) => File.WriteAllBytes(Path.Combine(dir, n), d);
        W("ok.jpg", SampleFiles.Jpeg(50_000, 1));
        W("ok.pdf", SampleFiles.Pdf(10_000, 2));
        W("ok.docx", SampleFiles.Docx(10_000, 3));
        W("ok.mp4", SampleFiles.Mp4(50_000, 4));
        var png = SampleFiles.Png(5_000, 5);
        // PNG بـ CRC صحيح
        int p = 8;
        while (p < png.Length) { int len = (int)Le.BE32(png, p); uint crc = Crc32.Update(0xFFFFFFFF, png.AsSpan(p + 4, 4 + len)) ^ 0xFFFFFFFF; png[p + 8 + len] = (byte)(crc >> 24); png[p + 9 + len] = (byte)(crc >> 16); png[p + 10 + len] = (byte)(crc >> 8); png[p + 11 + len] = (byte)crc; p += 12 + len; }
        W("ok.png", png);
        var badPng = (byte[])png.Clone(); badPng[100] ^= 0xFF; W("bitflip.png", badPng);
        var jpg = SampleFiles.Jpeg(50_000, 6); W("cut.jpg", jpg.AsSpan(0, jpg.Length / 2).ToArray());
        W("zeros.jpg", new byte[100_000]);
        W("zeros.txt", new byte[100_000]);
        W("renamed.pdf", SampleFiles.Jpeg(5_000, 7));
        var zip = SampleFiles.Docx(10_000, 8); zip[zip.Length / 3] ^= 0x55; W("broken.docx", zip);
        W("note.txt", Encoding.UTF8.GetBytes("hello"));

        var res = DamagedFileChecker.Scan(dir, new RecoveryProgress(), default);
        var bad = res.Damaged.ToDictionary(d => Path.GetFileName(d.Path), d => d.ReasonKey);
        foreach (var good in new[] { "ok.jpg", "ok.pdf", "ok.docx", "ok.mp4", "ok.png", "note.txt" })
            Assert.True(!bad.ContainsKey(good), $"{good} reported damaged: {(bad.TryGetValue(good, out var why) ? why : "")}");
        Assert.Eq("damaged.crc", bad.GetValueOrDefault("bitflip.png"), "png crc");
        Assert.True(bad.ContainsKey("cut.jpg"), "truncated jpg");
        Assert.Eq("damaged.zeros", bad.GetValueOrDefault("zeros.jpg"), "zeros jpg");
        Assert.Eq("damaged.zeros", bad.GetValueOrDefault("zeros.txt"), "zeros any file");
        Assert.Eq("damaged.header", bad.GetValueOrDefault("renamed.pdf"), "wrong header");
        Assert.True(bad.ContainsKey("broken.docx"), "broken zip");
    }

    public static void T_BurnIn_Detects_Late_Failure()
    {
        var good = new SimFileSpace("/sim", 128L << 20, 0, 128L << 20, wrap: false);
        var r = new BurnInTest(good, UsbDrive(128L << 20), () => 45).Run(TimeSpan.FromMinutes(5), 3, 32L << 20, new ScanSession(), null, default);
        Assert.Eq(3, r.Passes.Count, "passes");
        Assert.Eq(Verdict.Genuine, r.Verdict, "genuine");
        Assert.Eq(45.0, r.MaxTemperatureC, "temp");

        var fake = new SimFileSpace("/sim", 128L << 20, 0, 16L << 20, wrap: true);
        r = new BurnInTest(fake, UsbDrive(128L << 20)).Run(TimeSpan.FromMinutes(5), 2, 64L << 20, new ScanSession(), null, default);
        Assert.Eq(Verdict.ReadWriteErrors, r.Verdict, "errors detected");
    }

    public static void T_Ssd_Life_And_Model_Db()
    {
        var spec = ModelDatabase.Match("Samsung", "Samsung SSD 990 PRO 2TB");
        Assert.True(spec?.Name == "Samsung 990 PRO", "match 990 pro");
        Assert.Eq(1200, spec!.TbwFor(2_000_000_000_000), "tbw 2TB");
        Assert.True(ModelDatabase.Match(null, "CT1000P3PSSD8")?.Name == "Crucial P3 Plus", "crucial code");
        Assert.True(ModelDatabase.Match(null, "KINGSTON SNV2S1000G")?.Name == "Kingston NV2", "kingston code");
        Assert.True(ModelDatabase.Match(null, "Generic NVMe SSD 4TB") == null, "generic not matched");

        var h = new HealthInfo { PercentUsed = 10, DataWrittenBytes = 120_000_000_000_000, PowerOnHours = 8 * 365 * 2 };
        var e = SsdLife.Estimate(h, 2_000_000_000_000, spec, null, DateTime.UtcNow);
        Assert.Eq(90, e.HealthPercent, "health");
        Assert.True(e.YearsLeft is > 10 and < 30, "years " + e.YearsLeft);   // 120TB في سنتين، باقي 1080TB ≈ 18 سنة

        var noData = SsdLife.Estimate(new HealthInfo(), 1, null, null, DateTime.UtcNow);
        Assert.True(noData.HealthPercent == null && noData.BasisKey == "life.basis.none", "no data");
    }

    static byte[] Lnk(string target, string args)
    {
        var m = new MemoryStream();
        var hdr = new byte[0x4C];
        hdr[0] = 0x4C;
        BitConverter.GetBytes(0x20 | 0x80).CopyTo(hdr, 0x14);   // HasArguments | IsUnicode
        m.Write(hdr);
        m.Write(BitConverter.GetBytes((ushort)args.Length));
        m.Write(Encoding.Unicode.GetBytes(args));
        m.Write(Encoding.ASCII.GetBytes(target));
        return m.ToArray();
    }

    public static void T_Usb_Guard()
    {
        Assert.True(UsbGuard.ShortcutThreat(Lnk(@"C:\Windows\System32\cmd.exe", "/c start wscript.exe \"x.vbs\" & start explorer \"My Files\"")) != null, "virus lnk");
        Assert.True(UsbGuard.ShortcutThreat(Lnk(@"E:\Docs\report.docx", "")) == null, "normal lnk");
        Assert.True(UsbGuard.ShortcutThreat(Lnk(@"C:\Windows\explorer.exe", "E:\\Photos")) == null, "plain explorer lnk not flagged");

        string root = Temp("usb");
        string old = AppPaths.Root;
        AppPaths.Root = Temp("appdata");
        try
        {
            File.WriteAllText(Path.Combine(root, "autorun.inf"), "[autorun]\nopen=wscript.exe x.vbs\n");
            File.WriteAllBytes(Path.Combine(root, "My Files.lnk"), Lnk(@"C:\Windows\System32\cmd.exe", "/c start wscript x.vbs"));
            File.WriteAllText(Path.Combine(root, "photo.jpg"), "x");
            var scan = UsbGuard.Scan(root);
            Assert.True(scan.Threats.Any(t => t.Kind == ThreatKind.AutorunInf), "autorun found");
            Assert.True(scan.Threats.Any(t => t.Kind == ThreatKind.MaliciousShortcut), "shortcut found");
            var cleaned = UsbGuard.Clean(root, scan, vaccinate: true);
            Assert.Eq(2, cleaned.Quarantined, "quarantined");
            Assert.True(Directory.Exists(Path.Combine(root, "autorun.inf")), "vaccine folder");
            Assert.True(File.Exists(Path.Combine(root, "photo.jpg")), "user file untouched");
            Assert.True(Directory.GetFiles(cleaned.QuarantineFolder!).Length == 2, "files in quarantine");
            var again = UsbGuard.Scan(root);
            Assert.True(again.Vaccinated && again.Threats.Count == 0, "clean after");
        }
        finally { AppPaths.Root = old; }
    }

    public static void T_Secure_Erase_Overwrites_Everything()
    {
        var clock = new SimClock();
        var dev = new SimulatedDevice(32 << 20, 32 << 20, FakeMode.Genuine, clock);
        var r = SecureEraser.Overwrite(dev, 2, new RecoveryProgress(), default);
        Assert.True(r.Verified && r.Passes == 2 && r.WriteErrors == 0, "erased & verified");
        var buf = new byte[32 << 20];
        dev.Read(0, buf);
        Assert.True(buf.AsSpan().IndexOfAnyExcept((byte)0) < 0, "all zeros");
    }
}
