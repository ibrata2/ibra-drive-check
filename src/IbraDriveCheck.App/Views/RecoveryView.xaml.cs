using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class RecoveryView : UserControl, IPageActivated
{
    public RecoveryView()
    {
        InitializeComponent();
        DataContext = RecoveryViewModel.I;
    }

    public void OnActivated()
    {
        if (RecoveryViewModel.I.IsIdle) RecoveryViewModel.I.LoadVolumes();
    }
}
