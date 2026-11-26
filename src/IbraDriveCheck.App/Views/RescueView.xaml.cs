using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class RescueView : UserControl, IPageActivated
{
    public RescueView()
    {
        InitializeComponent();
        DataContext = RescueViewModel.I;
    }

    public void OnActivated() => RescueViewModel.I.Load();
}
