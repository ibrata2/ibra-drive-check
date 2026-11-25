using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class HealthView : UserControl, IPageActivated
{
    public HealthView()
    {
        InitializeComponent();
        DataContext = HealthViewModel.I;
    }

    public void OnActivated()
    {
        var vm = HealthViewModel.I;
        vm.Fill();
        if (vm.Main.SelectedDrive?.Health == null && vm.IsNotLoading) _ = vm.ReadAsync(save: false);
    }
}
