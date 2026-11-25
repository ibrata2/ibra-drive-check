namespace IbraDriveCheck.Core.Model;

public enum AdviceLevel
{
    Buy,          // زين — اخذه
    Caution,      // مقبول بملاحظات
    DoNotBuy,     // لا تاخذه
    NeedTests,    // لا توجد فحوصات كافية بعد
}

public enum CheckStatus
{
    Pass,
    Warn,
    Fail,
    NotTested,
    Info,
}

/// <summary>بند واحد في تقرير الشراء (مفتاح العنوان + الحالة + تفصيل بمفتاح ترجمة ووسائط).</summary>
public sealed class AdviceCheck
{
    public string TitleKey { get; set; } = "";
    public CheckStatus Status { get; set; }
    public NoteItem? Detail { get; set; }
    public AdviceCheck() { }
    public AdviceCheck(string title, CheckStatus status, string? detailKey = null, params object[] args)
    {
        TitleKey = title;
        Status = status;
        if (detailKey != null) Detail = new NoteItem(detailKey, args);
    }
}

public sealed class PurchaseAdvice
{
    public AdviceLevel Level { get; set; } = AdviceLevel.NeedTests;
    public List<AdviceCheck> Checks { get; set; } = new();
    public List<AdviceCheck> Compatibility { get; set; } = new();
    public bool CompatibilityChecked { get; set; }
    public SystemSnapshot? System { get; set; }
}

/// <summary>مواصفات الحاسبة الحالية (لقسم التوافق). كل حقل غير متاح يبقى null.</summary>
public sealed class SystemSnapshot
{
    public string? ComputerName { get; set; }
    public string? Os { get; set; }
    public string? Cpu { get; set; }
    public long? RamBytes { get; set; }
    public string? Board { get; set; }
    public string? BoardMaker { get; set; }
    public string? Bios { get; set; }
    public string? FirmwareType { get; set; }     // UEFI / Legacy
    /// <summary>سرعة PCIe الحالية والقصوى للوحدة (1..5 = Gen1..Gen5) وعدد المسارات — للأقراص NVMe الداخلية فقط.</summary>
    public int? LinkGenCurrent { get; set; }
    public int? LinkGenMax { get; set; }
    public int? LinkWidthCurrent { get; set; }
    public int? LinkWidthMax { get; set; }
}
