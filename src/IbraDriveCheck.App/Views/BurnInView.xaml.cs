using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class BurnInView : UserControl, IPageActivated
{
    public BurnInView()
    {
        InitializeComponent();
        DataContext = BurnInViewModel.I;
    }

    public void OnActivated() => BurnInViewModel.I.Load();
}
