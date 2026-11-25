using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Safety;

public sealed record SafetyDecision(bool Allowed, string ReasonKey)
{
    public static SafetyDecision Ok => new(true, "safety.ok");
    public static SafetyDecision Deny(string key) => new(false, key);
}

/// <summary>
/// قواعد الحماية: متى يسمح البرنامج بكل نوع فحص على وحدة تخزين معينة.
/// الكتابة المباشرة (Raw) مسموحة فقط على وحدات USB/SD الخارجية التي ليست قرص نظام ولا تحتوي أقسام إقلاع أو استعادة.
/// </summary>
public static class SafetyPolicy
{
    public static bool WritesData(ScanKind kind) => kind is ScanKind.Quick or ScanKind.Deep or ScanKind.FullFreeSpace or ScanKind.FullDestructive or ScanKind.Speed;
    public static bool NeedsRawAccess(ScanKind kind) => kind is ScanKind.Quick or ScanKind.Deep or ScanKind.FullDestructive or ScanKind.ReadOnly;
    public static bool NeedsRawAccess(ScanKind kind, DriveSnapshot d) => NeedsRawAccess(kind) || (kind == ScanKind.Speed && d.Volumes.Count == 0);

    /// <summary>قرص داخلي جديد: ليس قرص النظام، بلا أقسام بحرف، وغير مهيأ.</summary>
    public static bool IsBlankInternal(DriveSnapshot d) =>
        !d.IsSystemDisk && !d.HasProtectedPartitions && d.Volumes.Count == 0 && d.PartitionStyle is null or "RAW"
        && d.BusType is "NVMe" or "SATA" or "SAS" or "ATA" or "SCSI";
    public static bool DestroysData(ScanKind kind) => kind == ScanKind.FullDestructive;

    public static SafetyDecision Check(DriveSnapshot d, ScanKind kind)
    {
        switch (kind)
        {
            case ScanKind.ReadOnly:
            case ScanKind.Health:
            case ScanKind.Purchase:
                return SafetyDecision.Ok;

            case ScanKind.Speed:
                // بدون قسم: قراءة مباشرة فقط (لا كتابة). مع قسم: ملف مؤقت في المساحة الفارغة.
                if (d.Volumes.Count == 0) return SafetyDecision.Ok;
                if (d.WriteProtected == true) return SafetyDecision.Deny("safety.writeProtected");
                return SafetyDecision.Ok;

            case ScanKind.FullFreeSpace:
                // اختبار بملفات مؤقتة داخل نظام الملفات: يحتاج قسم بحرف ومساحة فارغة
                if (d.WriteProtected == true) return SafetyDecision.Deny("safety.writeProtected");
                if (d.Volumes.Count == 0) return SafetyDecision.Deny("safety.noVolume");
                if (d.IsSystemDisk || !d.IsExternalBus) return SafetyDecision.Deny("safety.fillInternal");
                return SafetyDecision.Ok;

            case ScanKind.Quick:
            case ScanKind.Deep:
            case ScanKind.FullDestructive:
                if (d.IsSystemDisk) return SafetyDecision.Deny("safety.systemDisk");
                if (d.HasProtectedPartitions) return SafetyDecision.Deny("safety.protectedPartitions");
                // القرص الداخلي مسموح فقط إذا كان جديداً فارغاً (بدون أقسام) وليس قرص النظام — مثل هارد M.2 إضافي ركّبته للتو
                if (!d.IsExternalBus && !IsBlankInternal(d)) return SafetyDecision.Deny("safety.internalRaw");
                if (!d.IsExternalBus && kind == ScanKind.FullDestructive) return SafetyDecision.Deny("safety.internalRaw");
                if (d.WriteProtected == true) return SafetyDecision.Deny("safety.writeProtected");
                if (d.DeclaredBytes <= 4L * 1024 * 1024) return SafetyDecision.Deny("safety.tooSmall");
                return SafetyDecision.Ok;
        }
        return SafetyDecision.Deny("safety.unknown");
    }

    /// <summary>التحقق من أن القرص المفتوح الآن هو نفسه الذي اختاره المستخدم.</summary>
    public static bool SameIdentity(DriveSnapshot selected, DriveSnapshot current)
        => selected.IdentityKey == current.IdentityKey;
}
