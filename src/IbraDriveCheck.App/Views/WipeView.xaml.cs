using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class WipeView : UserControl, IPageActivated
{
    public WipeView()
    {
        InitializeComponent();
        DataContext = WipeViewModel.I;
    }

    public void OnActivated()
    {
        if (WipeViewModel.I.IsIdle) WipeViewModel.I.LoadVolumes();
    }
}
