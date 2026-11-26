using System.Security.Cryptography;
using IbraDriveCheck.Core.Devices;
using IbraDriveCheck.Core.Engine;

namespace IbraDriveCheck.Core.Recovery;

/// <summary>
/// إزالة آثار المحذوفات:
///  • مسح المساحة الفارغة (يملؤها ببيانات عشوائية ثم يحذفها) — يمحو محتوى كل الملفات المحذوفة.
///  • مسح أسماء المحذوفات: NTFS بملء سجلات MFT الفارغة، و FAT/exFAT بتصفير المدخلات المحذوفة مباشرة.
///  • الحذف الآمن لملفات ومجلدات محددة.
/// </summary>
public static class TraceCleaner
{
    public const string WipeFolderPrefix = "IBRA_WIPE_";

    /// <summary>يملأ المساحة الفارغة حتى آخر كتلة ثم يحذف الملفات. يرجع عدد البايتات التي تم مسحها.</summary>
    public static long WipeFreeSpace(ITestFileSpace fs, RecoveryProgress p, CancellationToken ct)
    {
        p.Phase = "wipe.phase.free";
        long free = fs.FreeBytes;
        p.SetTotal(Math.Max(1, free));
        string dir = Path.Combine(fs.Root, WipeFolderPrefix + Guid.NewGuid().ToString("N")[..8]);
        fs.CreateDirectory(dir);
        var files = new List<string>();
        long written = 0;
        using var buf = new AlignedBuffer(4 << 20, 4096);
        RandomNumberGenerator.Fill(buf.Span);
        long fileMax = Math.Min(1L << 30, fs.MaxFileBytes / (4 << 20) * (4 << 20));
        try
        {
            // كتل كبيرة ثم أصغر حتى يمتلئ آخر جزء
            foreach (int block in new[] { 4 << 20, 256 << 10, 4096 })
            {
                bool full = false;
                while (!full && !ct.IsCancellationRequested)
                {
                    string path = Path.Combine(dir, $"w{files.Count:D6}.tmp");
                    ITestFile f;
                    try { f = fs.Create(path, 0); }
                    catch (IOException) { full = true; break; }
                    files.Add(path);
                    using (f)
                    {
                        long off = 0;
                        while (off < fileMax && !ct.IsCancellationRequested)
                        {
                            // نغير البيانات قليلاً حتى لا تضغطها بعض وحدات SSD
                            buf.Span[(int)(off / block % 4096)] ^= 0x5A;
                            try { f.Write(off, buf.Slice(0, block)); }
                            catch (DiskFullException) { full = true; break; }
                            catch (BlockIoException) { full = true; break; }
                            off += block;
                            written += block;
                            p.Add(block);
                        }
                        try { f.Flush(); } catch { }
                    }
                }
            }
        }
        finally
        {
            p.Phase = "wipe.phase.cleanup";
            foreach (var f in files) { try { fs.Delete(f); } catch { } }
            try { fs.DeleteDirectory(dir); } catch { }
        }
        return written;
    }

    /// <summary>
    /// NTFS: ينشئ ملفات صغيرة بعدد سجلات MFT الفارغة حتى تُكتب فوق سجلات الملفات المحذوفة (أسماؤها وبياناتها الصغيرة)، ثم يحذفها.
    /// </summary>
    public static int FillMftRecords(string root, long count, RecoveryProgress p, CancellationToken ct)
    {
        p.Phase = "wipe.phase.mft";
        count = Math.Min(count, 2_000_000);
        p.SetTotal(Math.Max(1, count));
        string dir = Path.Combine(root, WipeFolderPrefix + "MFT_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var content = new byte[640];
        int made = 0;
        string pad = new('x', 150);
        try
        {
            for (long i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                try
                {
                    File.WriteAllBytes(Path.Combine(dir, $"{i:D7}{pad}"), content);
                    made++;
                }
                catch (IOException) { break; }   // امتلأ القرص
                p.Add(1);
            }
        }
        finally
        {
            p.Phase = "wipe.phase.cleanup";
            try { Directory.Delete(dir, true); } catch { }
        }
        return made;
    }

    /// <summary>
    /// يمسح الأسماء ومواقع البيانات من المدخلات المحذوفة في كل المجلدات الموجودة (FAT16/FAT32/exFAT).
    /// يجب أن يكون القسم مقفلاً أثناء الكتابة. يرجع عدد المدخلات التي تم مسحها.
    /// </summary>
    public static int ScrubDirectoryEntries(IVolumeSource src, Action<long, byte[]> write, string fs)
    {
        List<Extent> regions = fs == "exFAT" ? new ExFatScanner(src).LiveDirectoryRegions() : new FatScanner(src).LiveDirectoryRegions();
        int total = 0;
        foreach (var r in regions)
        {
            var buf = new byte[r.Length];
            src.ReadAt(r.Offset, buf);
            int n = fs == "exFAT" ? ExFatScanner.ScrubDeletedEntries(buf) : FatScanner.ScrubDeletedEntries(buf);
            if (n > 0)
            {
                write(r.Offset, buf);
                total += n;
            }
        }
        return total;
    }

    private static readonly string[] ProtectedRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    };

    /// <summary>هل المسار مسموح بحذفه حذفاً آمناً؟ (يمنع مجلدات النظام وجذر الأقراص)</summary>
    public static bool IsShredAllowed(string path)
    {
        try
        {
            string raw = Path.GetFullPath(path);
            string full = raw.TrimEnd(Path.DirectorySeparatorChar);
            string? root = Path.GetPathRoot(raw);
            if (full.Length == 0 || root == null || string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), full, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var p in ProtectedRoots.Where(p => !string.IsNullOrEmpty(p)))
                if (full.StartsWith(p.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, profile, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>حذف آمن: كتابة بيانات عشوائية فوق المحتوى، ثم تغيير الاسم وتصفير الحجم، ثم الحذف.</summary>
    public static void ShredFile(string path, int passes = 1)
    {
        var fi = new FileInfo(path);
        if (fi.Attributes.HasFlag(FileAttributes.ReadOnly)) fi.Attributes &= ~FileAttributes.ReadOnly;
        long len = fi.Length;
        var buf = new byte[1 << 20];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
        {
            for (int pass = 0; pass < Math.Max(1, passes); pass++)
            {
                fs.Position = 0;
                long left = len;
                while (left > 0)
                {
                    int n = (int)Math.Min(buf.Length, left);
                    if (pass % 2 == 0) RandomNumberGenerator.Fill(buf.AsSpan(0, n)); else Array.Clear(buf, 0, n);
                    fs.Write(buf, 0, n);
                    left -= n;
                }
                fs.Flush(true);
            }
            fs.SetLength(0);
            fs.Flush(true);
        }
        string dir = Path.GetDirectoryName(path)!;
        string renamed = Path.Combine(dir, new string('0', Math.Min(40, Path.GetFileName(path).Length)) + ".~");
        int k = 0;
        while (File.Exists(renamed)) renamed = Path.Combine(dir, $"{k++:D8}.~");
        File.Move(path, renamed);
        File.SetCreationTimeUtc(renamed, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(renamed, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.Delete(renamed);
    }

    /// <summary>حذف آمن لمجلد بكل محتوياته. يرجع (ملفات تم حذفها، أخطاء).</summary>
    public static (int files, int errors) ShredDirectory(string path, int passes, RecoveryProgress? p, CancellationToken ct)
    {
        int ok = 0, err = 0;
        var files = Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList();
        p?.SetTotal(Math.Max(1, files.Count));
        foreach (var f in files)
        {
            if (ct.IsCancellationRequested) break;
            try { ShredFile(f, passes); ok++; } catch { err++; }
            p?.Add(1);
        }
        if (!ct.IsCancellationRequested)
        {
            foreach (var d in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(path))
            {
                try
                {
                    string parent = Path.GetDirectoryName(d)!;
                    string tmp = Path.Combine(parent, "~" + Guid.NewGuid().ToString("N")[..8]);
                    Directory.Move(d, tmp);
                    Directory.Delete(tmp, false);
                }
                catch { err++; }
            }
        }
        return (ok, err);
    }
}
