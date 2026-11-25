using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Engine;

/// <summary>
/// يجمع نتائج الفحوصات (السعة، الصحة، السرعة) في حكم واحد: زين اخذه / مقبول بملاحظات / لا تاخذه.
/// قسم التوافق مع الحاسبة اختياري ومنفصل عن الحكم.
/// </summary>
public static class PurchaseAdvisor
{
    private const long GB = 1_000_000_000L;
    private const long MbrLimit = 2_199_023_255_552L; // 2TiB

    private static readonly string[] GenericNames =
    {
        "GENERIC", "NVME SSD", "SSD DISK", "USB DISK", "MASS STORAGE", "FLASH DISK", "UDISK", "USB DEVICE", "DISK 2.0", "DISK 3.0",
    };

    public static PurchaseAdvice Evaluate(DriveSnapshot d, ScanResult? capacity, ScanResult? speed, HealthInfo? health, ScanResult? readOnly,
                                          bool checkCompatibility, SystemSnapshot? sys)
    {
        var a = new PurchaseAdvice();
        var checks = a.Checks;

        // 1) السعة الحقيقية
        if (capacity == null) checks.Add(new("adv.capacity", CheckStatus.NotTested, "adv.capNotTested"));
        else switch (capacity.Verdict)
        {
            case Verdict.FakeCapacity:
                checks.Add(new("adv.capacity", CheckStatus.Fail, "adv.capFake", Fmt.Bytes(capacity.DeclaredBytes), Fmt.Bytes(capacity.EstimatedRealBytes ?? 0)));
                break;
            case Verdict.ReadWriteErrors:
                checks.Add(new("adv.capacity", CheckStatus.Fail, "adv.capErrors"));
                break;
            case Verdict.Incomplete:
            case Verdict.Inconclusive:
                checks.Add(new("adv.capacity", CheckStatus.Warn, "adv.capIncomplete"));
                break;
            default:
                checks.Add(new("adv.capacity", CheckStatus.Pass, capacity.VerifiedBytes != null ? "adv.capVerified" : "adv.capOk", Fmt.Bytes(capacity.DeclaredBytes)));
                break;
        }

        // 2) الموديل
        string name = $"{d.Manufacturer} {d.Model}".Trim();
        string upper = name.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(d.Model) || GenericNames.Any(g => upper.Contains(g)) && !KnownBrand(upper))
            checks.Add(new("adv.identity", CheckStatus.Warn, "adv.genericModel", string.IsNullOrWhiteSpace(name) ? "?" : name));
        else
            checks.Add(new("adv.identity", CheckStatus.Info, "adv.model", name, d.SerialNumber ?? "—"));

        // 3) جديد أم مستعمل
        long? hours = health?.PowerOnHours;
        long? written = health?.DataWrittenBytes;
        if (hours == null && written == null && health?.PercentUsed == null)
            checks.Add(new("adv.usage", CheckStatus.NotTested, "adv.usageUnknown"));
        else if ((hours ?? 0) <= 50 && (written ?? 0) <= 200 * GB && (health?.PercentUsed ?? 0) <= 1)
            checks.Add(new("adv.usage", CheckStatus.Pass, "adv.usageNew", hours?.ToString() ?? "—", (written is long wb ? Fmt.Bytes(wb) : "—")));
        else if ((health?.PercentUsed ?? 0) >= 90)
            checks.Add(new("adv.usage", CheckStatus.Fail, "adv.usageWorn", health!.PercentUsed!.Value));
        else
            checks.Add(new("adv.usage", CheckStatus.Warn, "adv.usageUsed", hours?.ToString() ?? "—", (written is long wb ? Fmt.Bytes(wb) : "—"), health?.PercentUsed?.ToString() ?? "—"));

        // 4) الصحة
        if (health == null || (!health.SmartSupported && health.HealthStatus == null && !health.NvmeLogAvailable))
            checks.Add(new("adv.health", CheckStatus.NotTested, "adv.healthUnknown"));
        else if (health.PredictFailure == true || (health.CriticalWarning ?? 0) != 0 || (health.MediaErrors ?? 0) > 0
                 || health.HealthStatus == "Unhealthy" || health.AvailableSpare is < 10)
            checks.Add(new("adv.health", CheckStatus.Fail, "adv.healthBad", health.MediaErrors?.ToString() ?? "0", health.CriticalWarning?.ToString() ?? "0"));
        else if (health.HealthStatus == "Warning" || (health.ReadErrorsUncorrected ?? 0) > 0)
            checks.Add(new("adv.health", CheckStatus.Warn, "adv.healthWarn"));
        else
            checks.Add(new("adv.health", CheckStatus.Pass, "adv.healthOk"));

        if (health?.TemperatureC is double t)
            checks.Add(new("adv.temp", t >= 70 ? CheckStatus.Warn : CheckStatus.Pass, t >= 70 ? "adv.tempHot" : "adv.tempOk", t.ToString("0")));

        // 5) السرعة
        var sp = speed?.Speed;
        bool ssd = d.Kind is MediaKind.ExternalSsd or MediaKind.InternalSsd || d.BusType == "NVMe" || d.SeekPenalty == false && d.Kind != MediaKind.UsbFlash && d.Kind != MediaKind.SdCard;
        if (speed == null || sp == null || !sp.SeqRead.Measured)
        {
            checks.Add(new("adv.speed", CheckStatus.NotTested, "adv.speedNotTested"));
        }
        else if (speed.Verdict == Verdict.ReadWriteErrors)
        {
            checks.Add(new("adv.speed", CheckStatus.Fail, "adv.speedErrors"));
        }
        else
        {
            double r = sp.SeqRead.AvgMBps, w = sp.SeqWrite.Measured ? sp.SeqWrite.AvgMBps : -1;
            string ws = w >= 0 ? Fmt.Speed(w) : "—";
            bool usb = d.BusType == "USB";
            if (usb && r < 45)
                checks.Add(new("adv.speed", CheckStatus.Warn, "adv.speedUsb2", Fmt.Speed(r), ws));
            else if (ssd && (r < 150 || (w >= 0 && w < 50)))
                checks.Add(new("adv.speed", CheckStatus.Warn, "adv.speedSlow", Fmt.Speed(r), ws));
            else if (!ssd && (r < 8 || (w >= 0 && w < 3)))
                checks.Add(new("adv.speed", CheckStatus.Warn, "adv.speedSlow", Fmt.Speed(r), ws));
            else
                checks.Add(new("adv.speed", CheckStatus.Pass, "adv.speedOk", Fmt.Speed(r), ws));

            // انهيار السرعة أثناء الكتابة المستمرة (ذاكرة رديئة بعد امتلاء الكاش)
            if (sp.SeqWrite.Measured && sp.SeqWrite.Samples.Count >= 8)
            {
                var tail = sp.SeqWrite.Samples.Skip(sp.SeqWrite.Samples.Count / 2).ToList();
                double tailAvg = tail.Average();
                if (tailAvg < sp.SeqWrite.MaxMBps * 0.2)
                    checks.Add(new("adv.sustained", CheckStatus.Warn, "adv.speedCollapse", Fmt.Speed(sp.SeqWrite.MaxMBps), Fmt.Speed(tailAvg)));
            }
            if (sp.RawReadOnly) checks.Add(new("adv.writeSpeed", CheckStatus.Info, "adv.rawSpeedNote"));
        }

        // 6) أخطاء القراءة
        if (readOnly != null && readOnly.ReadErrorCells > 0)
            checks.Add(new("adv.readScan", CheckStatus.Fail, "adv.readErrors", readOnly.ReadErrorCells));

        if (checks.Any(c => c.Status == CheckStatus.Fail)) a.Level = AdviceLevel.DoNotBuy;
        else if (checks.First().Status == CheckStatus.NotTested) a.Level = AdviceLevel.NeedTests;
        else if (checks.Any(c => c.Status is CheckStatus.Warn or CheckStatus.NotTested)) a.Level = AdviceLevel.Caution;
        else a.Level = AdviceLevel.Buy;

        if (checkCompatibility && sys != null)
        {
            a.CompatibilityChecked = true;
            a.System = sys;
            Compat(a.Compatibility, d, sys);
        }
        return a;
    }

    private static bool KnownBrand(string upper) =>
        new[] { "SAMSUNG", "WDC", "WESTERN DIGITAL", "WD ", "CRUCIAL", "MICRON", "KINGSTON", "SANDISK", "SEAGATE", "TOSHIBA", "KIOXIA", "INTEL", "SK HYNIX", "HYNIX", "ADATA", "LEXAR", "TRANSCEND", "PNY", "CORSAIR", "TEAM", "SABRENT", "PATRIOT", "HP ", "LENOVO", "HIKVISION", "SOLIDIGM", "VERBATIM", "NETAC", "SILICON POWER", "APACER", "GIGABYTE", "MSI" }
            .Any(b => upper.Contains(b));

    private static void Compat(List<AdviceCheck> list, DriveSnapshot d, SystemSnapshot sys)
    {
        if (d.IsExternalBus)
            list.Add(new("adv.compatConnection", CheckStatus.Info, "adv.compatUsb"));

        if (sys.FirmwareType == "UEFI")
            list.Add(new("adv.compatBoot", CheckStatus.Pass, "adv.compatUefi"));
        else if (sys.FirmwareType == "Legacy")
            list.Add(new("adv.compatBoot", d.DeclaredBytes > MbrLimit ? CheckStatus.Warn : CheckStatus.Info,
                d.DeclaredBytes > MbrLimit ? "adv.compatLegacyBig" : "adv.compatLegacy"));

        if (d.PartitionStyle == "MBR" && d.DeclaredBytes > MbrLimit)
            list.Add(new("adv.compatPartition", CheckStatus.Warn, "adv.compatMbr", Fmt.Bytes(d.DeclaredBytes)));
        else if (d.PartitionStyle is null or "RAW")
            list.Add(new("adv.compatPartition", CheckStatus.Info, "adv.compatRaw"));
        else
            list.Add(new("adv.compatPartition", CheckStatus.Pass, "adv.compatPartOk", d.PartitionStyle));

        if (sys.LinkGenCurrent is int cg && sys.LinkGenMax is int mg)
        {
            int cw = sys.LinkWidthCurrent ?? 0, mw = sys.LinkWidthMax ?? cw;
            bool slower = cg < mg || cw < mw;
            list.Add(new("adv.compatLink", slower ? CheckStatus.Warn : CheckStatus.Pass,
                slower ? "adv.compatLinkSlow" : "adv.compatLinkOk", cg, cw, mg, mw));
        }
        else if (!d.IsExternalBus && d.BusType == "NVMe")
        {
            list.Add(new("adv.compatLink", CheckStatus.NotTested, "adv.compatLinkUnknown"));
        }
    }
}
