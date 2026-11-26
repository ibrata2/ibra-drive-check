using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class LifeView : UserControl, IPageActivated
{
    public LifeView()
    {
        InitializeComponent();
        DataContext = LifeViewModel.I;
    }

    public void OnActivated() => LifeViewModel.I.ReadIfNeeded();
}
