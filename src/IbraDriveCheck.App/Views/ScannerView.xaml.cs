using System.Windows;
using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class ScannerView : UserControl, IPageActivated
{
    public ScannerView()
    {
        InitializeComponent();
        DataContext = ScannerViewModel.I;
    }

    public void OnActivated() => ScannerViewModel.I.OnDriveChanged();

    private async void OnRefresh(object sender, RoutedEventArgs e) => await MainViewModel.I.RefreshDrivesAsync(false);
}
