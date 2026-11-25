using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using IbraDriveCheck.Core.Devices;

namespace IbraDriveCheck.Core.Engine;

public interface ITestFile : IDisposable
{
    void Write(long offset, ReadOnlySpan<byte> data);
    void Read(long offset, Span<byte> data);
    void Flush();
}

/// <summary>نظام ملفات يُكتب عليه ملفات اختبار مؤقتة (حقيقي على ويندوز، أو محاكى في الاختبارات).</summary>
public interface ITestFileSpace
{
    string Root { get; }
    long FreeBytes { get; }
    long TotalBytes { get; }
    /// <summary>أكبر حجم ملف مسموح (FAT32 = 4GB − 1).</summary>
    long MaxFileBytes { get; }
    void CreateDirectory(string path);
    void DeleteDirectory(string path);
    ITestFile Create(string path, long length);
    ITestFile OpenRead(string path);
    void Delete(string path);
    bool Exists(string path);
}

/// <summary>
/// ملفات حقيقية بدون كاش ويندوز (FILE_FLAG_NO_BUFFERING + WRITE_THROUGH)، حتى تأتي القراءة من وحدة التخزين نفسها لا من الذاكرة.
/// </summary>
public sealed class RealFileSpace : ITestFileSpace
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private readonly string _root;
    private readonly string? _fileSystem;

    public RealFileSpace(string root, string? fileSystem = null)
    {
        _root = root;
        _fileSystem = fileSystem;
    }

    public string Root => _root;

    private DriveInfo Info => new(Path.GetPathRoot(Path.GetFullPath(_root))!);

    public long FreeBytes => Info.AvailableFreeSpace;
    public long TotalBytes => Info.TotalSize;

    public long MaxFileBytes
    {
        get
        {
            string fs = _fileSystem ?? SafeFs();
            return fs.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) && !fs.Equals("exFAT", StringComparison.OrdinalIgnoreCase)
                ? (4L << 30) - (1L << 20)
                : long.MaxValue;
        }
    }

    private string SafeFs()
    {
        try { return Info.DriveFormat; } catch { return ""; }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteDirectory(string path) { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    public bool Exists(string path) => File.Exists(path);
    public void Delete(string path) { if (File.Exists(path)) File.Delete(path); }

    private static FileOptions Opts => OperatingSystem.IsWindows() ? FileOptions.WriteThrough | NoBuffering : FileOptions.WriteThrough;

    public ITestFile Create(string path, long length)
        => new RealTestFile(File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, Opts, preallocationSize: 0));

    public ITestFile OpenRead(string path)
        => new RealTestFile(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, Opts));

    private sealed class RealTestFile : ITestFile
    {
        private readonly SafeFileHandle _h;
        public RealTestFile(SafeFileHandle h) => _h = h;

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            try { RandomAccess.Write(_h, data, offset); }
            catch (IOException ex) { throw Map(ex, offset, true); }
        }

        public void Read(long offset, Span<byte> data)
        {
            int total = 0;
            try
            {
                while (total < data.Length)
                {
                    int n = RandomAccess.Read(_h, data[total..], offset + total);
                    if (n <= 0) throw new BlockIoException(offset + total, false, 38, "Unexpected end of file");
                    total += n;
                }
            }
            catch (IOException ex) when (ex is not BlockIoException and not DeviceGoneException) { throw Map(ex, offset, false); }
        }

        public void Flush()
        {
            try { RandomAccess.FlushToDisk(_h); } catch (IOException) { }
        }

        public void Dispose() => _h.Dispose();

        private static IOException Map(IOException ex, long offset, bool write)
        {
            int code = ex.HResult & 0xFFFF;
            // 1167 الجهاز غير متصل، 21 غير جاهز، 433 لا يوجد جهاز، 55 الجهاز غير موجود، 2/3 المسار اختفى
            if (code is 1167 or 21 or 433 or 55 or 2 or 3 or 1110) return new DeviceGoneException(code, ex.Message);
            if (code is 112 or 39) return new DiskFullException();
            return new BlockIoException(offset, write, code, ex.Message);
        }
    }
}

public sealed class DiskFullException : IOException
{
    public DiskFullException() : base("Disk full") { }
}
