using System.Collections.ObjectModel;
using System.Windows.Threading;
using Microsoft.Win32;
using IbraDriveCheck.App.ViewModels;
using IbraDriveCheck.Core.Devices.Windows;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.App.Infrastructure;

public sealed class MonitorEntry : ObservableObject
{
    public string Key { get; init; } = "";
    public string Name { get; set; } = "";
    private string _temp = "—", _status = "—", _time = "";
    public string Temp { get => _temp; set => Set(ref _temp, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Time { get => _time; set => Set(ref _time, value); }
    public double? LastTemp;
    public long? LastMediaErrors;
    public string? LastHealth;
    public DateTime LastHotAlert = DateTime.MinValue;
}

/// <summary>
/// المراقبة بالخلفية: يقرأ الحرارة والصحة لكل الوحدات كل عدة دقائق، ويعرض أعلى حرارة على أيقونة الساعة،
/// وينبه عند ارتفاع الحرارة أو ظهور أخطاء جديدة أو تغير حالة الصحة.
/// </summary>
public sealed class MonitorService
{
    public static MonitorService I { get; } = new();
    public ObservableCollection<MonitorEntry> Entries { get; } = new();
    private readonly DispatcherTimer _timer = new();
    private bool _polling;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "IBRA DRIVE CHECK";

    private MonitorService() => _timer.Tick += async (_, _) => await PollAsync();

    public void Apply()
    {
        var s = MainViewModel.I.Settings;
        _timer.Interval = TimeSpan.FromMinutes(Math.Clamp(s.MonitorMinutes, 1, 120));
        if (s.MonitorEnabled) { _timer.Start(); _ = PollAsync(); }
        else _timer.Stop();
        SetStartup(s.StartWithWindows);
    }

    public void Stop() => _timer.Stop();

    public static bool IsStartupRegistered()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunName) != null; } catch { return false; }
    }

    private static void SetStartup(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
            else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
        }
        catch { }
    }

    public async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var drives = MainViewModel.I.Drives.Select(d => d.Drive).ToList();
            var results = await Task.Run(() => drives.Select(d => (d, h: SafeRead(d))).ToList());
            var s = MainViewModel.I.Settings;
            double? hottest = null;
            foreach (var (d, h) in results)
            {
                var e = Entries.FirstOrDefault(x => x.Key == d.HardwareKey);
                if (e == null) { e = new MonitorEntry { Key = d.HardwareKey }; Entries.Add(e); }
                e.Name = $"{d.DisplayName} {d.Letters}".Trim();
                e.Time = DateTime.Now.ToString("HH:mm");
                if (h == null) { e.Status = Loc.T("common.notAvailable"); continue; }
                e.Temp = h.TemperatureC is double t ? $"{t:0} °C" : "—";
                e.Status = h.HealthStatus != null ? Loc.T("health." + h.HealthStatus) : h.SmartSupported ? "OK" : Loc.T("common.notAvailable");
                if (h.TemperatureC is double tc) hottest = Math.Max(hottest ?? 0, tc);

                // التنبيهات
                var notify = MainViewModel.I.Notify;
                if (h.TemperatureC is double temp && temp >= s.TempLimit && s.NotifyHot && DateTime.Now - e.LastHotAlert > TimeSpan.FromMinutes(30))
                {
                    notify?.Show("IBRA DRIVE CHECK", Loc.F("notify.hot", d.DisplayName, temp.ToString("0")), true);
                    e.LastHotAlert = DateTime.Now;
                }
                if (s.NotifyErrors && (h.PredictFailure == true && e.LastHealth != "predict"
                    || h.MediaErrors is long me && e.LastMediaErrors is long old && me > old
                    || h.HealthStatus is "Warning" or "Unhealthy" && e.LastHealth != h.HealthStatus))
                    notify?.Show("IBRA DRIVE CHECK", Loc.F("monitor.alert", d.DisplayName), true);
                e.LastTemp = h.TemperatureC;
                e.LastMediaErrors = h.MediaErrors;
                e.LastHealth = h.PredictFailure == true ? "predict" : h.HealthStatus;
            }
            foreach (var gone in Entries.Where(x => results.All(r => r.d.HardwareKey != x.Key)).ToList()) Entries.Remove(gone);
            MainViewModel.I.Notify?.SetTooltip("IBRA DRIVE CHECK" + (hottest is double ht ? $" — {ht:0}°C" : ""));
        }
        finally { _polling = false; }
    }

    private static HealthInfo? SafeRead(DriveSnapshot d)
    {
        try { return HealthReader.Read(d); } catch { return null; }
    }
}
