using System.Windows;
using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.App.Views;

public partial class FakeDetectorView : UserControl
{
    public FakeDetectorView()
    {
        InitializeComponent();
        DataContext = PurchaseViewModel.I;
    }

    private void Go(ScanKind kind)
    {
        var vm = ScannerViewModel.I;
        vm.SelectedKind = vm.Kinds.FirstOrDefault(k => k.Kind == kind) ?? vm.SelectedKind;
        MainViewModel.I.Navigate("scanner");
        _ = vm.StartAsync(kind);
    }

    private void OnQuick(object sender, RoutedEventArgs e) => Go(ScanKind.Quick);
    private void OnDeep(object sender, RoutedEventArgs e) => Go(ScanKind.Deep);
    private void OnFull(object sender, RoutedEventArgs e) => Go(ScanKind.FullFreeSpace);
}
