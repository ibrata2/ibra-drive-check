using System.Windows;
using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        DataContext = MainViewModel.I;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await MainViewModel.I.RefreshDrivesAsync(false);

    private void OnQuick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key) MainViewModel.I.Navigate(key);
    }

    private void OnScan(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DriveItem d)
        {
            MainViewModel.I.SelectedDrive = d;
            MainViewModel.I.Navigate("scanner");
        }
    }
}
