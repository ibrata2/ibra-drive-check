using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace IbraDriveCheck.Tests;

/// <summary>ملفات تجريبية ببنية صحيحة لاختبار الاستخراج بالتوقيع.</summary>
public static class SampleFiles
{
    static byte[] Rand(int n, int seed, bool noFF = false)
    {
        var r = new Random(seed);
        var b = new byte[n];
        r.NextBytes(b);
        if (noFF) for (int i = 0; i < n; i++) if (b[i] == 0xFF) b[i] = 0xFE;
        return b;
    }

    static void BE(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    static void LE(Stream s, uint v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 24)); }

    public static byte[] Jpeg(int size, int seed)
    {
        var m = new MemoryStream();
        m.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 });
        // APP1 فيه صورة مصغرة كاملة (FFD8..FFD9) — يجب ألا يتوقف الاستخراج عندها
        var thumb = new byte[] { 0xFF, 0xD8, 0x11, 0x22, 0xFF, 0xD9 };
        int app1 = 2 + 6 + thumb.Length;
        m.Write(new byte[] { 0xFF, 0xE1, (byte)(app1 >> 8), (byte)app1 });
        m.Write("Exif\0\0"u8);
        m.Write(thumb);
        m.Write(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 0x3F, 0 }.AsSpan(0, 10));
        var data = Rand(size, seed, noFF: true);
        for (int i = 1000; i < data.Length; i += 5000) { data[i] = 0xFF; data[i + 1] = 0x00; }   // stuffing
        for (int i = 3000; i < data.Length; i += 7000) { data[i] = 0xFF; data[i + 1] = 0xD3; }   // RST
        m.Write(data);
        m.Write(new byte[] { 0xFF, 0xD9 });
        return m.ToArray();
    }

    public static byte[] Png(int size, int seed)
    {
        var m = new MemoryStream();
        m.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string t, byte[] d) { BE(m, (uint)d.Length); m.Write(Encoding.ASCII.GetBytes(t)); m.Write(d); BE(m, 0x12345678); }
        Chunk("IHDR", new byte[13]);
        Chunk("IDAT", Rand(size, seed));
        Chunk("IEND", Array.Empty<byte>());
        return m.ToArray();
    }

    public static byte[] Pdf(int size, int seed)
    {
        var m = new MemoryStream();
        m.Write("%PDF-1.4\n1 0 obj\n<< /Length 0 >>\nstream\n"u8);
        var d = Rand(size, seed);
        for (int i = 0; i < d.Length; i++) if (d[i] == (byte)'%') d[i] = (byte)'#';
        m.Write(d);
        m.Write("\nendstream\nendobj\nstartxref\n0\n%%EOF\n"u8);
        return m.ToArray();
    }

    public static byte[] Docx(int size, int seed)
    {
        var m = new MemoryStream();
        using (var z = new ZipArchive(m, ZipArchiveMode.Create, true))
        {
            using (var s = z.CreateEntry("word/document.xml", CompressionLevel.NoCompression).Open()) s.Write(Rand(size, seed));
            using (var s = z.CreateEntry("[Content_Types].xml").Open()) s.Write("<Types/>"u8);
        }
        return m.ToArray();
    }

    public static byte[] Mp4(int size, int seed)
    {
        var m = new MemoryStream();
        BE(m, 24); m.Write("ftypisom"u8); BE(m, 0x200); m.Write("isommp41"u8);
        var data = Rand(size, seed);
        BE(m, (uint)(8 + data.Length)); m.Write("mdat"u8); m.Write(data);
        var moov = Rand(300, seed + 1);
        BE(m, (uint)(8 + moov.Length)); m.Write("moov"u8); m.Write(moov);
        return m.ToArray();
    }

    public static byte[] Wav(int size, int seed)
    {
        var m = new MemoryStream();
        var data = Rand(size, seed);
        m.Write("RIFF"u8); LE(m, (uint)(4 + 8 + 16 + 8 + data.Length)); m.Write("WAVE"u8);
        m.Write("fmt "u8); LE(m, 16); m.Write(new byte[16]);
        m.Write("data"u8); LE(m, (uint)data.Length); m.Write(data);
        return m.ToArray();
    }

    public static byte[] Gif(int seed)
    {
        var m = new MemoryStream();
        m.Write("GIF89a"u8);
        m.Write(new byte[] { 10, 0, 10, 0, 0x80, 0, 0 });       // شاشة 10x10 مع جدول ألوان 2
        m.Write(new byte[6]);
        m.Write(new byte[] { 0x2C, 0, 0, 0, 0, 10, 0, 10, 0, 0, 2 });
        var r = new Random(seed);
        for (int b = 0; b < 40; b++) { m.WriteByte(200); var d = new byte[200]; r.NextBytes(d); m.Write(d); }
        m.WriteByte(0);
        m.WriteByte(0x3B);
        return m.ToArray();
    }

    public static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
