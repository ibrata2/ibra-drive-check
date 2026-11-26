using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class CompareView : UserControl, IPageActivated
{
    public CompareView()
    {
        InitializeComponent();
        DataContext = CompareViewModel.I;
    }

    public void OnActivated() => CompareViewModel.I.Refresh();
}
