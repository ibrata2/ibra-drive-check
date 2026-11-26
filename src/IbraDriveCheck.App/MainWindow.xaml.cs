using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.App.ViewModels;
using IbraDriveCheck.App.Views;

namespace IbraDriveCheck.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, SectionView> _views = new();
    private readonly DispatcherTimer _deviceDebounce;
    private MainViewModel VM => MainViewModel.I;
    public static bool ReallyExit;

    /// <summary>7 أقسام فقط في القائمة الجانبية، وكل قسم فيه تبويبات بسيطة.</summary>
    public static readonly List<SectionDef> Sections = new()
    {
        new() { Key = "home", TitleKey = "sec.home", Glyph = "", Tabs = { new() { Key = "dashboard", TitleKey = "nav.dashboard", Glyph = "", Create = () => new DashboardView() } } },
        new()
        {
            Key = "scan", TitleKey = "sec.scan", Glyph = "", Tabs =
            {
                new() { Key = "fake", TitleKey = "tab.purchase", Glyph = "", Create = () => new FakeDetectorView() },
                new() { Key = "scanner", TitleKey = "tab.capacity", Glyph = "", Create = () => new ScannerView() },
                new() { Key = "speed", TitleKey = "tab.speed", Glyph = "", Create = () => new SpeedView() },
                new() { Key = "burnin", TitleKey = "tab.burnin", Glyph = "", Create = () => new BurnInView() },
            },
        },
        new()
        {
            Key = "healthsec", TitleKey = "sec.health", Glyph = "", Tabs =
            {
                new() { Key = "health", TitleKey = "tab.health", Glyph = "", Create = () => new HealthView() },
                new() { Key = "life", TitleKey = "tab.life", Glyph = "", Create = () => new LifeView() },
                new() { Key = "monitor", TitleKey = "tab.monitor", Glyph = "", Create = () => new MonitorView() },
                new() { Key = "compare", TitleKey = "tab.compare", Glyph = "", Create = () => new CompareView() },
            },
        },
        new()
        {
            Key = "repair", TitleKey = "sec.repair", Glyph = "", Tabs =
            {
                new() { Key = "recovery", TitleKey = "tab.recovery", Glyph = "", Create = () => new RecoveryView() },
                new() { Key = "rescue", TitleKey = "tab.rescue", Glyph = "", Create = () => new RescueView() },
                new() { Key = "tools", TitleKey = "tab.capfix", Glyph = "", Create = () => new ToolsView() },
                new() { Key = "damaged", TitleKey = "tab.damaged", Glyph = "", Create = () => new DamagedView() },
                new() { Key = "clone", TitleKey = "tab.clone", Glyph = "", Create = () => new CloneView() },
            },
        },
        new()
        {
            Key = "security", TitleKey = "sec.security", Glyph = "", Tabs =
            {
                new() { Key = "guard", TitleKey = "tab.guard", Glyph = "", Create = () => new GuardView() },
                new() { Key = "writelock", TitleKey = "tab.writelock", Glyph = "", Create = () => new WriteLockView() },
                new() { Key = "wipe", TitleKey = "tab.wipe", Glyph = "", Create = () => new WipeView() },
                new() { Key = "erase", TitleKey = "tab.erase", Glyph = "", Create = () => new EraseView() },
                new() { Key = "eject", TitleKey = "tab.eject", Glyph = "", Create = () => new EjectView() },
            },
        },
        new()
        {
            Key = "records", TitleKey = "sec.records", Glyph = "", Tabs =
            {
                new() { Key = "history", TitleKey = "tab.history", Glyph = "", Create = () => new HistoryView() },
                new() { Key = "reports", TitleKey = "tab.reports", Glyph = "", Create = () => new ReportsView() },
            },
        },
        new()
        {
            Key = "settingssec", TitleKey = "sec.settings", Glyph = "", Tabs =
            {
                new() { Key = "settings", TitleKey = "tab.settings", Glyph = "", Create = () => new SettingsView() },
                new() { Key = "about", TitleKey = "tab.about", Glyph = "", Create = () => new AboutView() },
            },
        },
    };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = VM;
        NavList.ItemsSource = Sections;
        if (VM.IsElevated)
        {
            AdminBadge.Visibility = Visibility.Visible;
            AdminButton.Visibility = Visibility.Collapsed;
        }
        Loc.I.LanguageChanged += () => { foreach (var s in Sections) s.Refresh(); };

        _deviceDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _deviceDebounce.Tick += async (_, _) =>
        {
            _deviceDebounce.Stop();
            await VM.RefreshDrivesAsync(notifyNew: true);
        };

        VM.NavigateRequested += Go;

        StateChanged += (_, _) =>
        {
            // مع WindowChrome النافذة المكبرة تتجاوز حدود الشاشة قليلاً
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };

        Loaded += OnLoaded;
        Closed += (_, _) => { MonitorService.I.Stop(); VM.Notify?.Dispose(); };
    }

    /// <summary>الانتقال إلى قسم أو تبويب بمفتاحه (مثل "recovery" أو "scan").</summary>
    public void Go(string key)
    {
        var sec = Sections.FirstOrDefault(s => s.Key == key) ?? Sections.FirstOrDefault(s => s.Tabs.Any(t => t.Key == key));
        if (sec == null) return;
        if (NavList.SelectedItem != sec) NavList.SelectedItem = sec;
        if (_views.TryGetValue(sec.Key, out var v) && sec.Key != key) v.Select(key);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Dwm.RoundCorners(this);
        Dwm.ApplyDarkTitle(this, ThemeService.Current == "dark");
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        VM.Notify = new NotifyService(ShowFromTray, ExitFromTray);
        Go(App.StartPage ?? "dashboard");
        if (NavList.SelectedItem == null) NavList.SelectedIndex = 0;
        await VM.RefreshDrivesAsync(notifyNew: false);
        MonitorService.I.Apply();

        // بعد إعادة التشغيل كمسؤول: نرجع لنفس الوحدة ونوع الفحص
        if (App.StartDisk is int disk)
        {
            var d = VM.Drives.FirstOrDefault(x => x.Drive.DiskNumber == disk);
            if (d != null) VM.SelectedDrive = d;
        }
        if (App.StartKind is { } kind)
        {
            var opt = ScannerViewModel.I.Kinds.FirstOrDefault(k => k.Kind == kind);
            if (opt != null) ScannerViewModel.I.SelectedKind = opt;
        }
        if (App.Smoke) await RunSmokeTest();
    }

    public void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        ReallyExit = true;
        OnClose(this, new RoutedEventArgs());
    }

    /// <summary>اختبار تشغيل آلي: كل الأقسام وكل التبويبات باللغتين والمظهرين، ثم خروج برمز 0.</summary>
    private async Task RunSmokeTest()
    {
        foreach (var lang in new[] { "ar", "en" })
        {
            Loc.I.Lang = lang;
            foreach (var theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme);
                foreach (var s in Sections)
                    foreach (var t in s.Tabs)
                    {
                        Go(t.Key);
                        await Task.Delay(200);
                        UpdateLayout();
                    }
            }
        }
        System.IO.File.AppendAllText(App.SmokeLog, $"OK drives={VM.Drives.Count} history={VM.History.Count}{Environment.NewLine}");
        Environment.Exit(0);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x0219;
        if (msg == WM_DEVICECHANGE)
        {
            int ev = wParam.ToInt32();
            // DBT_DEVICEARRIVAL / DBT_DEVICEREMOVECOMPLETE / DBT_DEVNODES_CHANGED
            if (ev == 0x8000 || ev == 0x8004 || ev == 0x0007)
            {
                _deviceDebounce.Stop();
                _deviceDebounce.Start();
            }
        }
        return IntPtr.Zero;
    }

    private void OnNav(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not SectionDef sec) return;
        if (!_views.TryGetValue(sec.Key, out var view))
        {
            view = new SectionView(sec);
            _views[sec.Key] = view;
        }
        PageHost.Content = view;
        view.Activate();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private async void OnClose(object sender, RoutedEventArgs e)
    {
        // المراقبة بالخلفية: الإغلاق يخفي النافذة بجانب الساعة
        if (VM.Settings.MonitorEnabled && !ReallyExit)
        {
            Hide();
            if (!VM.Settings.TrayHintShown)
            {
                VM.Notify?.Show("IBRA DRIVE CHECK", Loc.T("monitor.trayHint"));
                VM.Settings.TrayHintShown = true;
                VM.SaveSettings();
            }
            return;
        }
        if (VM.IsBusy)
        {
            if (sender is not Window) ShowFromTray();
            if (!TestExecution.Ask(Loc.T("scan.running") + "\n\n" + Loc.T("scan.cancel") + "?")) { ReallyExit = false; return; }
            // نلغي بأمان وننتظر إعادة البيانات الأصلية وحذف الملفات المؤقتة قبل الإغلاق
            foreach (var c in MainViewModel.I.CancelCommands) if (c.CanExecute(null)) c.Execute(null);
            IsEnabled = false;
            while (VM.IsBusy) await Task.Delay(200);
        }
        ReallyExit = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ReallyExit || VM.IsBusy && IsEnabled)
        {
            e.Cancel = true;
            OnClose(this, new RoutedEventArgs());
            return;
        }
        base.OnClosing(e);
    }

    private void OnRestartAdmin(object sender, RoutedEventArgs e)
    {
        if (VM.IsBusy) return;
        var page = (PageHost.Content as SectionView) != null ? (NavList.SelectedItem as SectionDef)?.Key ?? "dashboard" : "dashboard";
        var disk = VM.SelectedDrive?.Drive.DiskNumber;
        Shell.RestartElevated($"--page {page}" + (disk is int d ? $" --disk {d}" : ""));
    }
}

/// <summary>صفحة تحتاج تحديث بياناتها عند فتحها.</summary>
public interface IPageActivated
{
    void OnActivated();
}
