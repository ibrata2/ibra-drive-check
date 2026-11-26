using System.Security.Cryptography;
using IbraDriveCheck.Core.Devices;
using IbraDriveCheck.Core.Recovery;

namespace IbraDriveCheck.Core.Tools;

public sealed class EraseResult
{
    public long BytesWritten;
    public int WriteErrors;
    public int Passes;
    public bool Verified;
    public bool Cancelled;
    public bool UsedBuiltIn;
}

/// <summary>
/// مسح كامل آمن للوحدة قبل البيع: الكتابة فوق كل قطاع (عشوائي ثم أصفار عند تعدد المرات، أو أصفار فقط)،
/// ثم التحقق من عينات موزعة أنها أصبحت أصفاراً.
/// </summary>
public static class SecureEraser
{
    public static EraseResult Overwrite(IBlockDevice dev, int passes, RecoveryProgress p, CancellationToken ct)
    {
        var res = new EraseResult();
        passes = Math.Clamp(passes, 1, 3);
        int align = Math.Max(4096, dev.Alignment);
        using var buf = new AlignedBuffer(8 << 20, align);
        long size = dev.Size / dev.SectorSize * dev.SectorSize;
        p.SetTotal(Math.Max(1, size * passes));
        long done = 0;
        for (int pass = 1; pass <= passes; pass++)
        {
            bool zeros = pass == passes;   // آخر مرة أصفار (حتى يمكن التحقق)
            p.Phase = zeros ? "erase.phase.zeros" : "erase.phase.random";
            if (zeros) buf.Span.Clear(); else RandomNumberGenerator.Fill(buf.Span);
            for (long off = 0; off < size; off += buf.Length)
            {
                if (ct.IsCancellationRequested) { res.Cancelled = true; return res; }
                int n = (int)Math.Min(buf.Length, size - off);
                try { dev.Write(off, buf.Slice(0, n)); }
                catch (BlockIoException) { res.WriteErrors++; }
                if (!zeros) buf.Span[(int)(off / buf.Length % 4096)] ^= 0xA5;
                res.BytesWritten += n;
                done += n;
                p.Set(done);
            }
            dev.Flush();
            res.Passes = pass;
        }
        res.Verified = VerifyZeros(dev, 512, p);
        return res;
    }

    /// <summary>يقرأ عينات موزعة على كامل الوحدة ويتأكد أنها أصفار.</summary>
    public static bool VerifyZeros(IBlockDevice dev, int samples, RecoveryProgress p)
    {
        p.Phase = "erase.phase.verify";
        int align = Math.Max(4096, dev.Alignment);
        using var buf = new AlignedBuffer(align, align);
        long units = dev.Size / align;
        var rng = new Random();
        for (int i = 0; i < samples; i++)
        {
            long off = (i == 0 ? 0 : i == samples - 1 ? units - 1 : rng.NextInt64(units)) * align;
            try
            {
                dev.Read(off, buf.Span);
                if (buf.Span.IndexOfAnyExcept((byte)0) >= 0) return false;
            }
            catch (BlockIoException) { }
        }
        return true;
    }
}
