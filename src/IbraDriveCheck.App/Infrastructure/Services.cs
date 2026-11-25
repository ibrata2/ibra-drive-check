using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace IbraDriveCheck.App.Infrastructure;

public static class ThemeService
{
    public static string Current { get; private set; } = "light";

    /// <summary>light / dark / system</summary>
    public static void Apply(string setting)
    {
        string effective = setting == "system" ? (SystemIsDark() ? "dark" : "light") : setting;
        Current = effective;
        var dicts = Application.Current.Resources.MergedDictionaries;
        var uri = new Uri($"pack://application:,,,/IbraDriveCheck;component/Themes/{(effective == "dark" ? "Dark" : "Light")}.xaml", UriKind.Absolute);
        var theme = new ResourceDictionary { Source = uri };
        if (dicts.Count > 0) dicts[0] = theme; else dicts.Add(theme);
        foreach (Window w in Application.Current.Windows) Dwm.ApplyDarkTitle(w, effective == "dark");
    }

    public static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}

public static class Dwm
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyDarkTitle(Window w, bool dark)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int v = dark ? 1 : 0;
            DwmSetWindowAttribute(h, 20, ref v, 4);           // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch { }
    }

    /// <summary>زوايا مستديرة على ويندوز 11 (يتجاهلها ويندوز 10 بأمان).</summary>
    public static void RoundCorners(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            int pref = 2;                                     // DWMWCP_ROUND
            DwmSetWindowAttribute(h, 33, ref pref, 4);
        }
        catch { }
    }
}

public static class Shell
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    /// <summary>إعادة تشغيل البرنامج بصلاحيات المسؤول (يظهر طلب UAC من ويندوز).</summary>
    public static bool RestartElevated(string args)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
            return true;
        }
        catch (Win32Exception)
        {
            return false; // المستخدم رفض طلب الصلاحيات
        }
    }
}

/// <summary>إشعارات ويندوز عبر أيقونة منطقة الإشعارات (بدون أي مكتبة خارجية).</summary>
public sealed class NotifyService : IDisposable
{
    private System.Windows.Forms.NotifyIcon? _icon;

    public NotifyService()
    {
        try
        {
            _icon = new System.Windows.Forms.NotifyIcon
            {
                Text = "IBRA DRIVE CHECK",
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
                Visible = true,
            };
            _icon.DoubleClick += (_, _) =>
            {
                var w = Application.Current.MainWindow;
                if (w == null) return;
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
            };
        }
        catch { _icon = null; }
    }

    public void Show(string title, string text, bool warning = false)
    {
        try
        {
            _icon?.ShowBalloonTip(6000, title, text, warning ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_icon != null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
