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
    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly DispatcherTimer _deviceDebounce;
    private MainViewModel VM => MainViewModel.I;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = VM;
        NavList.ItemsSource = VM.Nav;
        if (VM.IsElevated)
        {
            AdminBadge.Visibility = Visibility.Visible;
            AdminButton.Visibility = Visibility.Collapsed;
        }

        _deviceDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _deviceDebounce.Tick += async (_, _) =>
        {
            _deviceDebounce.Stop();
            await VM.RefreshDrivesAsync(notifyNew: true);
        };

        VM.NavigateRequested += page =>
        {
            var item = VM.Nav.FirstOrDefault(n => n.Key == page);
            if (item != null) NavList.SelectedItem = item;
        };

        StateChanged += (_, _) =>
        {
            // مع WindowChrome النافذة المكبرة تتجاوز حدود الشاشة قليلاً
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };

        Loaded += OnLoaded;
        Closed += (_, _) => VM.Notify?.Dispose();
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
        VM.Notify = new NotifyService();
        NavList.SelectedItem = VM.Nav.FirstOrDefault(n => n.Key == (App.StartPage ?? "dashboard")) ?? VM.Nav[0];
        await VM.RefreshDrivesAsync(notifyNew: false);

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

    /// <summary>اختبار تشغيل آلي: كل الصفحات باللغتين والمظهرين، ثم خروج برمز 0.</summary>
    private async Task RunSmokeTest()
    {
        foreach (var lang in new[] { "ar", "en" })
        {
            Loc.I.Lang = lang;
            foreach (var theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme);
                foreach (var n in VM.Nav)
                {
                    NavList.SelectedItem = n;
                    await Task.Delay(250);
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
        if (NavList.SelectedItem is not NavItem item) return;
        if (!_pages.TryGetValue(item.Key, out var page))
        {
            page = item.Key switch
            {
                "dashboard" => new DashboardView(),
                "scanner" => new ScannerView(),
                "fake" => new FakeDetectorView(),
                "speed" => new SpeedView(),
                "health" => new HealthView(),
                "history" => new HistoryView(),
                "reports" => new ReportsView(),
                "settings" => new SettingsView(),
                _ => new AboutView(),
            };
            _pages[item.Key] = page;
        }
        PageHost.Content = page;
        if (page is IPageActivated act) act.OnActivated();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private async void OnClose(object sender, RoutedEventArgs e)
    {
        if (VM.IsBusy)
        {
            if (!TestExecution.Ask(Loc.T("scan.running") + "\n\n" + Loc.T("scan.cancel") + "?")) return;
            // نلغي بأمان وننتظر إعادة البيانات الأصلية وحذف الملفات المؤقتة قبل الإغلاق
            if (ScannerViewModel.I.CancelCommand.CanExecute(null)) ScannerViewModel.I.CancelCommand.Execute(null);
            if (SpeedViewModel.I.CancelCommand.CanExecute(null)) SpeedViewModel.I.CancelCommand.Execute(null);
            IsEnabled = false;
            while (VM.IsBusy) await Task.Delay(200);
        }
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 أثناء فحص: نفس المسار الآمن
        if (VM.IsBusy && IsEnabled)
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
        var page = (NavList.SelectedItem as NavItem)?.Key ?? "dashboard";
        var disk = VM.SelectedDrive?.Drive.DiskNumber;
        Shell.RestartElevated($"--page {page}" + (disk is int d ? $" --disk {d}" : ""));
    }
}

/// <summary>صفحة تحتاج تحديث بياناتها عند فتحها.</summary>
public interface IPageActivated
{
    void OnActivated();
}
