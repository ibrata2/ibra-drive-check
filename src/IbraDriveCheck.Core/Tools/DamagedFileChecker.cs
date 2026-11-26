using System.IO.Compression;
using IbraDriveCheck.Core.Recovery;

namespace IbraDriveCheck.Core.Tools;

public sealed record DamagedFile(string Path, long Size, string ReasonKey);

public sealed class DamagedScanResult
{
    public int Checked;
    public int Skipped;
    public List<DamagedFile> Damaged { get; } = new();
    public bool Cancelled;
}

/// <summary>
/// يمر على الصور والفيديوهات والمستندات ويتحقق من بنيتها الداخلية (وليس الامتداد فقط).
/// يكشف أيضاً الملفات التي صار محتواها أصفاراً — علامة شائعة للفلاشات المزيفة.
/// </summary>
public static class DamagedFileChecker
{
    public static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".pdf", ".zip", ".docx", ".xlsx", ".pptx", ".apk", ".jar", ".odt",
        ".mp4", ".mov", ".m4a", ".m4v", ".3gp", ".wav", ".avi", ".webp",
    };

    public static DamagedScanResult Scan(string root, RecoveryProgress p, CancellationToken ct, bool allFilesZeroCheck = true)
    {
        var res = new DamagedScanResult();
        p.Phase = "damaged.phase.list";
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        var files = Directory.EnumerateFiles(root, "*", opts)
            .Where(f => !f.Contains("System Volume Information", StringComparison.OrdinalIgnoreCase) && !f.Contains("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase))
            .ToList();
        p.Phase = "damaged.phase.check";
        p.SetTotal(Math.Max(1, files.Count));
        foreach (var f in files)
        {
            if (ct.IsCancellationRequested) { res.Cancelled = true; break; }
            p.Add(1);
            bool typed = Supported.Contains(Path.GetExtension(f));
            if (!typed && !allFilesZeroCheck) { res.Skipped++; continue; }
            string? reason;
            long size = 0;
            try
            {
                size = new FileInfo(f).Length;
                reason = Check(f, typed);
            }
            catch (IOException) { reason = "damaged.readError"; }
            catch (UnauthorizedAccessException) { res.Skipped++; continue; }
            res.Checked++;
            if (reason != null)
            {
                res.Damaged.Add(new DamagedFile(f, size, reason));
                p.Found = res.Damaged.Count;
            }
        }
        return res;
    }

    /// <summary>null = سليم، وإلا مفتاح سبب التلف.</summary>
    public static string? Check(string path, bool typed = true)
    {
        long len = new FileInfo(path).Length;
        if (len == 0) return typed ? "damaged.empty" : null;
        if (IsUniform(path, len, out byte b)) return b == 0 ? "damaged.zeros" : "damaged.uniform";
        if (!typed) return null;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        using var src = new FileVolumeSource(path);
        var head = new byte[16];
        src.ReadAt(0, head);
        string? kind = Carver.Sniff(head);
        string expected = ext switch
        {
            ".jpg" or ".jpeg" => "jpg",
            ".png" => "png",
            ".gif" => "gif",
            ".pdf" => "pdf",
            ".mp4" or ".mov" or ".m4a" or ".m4v" or ".3gp" => "mp4",
            ".wav" or ".avi" or ".webp" => "riff",
            _ => "zip",
        };
        if (kind != expected) return "damaged.header";
        if (expected == "zip") return CheckZip(path);
        var carver = new Carver(src);
        long measured = carver.Measure(kind, 0, len);
        if (measured <= 0) return "damaged.structure";
        if (measured > len) return "damaged.truncated";
        if (expected == "png" && !PngCrcOk(path)) return "damaged.crc";
        return null;
    }

    private static bool IsUniform(string path, long len, out byte value)
    {
        value = 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        var buf = new byte[64 << 10];
        bool first = true;
        foreach (long at in new[] { 0, Math.Max(0, len / 2 - buf.Length / 2), Math.Max(0, len - buf.Length) }.Distinct())
        {
            fs.Position = at;
            int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, len - at));
            if (n <= 0) continue;
            if (first) { value = buf[0]; first = false; }
            if (buf.AsSpan(0, n).IndexOfAnyExcept(value) >= 0) return false;
        }
        return !first;
    }

    private static string? CheckZip(string path)
    {
        try
        {
            using var z = ZipFile.OpenRead(path);
            var buf = new byte[1 << 16];
            foreach (var e in z.Entries)
            {
                if (e.Length == 0) continue;
                uint crc = 0xFFFFFFFF;
                using var s = e.Open();
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) crc = Crc32.Update(crc, buf.AsSpan(0, n));
                if ((crc ^ 0xFFFFFFFF) != e.Crc32) return "damaged.crc";
            }
            return null;
        }
        catch (InvalidDataException) { return "damaged.structure"; }
    }

    private static bool PngCrcOk(string path)
    {
        var d = File.ReadAllBytes(path);
        int p = 8;
        while (p + 12 <= d.Length)
        {
            int len = (int)Le.BE32(d, p);
            if (len < 0 || p + 12 + len > d.Length) return false;
            uint crc = Crc32.Update(0xFFFFFFFF, d.AsSpan(p + 4, 4 + len)) ^ 0xFFFFFFFF;
            if (crc != Le.BE32(d, p + 8 + len)) return false;
            bool end = d[p + 4] == 'I' && d[p + 5] == 'E' && d[p + 6] == 'N' && d[p + 7] == 'D';
            p += 12 + len;
            if (end) return true;
        }
        return false;
    }
}

public static class Crc32
{
    private static readonly uint[] Table = Build();
    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
