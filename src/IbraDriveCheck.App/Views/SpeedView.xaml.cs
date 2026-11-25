using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class SpeedView : UserControl, IPageActivated
{
    public SpeedView()
    {
        InitializeComponent();
        DataContext = SpeedViewModel.I;
    }

    public void OnActivated()
    {
        if (!SpeedViewModel.I.IsRunning) SpeedViewModel.I.LoadVolumes();
    }
}
