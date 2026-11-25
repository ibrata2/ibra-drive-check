using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using IbraDriveCheck.App.Infrastructure;

namespace IbraDriveCheck.App.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        void SetVersion() => VersionText.Text = $"{Loc.T("about.version")} {v?.Major}.{v?.Minor}.{v?.Build}";
        SetVersion();
        Loc.I.LanguageChanged += SetVersion;
    }

    private void OnSite(object sender, RoutedEventArgs e) => Shell.Open("https://ibraprogram.netlify.app/");
    private void OnTelegram(object sender, RoutedEventArgs e) => Shell.Open("https://t.me/ibraprogram");
    private void OnTikTok(object sender, RoutedEventArgs e) => Shell.Open("https://www.tiktok.com/@ibraprogram");
}
