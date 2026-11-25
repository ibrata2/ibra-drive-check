using System.Windows;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.App.Views;
using IbraDriveCheck.Core.Devices.Windows;
using IbraDriveCheck.Core.Engine;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Safety;

namespace IbraDriveCheck.App.ViewModels;

/// <summary>تشغيل أي فحص في الخلفية مع تحويل الأخطاء إلى رسائل مفهومة.</summary>
public static class TestExecution
{
    public static async Task<ScanResult?> RunAsync(ScanRequest req, ScanSession session, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => ScanRunner.Run(req, session, ct), CancellationToken.None);
        }
        catch (SafetyException ex)
        {
            Msg(Loc.T(ex.ReasonKey), true);
        }
        catch (UnauthorizedAccessException)
        {
            AskElevation(req.Kind, req.Drive);
        }
        catch (IdentityChangedException)
        {
            Msg(Loc.T("scan.identityChanged"), true);
        }
        catch (VolumeInUseException ex)
        {
            Msg(Loc.F("scan.volumeInUse", ex.Volume), true);
        }
        catch (IbraDriveCheck.Core.Devices.DeviceGoneException)
        {
            Msg(Loc.T("note.disconnected"), true);
        }
        catch (Exception ex)
        {
            Msg(Loc.F("scan.failedStart", ex.Message), true);
        }
        return null;
    }

    public static void Msg(string text, bool warning = false)
    {
        var opts = Loc.I.IsArabic ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
        MessageBox.Show(Application.Current.MainWindow, text, "IBRA DRIVE CHECK", MessageBoxButton.OK,
            warning ? MessageBoxImage.Warning : MessageBoxImage.Information, MessageBoxResult.OK, opts);
    }

    public static bool Ask(string text)
    {
        var opts = Loc.I.IsArabic ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
        return MessageBox.Show(Application.Current.MainWindow, text, "IBRA DRIVE CHECK", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No, opts) == MessageBoxResult.Yes;
    }

    public static void AskElevation(ScanKind kind, DriveSnapshot d)
    {
        if (Ask(Loc.T("common.adminRequired") + "\n\n" + Loc.T("common.restartAdmin") + "?"))
            Shell.RestartElevated($"--page scanner --kind {kind} --disk {d.DiskNumber}");
    }

    /// <summary>كل التحققات والتأكيدات قبل الفحص. يرجع false إذا لا يجب البدء.</summary>
    public static bool Confirm(ScanKind kind, DriveSnapshot d)
    {
        var decision = SafetyPolicy.Check(d, kind);
        if (!decision.Allowed)
        {
            Msg(Loc.T(decision.ReasonKey), true);
            return false;
        }
        if (SafetyPolicy.NeedsRawAccess(kind) && !MainViewModel.I.IsElevated)
        {
            AskElevation(kind, d);
            return false;
        }
        string? typed = null;
        string message;
        switch (kind)
        {
            case ScanKind.Quick:
            case ScanKind.Deep:
                message = Loc.T("scan.confirmWrite");
                break;
            case ScanKind.FullFreeSpace:
                message = Loc.T("scan.confirmFree");
                break;
            case ScanKind.FullDestructive:
                message = Loc.T("scan.confirmDestructive");
                typed = Fmt.Bytes(d.DeclaredBytes);
                break;
            default:
                return true;   // القراءة فقط / السرعة / الصحة لا تحتاج تأكيد إضافي
        }
        var dlg = new ConfirmDialog($"{d.DisplayName}  {d.Letters}  —  {Fmt.Bytes(d.DeclaredBytes)}", Strings.Kind(Loc.I.Lang, kind), message, typed, danger: kind == ScanKind.FullDestructive)
        {
            Owner = Application.Current.MainWindow,
        };
        return dlg.ShowDialog() == true;
    }
}
