using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.Core.Devices.Windows;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Storage;

namespace IbraDriveCheck.App.ViewModels;

/// <summary>وحدة تخزين متصلة كما تظهر في الواجهة.</summary>
public sealed class DriveItem : ObservableObject
{
    public DriveItem(DriveSnapshot d) => Drive = d;

    public DriveSnapshot Drive { get; private set; }
    private ScanResult? _last;
    public ScanResult? Last { get => _last; set { _last = value; RaiseAll(); } }
    private HealthInfo? _health;
    public HealthInfo? Health { get => _health; set { _health = value; RaiseAll(); } }

    public void Update(DriveSnapshot d) { Drive = d; RaiseAll(); }

    public string Name => Drive.DisplayName;
    public string Letters => Drive.Letters.Length > 0 ? Drive.Letters : "—";
    public string SizeText => Fmt.Bytes(Drive.DeclaredBytes);
    public string KindText => Strings.Media(Loc.I.Lang, Drive.Kind) + (Drive.IsSystemDisk ? " · " + Loc.T("common.system") : "");
    public string Glyph => VerdictColors.MediaGlyph(Drive.Kind);
    public string Display => $"{Name}  {(Drive.Letters.Length > 0 ? "(" + Drive.Letters + ")" : "")}  —  {SizeText}";
    public bool HasResult => Last != null;
    public string LastText => Last == null ? Loc.T("dash.never")
        : Last.Advice != null ? Strings.Level(Loc.I.Lang, Last.Advice.Level)
        : Strings.Verdict(Loc.I.Lang, Last.Verdict);
    public Brush LastBrush => Last == null ? new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)) : VerdictColors.For(Last.Verdict);
    public string LastDetail => Last == null ? "" :
        Last.EstimatedRealBytes is long e ? $"{Loc.T("res.estimatedReal")}: {Fmt.Bytes(e)}" :
        Last.VerifiedBytes is long v ? $"{Loc.T("res.verified")}: {Fmt.Bytes(v)}" :
        Last.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public void RefreshTexts() => RaiseAll();
}

public sealed class NavItem : ObservableObject
{
    public string Key { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string TitleKey => "nav." + Key;
    public string Title => Loc.T(TitleKey);
    public void Refresh() => Raise(nameof(Title));
}

public sealed class MainViewModel : ObservableObject
{
    public static MainViewModel I { get; } = new();

    public AppSettings Settings { get; } = AppSettings.Load();
    public HistoryStore Store { get; } = new();
    public ObservableCollection<ScanResult> History { get; } = new();
    public ObservableCollection<DriveItem> Drives { get; } = new();
    public NotifyService? Notify { get; set; }

    public List<NavItem> Nav { get; } = new()
    {
        new() { Key = "dashboard", Glyph = "" },
        new() { Key = "scanner", Glyph = "" },
        new() { Key = "fake", Glyph = "" },
        new() { Key = "speed", Glyph = "" },
        new() { Key = "health", Glyph = "" },
        new() { Key = "tools", Glyph = "\uE90F" },
        new() { Key = "recovery", Glyph = "\uE777" },
        new() { Key = "wipe", Glyph = "\uE75C" },
        new() { Key = "history", Glyph = "" },
        new() { Key = "reports", Glyph = "" },
        new() { Key = "settings", Glyph = "" },
        new() { Key = "about", Glyph = "" },
    };

    public bool IsElevated { get; } = DriveEnumerator.IsElevated;

    private DriveItem? _selected;
    public DriveItem? SelectedDrive
    {
        get => _selected;
        set { if (Set(ref _selected, value)) SelectedDriveChanged?.Invoke(); }
    }
    public event Action? SelectedDriveChanged;

    private bool _busy;
    /// <summary>فحص واحد فقط في نفس الوقت على مستوى البرنامج.</summary>
    public bool IsBusy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(IsIdle)); } }
    public bool IsIdle => !_busy;

    private bool _refreshing;
    public bool IsRefreshing { get => _refreshing; set => Set(ref _refreshing, value); }

    public event Action<string>? NavigateRequested;
    public void Navigate(string page) => NavigateRequested?.Invoke(page);

    // ── الإحصائيات ──
    public int StatConnected => Drives.Count;
    public int StatHealthy => Drives.Count(d => d.Last?.Verdict == Verdict.Genuine);
    public int StatSuspicious => Drives.Count(d => d.Last?.Verdict is Verdict.ReadWriteErrors or Verdict.PoorPerformance);
    public int StatFake => History.Where(h => h.Verdict == Verdict.FakeCapacity).Select(h => h.Drive.HardwareKey).Distinct().Count();
    public int StatTotalScans => History.Count;
    public string StatVerified => Fmt.Bytes(History.Where(h => h.VerifiedBytes != null && h.Verdict != Verdict.Incomplete).Sum(h => h.VerifiedBytes!.Value));

    public void RaiseStats()
    {
        Raise(nameof(StatConnected)); Raise(nameof(StatHealthy)); Raise(nameof(StatSuspicious));
        Raise(nameof(StatFake)); Raise(nameof(StatTotalScans)); Raise(nameof(StatVerified));
    }

    private MainViewModel()
    {
        foreach (var r in Store.LoadAll()) History.Add(r);
        Loc.I.LanguageChanged += () =>
        {
            foreach (var d in Drives) d.RefreshTexts();
            foreach (var n in Nav) n.Refresh();
            RaiseStats();
        };
    }

    public static readonly ScanKind[] CapacityKinds = { ScanKind.Quick, ScanKind.Deep, ScanKind.FullFreeSpace, ScanKind.FullDestructive, ScanKind.ReadOnly };

    public ScanResult? LastFor(DriveSnapshot d) => History.FirstOrDefault(h => h.Drive.HardwareKey == d.HardwareKey && (h.Kind == ScanKind.Purchase || CapacityKinds.Contains(h.Kind) && h.Kind != ScanKind.ReadOnly))
                                                ?? History.FirstOrDefault(h => h.Drive.HardwareKey == d.HardwareKey);

    /// <summary>تحديث قائمة الوحدات (يُستدعى عند البدء وعند توصيل/إزالة جهاز).</summary>
    public async Task RefreshDrivesAsync(bool notifyNew)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var list = await Task.Run(() => { try { return DriveEnumerator.Enumerate(); } catch { return new List<DriveSnapshot>(); } });
            var oldKeys = Drives.Select(d => d.Drive.HardwareKey).ToHashSet();
            var keepSel = SelectedDrive?.Drive.HardwareKey;

            // الأجهزة الخارجية أولاً
            list = list.OrderByDescending(d => d.IsExternalBus).ThenBy(d => d.IsSystemDisk).ThenBy(d => d.DiskNumber).ToList();
            var items = new List<DriveItem>();
            foreach (var d in list)
            {
                var existing = Drives.FirstOrDefault(x => x.Drive.HardwareKey == d.HardwareKey);
                if (existing != null) { existing.Update(d); items.Add(existing); }
                else items.Add(new DriveItem(d) { Last = LastFor(d) });
            }
            // أثناء الفحص لا نغير القائمة جذرياً
            if (!IsBusy || items.Count != Drives.Count)
            {
                Drives.Clear();
                foreach (var i in items) Drives.Add(i);
            }
            SelectedDrive = Drives.FirstOrDefault(x => x.Drive.HardwareKey == keepSel)
                            ?? (IsBusy ? SelectedDrive : null)
                            ?? Drives.FirstOrDefault(x => x.Drive.IsExternalBus)
                            ?? Drives.FirstOrDefault();

            if (notifyNew && Settings.NotifyNewDrive)
                foreach (var d in list.Where(d => !oldKeys.Contains(d.HardwareKey) && d.IsExternalBus))
                    Notify?.Show("IBRA DRIVE CHECK", Loc.F("notify.newDrive", $"{d.DisplayName} {d.Letters}"));
            RaiseStats();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>يحفظ النتيجة في السجل ويحدث الإحصائيات ويرسل الإشعارات.</summary>
    public void AddResult(ScanResult r)
    {
        try { Store.Save(r); } catch { }
        History.Insert(0, r);
        foreach (var d in Drives.Where(d => d.Drive.HardwareKey == r.Drive.HardwareKey))
        {
            if (r.Kind is not (ScanKind.Health or ScanKind.Speed)) d.Last = r;
            if (r.Health != null) d.Health = r.Health;
        }
        RaiseStats();

        string name = r.Drive.DisplayName;
        if (r.DeviceDisconnected && Settings.NotifyDisconnect) Notify?.Show("IBRA DRIVE CHECK", Loc.F("notify.disconnect", name), true);
        else if (r.Verdict == Verdict.FakeCapacity && Settings.NotifyFake) Notify?.Show("IBRA DRIVE CHECK", Loc.F("notify.fake", name, Fmt.Bytes(r.EstimatedRealBytes, Loc.I.Lang)), true);
        else if (r.Verdict == Verdict.ReadWriteErrors && Settings.NotifyErrors) Notify?.Show("IBRA DRIVE CHECK", Loc.F("notify.errors", name), true);
        else if (Settings.NotifyScanDone) Notify?.Show("IBRA DRIVE CHECK", Loc.F("notify.done", name, Strings.Verdict(Loc.I.Lang, r.Verdict)));

        if (r.Health?.TemperatureC is double t && t >= 60 && Settings.NotifyHot)
            Notify?.Show("IBRA DRIVE CHECK", Loc.F("notify.hot", name, t.ToString("0")), true);
    }

    /// <summary>حفظ نتيجة خطوة من الفحص الشامل بدون إشعار منفصل (الإشعار يكون للحكم النهائي).</summary>
    public void AddResultSilently(ScanResult r)
    {
        try { Store.Save(r); } catch { }
        History.Insert(0, r);
        foreach (var d in Drives.Where(d => d.Drive.HardwareKey == r.Drive.HardwareKey))
        {
            if (r.Kind is not (ScanKind.Health or ScanKind.Speed)) d.Last = r;
            if (r.Health != null) d.Health = r.Health;
        }
        RaiseStats();
    }

    public void DeleteResults(IEnumerable<ScanResult> items)
    {
        foreach (var r in items.ToList())
        {
            try { Store.Delete(r.Id); } catch { }
            History.Remove(r);
        }
        foreach (var d in Drives) d.Last = LastFor(d.Drive);
        RaiseStats();
    }

    public void DeleteAllResults()
    {
        try { Store.DeleteAll(); } catch { }
        History.Clear();
        foreach (var d in Drives) d.Last = null;
        RaiseStats();
    }

    public void SaveSettings()
    {
        try { Settings.Save(); } catch { }
    }

    /// <summary>صفوف معلومات وحدة التخزين (القسم 5 من المواصفات) — غير المتوفر يظهر "غير متوفر".</summary>
    public static List<InfoRow> InfoRows(DriveSnapshot d, HealthInfo? h, ScanResult? last)
    {
        string lang = Loc.I.Lang;
        string NA = Loc.T("common.notAvailable");
        var rows = new List<InfoRow>
        {
            new(Loc.T("info.name"), d.DisplayName),
            new(Loc.T("info.manufacturer"), d.Manufacturer ?? NA),
            new(Loc.T("info.model"), d.Model ?? NA),
            new(Loc.T("info.serial"), d.SerialNumber ?? NA),
            new(Loc.T("info.firmware"), d.Firmware ?? NA),
            new(Loc.T("info.kind"), Strings.Media(lang, d.Kind)),
            new(Loc.T("info.bus"), d.BusType ?? NA),
            new(Loc.T("info.usb"), d.UsbVersion ?? NA),
            new(Loc.T("info.fs"), d.FileSystems ?? NA),
            new(Loc.T("info.letters"), d.Letters.Length > 0 ? d.Letters : NA),
            new(Loc.T("info.declared"), Fmt.Bytes(d.DeclaredBytes), true),
            new(Loc.T("info.verified"), last?.VerifiedBytes is long v ? Fmt.Bytes(v)
                : last?.EstimatedRealBytes is long e ? $"{Fmt.Bytes(e)} ({Loc.T("res.estimatedNote")})" : NA, true),
            new(Loc.T("info.used"), Fmt.Bytes(d.UsedBytes, lang)),
            new(Loc.T("info.free"), Fmt.Bytes(d.FreeBytes, lang)),
            new(Loc.T("info.temp"), h?.TemperatureC is double t ? $"{t:0} °C" : NA),
            new(Loc.T("info.smart"), h == null ? NA : h.HealthStatus != null ? Loc.T("health." + h.HealthStatus) : h.PredictFailure is bool p ? (p ? Loc.T("health.predictFailure") : "OK") : NA),
            new(Loc.T("info.vidpid"), d.VendorId != null ? $"{d.VendorId} / {d.ProductId}" : NA),
            new(Loc.T("info.wp"), d.WriteProtected is bool w ? (w ? Loc.T("info.wpOn") : Loc.T("info.wpOff")) : NA),
            new(Loc.T("info.disk"), d.DiskNumber.ToString()),
            new(Loc.T("info.sector"), d.PhysicalSectorSize > 0 && d.PhysicalSectorSize != d.SectorSize ? $"{d.SectorSize} / {d.PhysicalSectorSize} B" : $"{d.SectorSize} B"),
            new(Loc.T("info.partition"), d.PartitionStyle ?? NA),
        };
        return rows;
    }
}
