using System.Windows;
using System.Windows.Threading;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.App.ViewModels;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.App;

public partial class App : Application
{
    public static string? StartPage { get; private set; }
    public static ScanKind? StartKind { get; private set; }
    public static int? StartDisk { get; private set; }
    /// <summary>--smoke-test: يفتح كل الصفحات ويبدل اللغة والمظهر ثم يخرج (يستخدم في البناء الآلي).</summary>
    public static bool Smoke { get; private set; }
    public static string SmokeLog => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ibra-drive-check-smoke.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        Smoke = e.Args.Contains("--smoke-test");
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (Smoke) { System.IO.File.AppendAllText(SmokeLog, ex.ExceptionObject + Environment.NewLine); Environment.Exit(1); }
        };

        // وسائط إعادة التشغيل كمسؤول: --page scanner --kind Quick --disk 3
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            switch (e.Args[i])
            {
                case "--page": StartPage = e.Args[i + 1]; break;
                case "--kind": if (Enum.TryParse<ScanKind>(e.Args[i + 1], out var k)) StartKind = k; break;
                case "--disk": if (int.TryParse(e.Args[i + 1], out var d)) StartDisk = d; break;
            }
        }

        var s = MainViewModel.I.Settings;
        Loc.I.Lang = s.Language == "en" ? "en" : "ar";
        ThemeService.Apply(s.Theme);

        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // لا نسمح لخطأ في الواجهة بإغلاق البرنامج أثناء فحص
        e.Handled = true;
        if (Smoke)
        {
            System.IO.File.AppendAllText(SmokeLog, e.Exception + Environment.NewLine);
            Environment.Exit(1);
        }
        try { TestExecution.Msg(Loc.F("scan.failedStart", e.Exception.Message), true); } catch { }
    }
}
