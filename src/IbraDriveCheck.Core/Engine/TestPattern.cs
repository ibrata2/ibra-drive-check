using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace IbraDriveCheck.Core.Engine;

public enum BlockStatus : byte
{
    /// <summary>البيانات مطابقة للنمط المكتوب في نفس الموقع.</summary>
    Ok,
    /// <summary>وجدنا نمط هذا الفحص لكنه يعود لموقع آخر → الذاكرة تلتف وتكتب فوق مناطق سابقة.</summary>
    Aliased,
    /// <summary>النمط موجود لكن البيانات تالفة (Hash غير مطابق).</summary>
    Corrupted,
    /// <summary>لا يوجد أي أثر للنمط (أصفار / FF / بيانات أخرى) → منطقة وهمية لا تخزن.</summary>
    Missing,
}

public readonly record struct BlockCheck(BlockStatus Status, long FoundBlockId = -1, long FoundOffset = -1, string? Detail = null);

/// <summary>
/// نمط اختبار فريد لكل كتلة 4KB:
/// [0..8) IBRADCHK | [8..24) معرّف الجلسة | [24..32) رقم الكتلة | [32..40) العنوان
/// [40..56) أول 16 بايت من SHA-256 للحمولة | [56..64) أصفار | [64..4096) حمولة شبه عشوائية مشتقة من الجلسة ورقم الكتلة.
/// </summary>
public static class TestPattern
{
    public const int BlockSize = 4096;
    public const int HeaderSize = 64;
    private static ReadOnlySpan<byte> Magic => "IBRADCHK"u8;

    public static byte[] NewNonce()
    {
        var n = new byte[16];
        RandomNumberGenerator.Fill(n);
        return n;
    }

    /// <summary>يملأ مقطعاً (مضاعفات 4KB) بكتل متتالية تبدأ بالرقم firstBlockId والعنوان firstOffset.</summary>
    public static void FillRange(Span<byte> dst, ReadOnlySpan<byte> nonce, long firstBlockId, long firstOffset)
    {
        if (dst.Length % BlockSize != 0) throw new ArgumentException("Length must be a multiple of 4096");
        int n = dst.Length / BlockSize;
        for (int i = 0; i < n; i++)
            Fill(dst.Slice(i * BlockSize, BlockSize), nonce, firstBlockId + i, firstOffset + (long)i * BlockSize);
    }

    public static void Fill(Span<byte> block, ReadOnlySpan<byte> nonce, long blockId, long offset)
    {
        if (block.Length != BlockSize) throw new ArgumentException("Block must be 4096 bytes");
        if (nonce.Length != 16) throw new ArgumentException("Nonce must be 16 bytes");
        var payload = block[HeaderSize..];
        FillPayload(payload, nonce, blockId);
        Magic.CopyTo(block);
        nonce.CopyTo(block.Slice(8, 16));
        BinaryPrimitives.WriteInt64LittleEndian(block.Slice(24, 8), blockId);
        BinaryPrimitives.WriteInt64LittleEndian(block.Slice(32, 8), offset);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        hash[..16].CopyTo(block.Slice(40, 16));
        block.Slice(56, 8).Clear();
    }

    public static BlockCheck Verify(ReadOnlySpan<byte> block, ReadOnlySpan<byte> nonce, long expectedBlockId, long expectedOffset)
    {
        if (block.Length != BlockSize) throw new ArgumentException("Block must be 4096 bytes");
        if (!block[..8].SequenceEqual(Magic) || !block.Slice(8, 16).SequenceEqual(nonce))
            return new BlockCheck(BlockStatus.Missing, Detail: Describe(block));

        long foundId = BinaryPrimitives.ReadInt64LittleEndian(block.Slice(24, 8));
        long foundOffset = BinaryPrimitives.ReadInt64LittleEndian(block.Slice(32, 8));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(block[HeaderSize..], hash);
        if (!hash[..16].SequenceEqual(block.Slice(40, 16)))
            return new BlockCheck(BlockStatus.Corrupted, foundId, foundOffset, "hash");

        if (foundId != expectedBlockId || foundOffset != expectedOffset)
            return new BlockCheck(BlockStatus.Aliased, foundId, foundOffset);

        return new BlockCheck(BlockStatus.Ok, foundId, foundOffset);
    }

    private static string Describe(ReadOnlySpan<byte> block)
    {
        if (block.IndexOfAnyExcept((byte)0) < 0) return "zeros";
        if (block.IndexOfAnyExcept((byte)0xFF) < 0) return "ff";
        return "foreign";
    }

    private static void FillPayload(Span<byte> dst, ReadOnlySpan<byte> nonce, long blockId)
    {
        ulong a = BinaryPrimitives.ReadUInt64LittleEndian(nonce);
        ulong b = BinaryPrimitives.ReadUInt64LittleEndian(nonce[8..]);
        ulong s = SplitMix(a ^ (b * 0x9E3779B97F4A7C15UL) ^ ((ulong)blockId * 0xBF58476D1CE4E5B9UL));
        if (s == 0) s = 0x1234567890ABCDEFUL;
        var words = MemoryMarshal.Cast<byte, ulong>(dst);
        for (int i = 0; i < words.Length; i++)
        {
            s ^= s >> 12;
            s ^= s << 25;
            s ^= s >> 27;
            words[i] = s * 0x2545F4914F6CDD1DUL;
        }
    }

    private static ulong SplitMix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
