using System.Windows;
using System.Windows.Controls;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.App.ViewModels;
using IbraDriveCheck.Core.Storage;

namespace IbraDriveCheck.App.Views;

public partial class SettingsView : UserControl
{
    private bool _loading = true;
    private AppSettings S => MainViewModel.I.Settings;

    public SettingsView()
    {
        InitializeComponent();
        LangAr.IsChecked = S.Language != "en";
        LangEn.IsChecked = S.Language == "en";
        ThemeLight.IsChecked = S.Theme == "light";
        ThemeDark.IsChecked = S.Theme == "dark";
        ThemeSystem.IsChecked = S.Theme == "system";
        NNew.IsChecked = S.NotifyNewDrive;
        NDone.IsChecked = S.NotifyScanDone;
        NFake.IsChecked = S.NotifyFake;
        NErrors.IsChecked = S.NotifyErrors;
        NHot.IsChecked = S.NotifyHot;
        NDisconnect.IsChecked = S.NotifyDisconnect;
        _loading = false;
    }

    private void OnLang(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Language = LangEn.IsChecked == true ? "en" : "ar";
        Loc.I.Lang = S.Language;
        MainViewModel.I.SaveSettings();
    }

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Theme = ThemeDark.IsChecked == true ? "dark" : ThemeSystem.IsChecked == true ? "system" : "light";
        ThemeService.Apply(S.Theme);
        MainViewModel.I.SaveSettings();
    }

    private void OnNotify(object sender, RoutedEventArgs e)
    {
        S.NotifyNewDrive = NNew.IsChecked == true;
        S.NotifyScanDone = NDone.IsChecked == true;
        S.NotifyFake = NFake.IsChecked == true;
        S.NotifyErrors = NErrors.IsChecked == true;
        S.NotifyHot = NHot.IsChecked == true;
        S.NotifyDisconnect = NDisconnect.IsChecked == true;
        MainViewModel.I.SaveSettings();
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(AppPaths.Root);
        Shell.Open(AppPaths.Root);
    }

    private void OnDeleteAll(object sender, RoutedEventArgs e)
    {
        if (TestExecution.Ask(Loc.T("hist.confirmDeleteAll"))) MainViewModel.I.DeleteAllResults();
    }
}
