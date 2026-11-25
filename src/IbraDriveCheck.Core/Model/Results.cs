namespace IbraDriveCheck.Core.Model;

public enum ScanKind
{
    Quick,            // فحص سريع: عينات موزعة على كامل السعة (كتابة مؤقتة مع استعادة البيانات الأصلية)
    Deep,             // فحص عميق: عينات أكثر بكثير
    FullFreeSpace,    // فحص السعة الكامل في المساحة الفارغة (ملفات مؤقتة — غير مدمر)
    FullDestructive,  // فحص السعة الكامل لكامل الوحدة (يمسح كل البيانات)
    ReadOnly,         // فحص القراءة فقط
    Speed,            // اختبار السرعة
    Health,           // صحة وحدة التخزين (SMART)
    Purchase,         // الفحص الشامل للشراء: الصحة + كشف السعة + السرعة + حكم نهائي
}

public enum Verdict
{
    Genuine,          // السعة سليمة وفق نطاق الاختبار
    FakeCapacity,     // تم اكتشاف سعة مزيفة
    ReadWriteErrors,  // أخطاء قراءة وكتابة
    PoorPerformance,  // أداء ضعيف
    Inconclusive,     // نتيجة غير حاسمة
    Incomplete,       // الفحص غير مكتمل
}

public enum MediaKind
{
    Unknown,
    UsbFlash,
    SdCard,
    ExternalHdd,
    ExternalSsd,
    InternalHdd,
    InternalSsd,
    Virtual,
}

public sealed class VolumeSnapshot
{
    public string Letter { get; set; } = "";       // "E:"
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

/// <summary>معلومات وحدة التخزين. كل حقل غير متاح يبقى null ولا يتم اختلاقه.</summary>
public sealed class DriveSnapshot
{
    public int DiskNumber { get; set; } = -1;
    public string? DevicePath { get; set; }
    public string? FriendlyName { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    /// <summary>اسم المنتج كما يبلغ عنه الجهاز مباشرة (يستخدم لبصمة الهوية).</summary>
    public string? HardwareProduct { get; set; }
    public string? HardwareSerial { get; set; }
    public string? SerialNumber { get; set; }
    public string? Firmware { get; set; }
    public string? BusType { get; set; }          // USB / SD / SATA / NVMe ...
    public MediaKind Kind { get; set; }
    public string? UsbVersion { get; set; }
    public string? VendorId { get; set; }
    public string? ProductId { get; set; }
    public string? PnpDeviceId { get; set; }
    public long DeclaredBytes { get; set; }
    public int SectorSize { get; set; } = 512;
    public int PhysicalSectorSize { get; set; }
    public bool? WriteProtected { get; set; }
    public bool? Removable { get; set; }
    public bool? SeekPenalty { get; set; }
    public bool IsSystemDisk { get; set; }
    public bool HasProtectedPartitions { get; set; }   // EFI / Recovery / MSR
    public string? PartitionStyle { get; set; }
    public List<VolumeSnapshot> Volumes { get; set; } = new();

    public long? UsedBytes => Volumes.Count == 0 ? null : Volumes.Sum(v => v.UsedBytes);
    public long? FreeBytes => Volumes.Count == 0 ? null : Volumes.Sum(v => v.FreeBytes);
    public string? FileSystems => Volumes.Count == 0 ? null : string.Join(", ", Volumes.Select(v => v.FileSystem).Where(s => s.Length > 0).Distinct());
    public string DisplayName => FriendlyName ?? Model ?? (DiskNumber >= 0 ? $"Disk {DiskNumber}" : "Drive");
    public string Letters => string.Join(" ", Volumes.Select(v => v.Letter));

    public bool IsExternalBus => BusType is "USB" or "SD" or "MMC";

    /// <summary>بصمة الهوية: تُفحص مرة ثانية قبل أي كتابة للتأكد أن القرص لم يتغير.</summary>
    public string IdentityKey => $"{DiskNumber}|{HardwareKey}";
    /// <summary>بصمة الجهاز بدون رقم القرص (رقم القرص قد يتغير بعد إعادة التوصيل).</summary>
    public string HardwareKey => $"{HardwareProduct}|{HardwareSerial}|{DeclaredBytes}";
}

public sealed class HealthInfo
{
    public bool SmartSupported { get; set; }
    public string? HealthStatus { get; set; }        // Healthy / Warning / Unhealthy / Unknown
    public bool? PredictFailure { get; set; }
    public double? TemperatureC { get; set; }
    public double? TemperatureMaxC { get; set; }
    public long? PowerOnHours { get; set; }
    public long? StartStopCycles { get; set; }
    public long? ReadErrorsTotal { get; set; }
    public long? ReadErrorsCorrected { get; set; }
    public long? ReadErrorsUncorrected { get; set; }
    public long? WriteErrorsTotal { get; set; }
    public long? WriteErrorsUncorrected { get; set; }
    public int? WearPercent { get; set; }
    public string? OperationalStatus { get; set; }
    public string? MediaType { get; set; }
    // من سجل SMART الخاص بـ NVMe (عند توفره)
    public bool NvmeLogAvailable { get; set; }
    public long? DataWrittenBytes { get; set; }
    public long? DataReadBytes { get; set; }
    public int? PercentUsed { get; set; }
    public int? AvailableSpare { get; set; }
    public long? PowerCycles { get; set; }
    public long? UnsafeShutdowns { get; set; }
    public long? MediaErrors { get; set; }
    public int? CriticalWarning { get; set; }
    public List<string> Messages { get; set; } = new();  // مفاتيح ملاحظات قابلة للترجمة
}

public sealed class SpeedStat
{
    public double AvgMBps { get; set; }
    public double MinMBps { get; set; }
    public double MaxMBps { get; set; }
    public double Iops { get; set; }
    public double AvgLatencyMs { get; set; }
    public double MinLatencyMs { get; set; }
    public double MaxLatencyMs { get; set; }
    public List<double> Samples { get; set; } = new();
    public bool Measured { get; set; }
}

public sealed class SpeedResult
{
    public long TestFileBytes { get; set; }
    /// <summary>true = قراءة مباشرة فقط (وحدة بدون قسم) — سرعة الكتابة غير مقاسة.</summary>
    public bool RawReadOnly { get; set; }
    public SpeedStat SeqRead { get; set; } = new();
    public SpeedStat SeqWrite { get; set; } = new();
    public SpeedStat RandRead4K { get; set; } = new();
    public SpeedStat RandWrite4K { get; set; } = new();
}

public sealed class NoteItem
{
    public string Key { get; set; } = "";
    public string[] Args { get; set; } = Array.Empty<string>();
    public NoteItem() { }
    public NoteItem(string key, params object[] args)
    {
        Key = key;
        Args = args.Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToArray();
    }
}

public sealed class ScanResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ScanKind Kind { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public Verdict Verdict { get; set; } = Verdict.Inconclusive;
    public DriveSnapshot Drive { get; set; } = new();

    public long DeclaredBytes { get; set; }
    /// <summary>سعة تم التحقق منها بالكامل (كتابة وقراءة كل بايت) — فقط في الفحص الكامل.</summary>
    public long? VerifiedBytes { get; set; }
    /// <summary>سعة حقيقية مقدرة من العينات أو من نمط الالتفاف.</summary>
    public long? EstimatedRealBytes { get; set; }
    public long? EstimatedFakeBytes { get; set; }
    public bool EstimateFromWrap { get; set; }

    /// <summary>نسبة البايتات التي تم كتابتها وقراءتها فعلاً من السعة المعلنة.</summary>
    public double CoveragePercent { get; set; }
    public long BytesTested { get; set; }
    public int SampleCount { get; set; }

    public int GoodCells { get; set; }
    public int SlowCells { get; set; }
    public int FailedCells { get; set; }
    public int WriteErrorCells { get; set; }
    public int ReadErrorCells { get; set; }
    public int UntestedCells { get; set; }

    public double AvgReadMBps { get; set; }
    public double AvgWriteMBps { get; set; }
    public double MinReadMBps { get; set; }
    public double MaxReadMBps { get; set; }
    public double MinWriteMBps { get; set; }
    public double MaxWriteMBps { get; set; }

    public bool Cancelled { get; set; }
    public bool DeviceDisconnected { get; set; }
    public bool? RestoreOk { get; set; }
    public int RestoreFailures { get; set; }
    public string? Error { get; set; }
    public TimeSpan Duration => FinishedUtc > StartedUtc ? FinishedUtc - StartedUtc : TimeSpan.Zero;

    public List<NoteItem> Notes { get; set; } = new();
    public CellRecord[] Map { get; set; } = Array.Empty<CellRecord>();
    public SpeedResult? Speed { get; set; }
    public HealthInfo? Health { get; set; }
    public PurchaseAdvice? Advice { get; set; }

    public void Note(string key, params object[] args) => Notes.Add(new NoteItem(key, args));
}
