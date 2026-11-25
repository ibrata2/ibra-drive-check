using System.Runtime.InteropServices;

namespace IbraDriveCheck.Core.Devices;

/// <summary>
/// وحدة تخزين يمكن القراءة منها والكتابة عليها بالبايت (بعناوين وأحجام محاذاة على حجم القطاع).
/// التنفيذ الحقيقي على ويندوز هو <c>RawDiskDevice</c>، وفي الاختبارات <c>SimulatedDevice</c>.
/// </summary>
public interface IBlockDevice : IDisposable
{
    /// <summary>السعة التي تبلغ عنها وحدة التخزين (المعلنة) بالبايت.</summary>
    long Size { get; }

    /// <summary>حجم القطاع المنطقي.</summary>
    int SectorSize { get; }

    /// <summary>المحاذاة المطلوبة لعناوين وأطوال عمليات القراءة/الكتابة.</summary>
    int Alignment { get; }

    bool CanWrite { get; }

    /// <summary>يقرأ بالضبط buffer.Length بايت من العنوان offset. يرمي BlockIoException أو DeviceGoneException.</summary>
    void Read(long offset, Span<byte> buffer);

    /// <summary>يكتب بالضبط buffer.Length بايت في العنوان offset. يرمي BlockIoException أو DeviceGoneException.</summary>
    void Write(long offset, ReadOnlySpan<byte> buffer);

    void Flush();
}

/// <summary>خطأ قراءة أو كتابة في منطقة محددة (الجهاز ما زال متصلاً).</summary>
public sealed class BlockIoException : IOException
{
    public long Offset { get; }
    public bool IsWrite { get; }
    public int NativeError { get; }

    public BlockIoException(long offset, bool isWrite, int nativeError, string? message = null)
        : base(message ?? $"{(isWrite ? "Write" : "Read")} error at 0x{offset:X} (code {nativeError})")
    {
        Offset = offset;
        IsWrite = isWrite;
        NativeError = nativeError;
    }
}

/// <summary>انقطع الاتصال بوحدة التخزين أو أزيلت أثناء العمل.</summary>
public sealed class DeviceGoneException : IOException
{
    public int NativeError { get; }
    public DeviceGoneException(int nativeError = 0, string? message = null)
        : base(message ?? $"Device disconnected (code {nativeError})") => NativeError = nativeError;
}

/// <summary>ذاكرة محاذاة (مطلوبة للقراءة/الكتابة المباشرة بدون كاش ويندوز).</summary>
public sealed unsafe class AlignedBuffer : IDisposable
{
    private byte* _ptr;
    public int Length { get; }

    public AlignedBuffer(int length, int alignment = 4096)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (alignment < 512) alignment = 512;
        _ptr = (byte*)NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment);
        NativeMemory.Clear(_ptr, (nuint)length);
        Length = length;
    }

    public Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_ptr == null, this);
            return new Span<byte>(_ptr, Length);
        }
    }

    public Span<byte> Slice(int offset, int length) => Span.Slice(offset, length);

    public void Dispose()
    {
        if (_ptr != null)
        {
            NativeMemory.AlignedFree(_ptr);
            _ptr = null;
        }
        GC.SuppressFinalize(this);
    }

    ~AlignedBuffer()
    {
        if (_ptr != null) NativeMemory.AlignedFree(_ptr);
    }
}
