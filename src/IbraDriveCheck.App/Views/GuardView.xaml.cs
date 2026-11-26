using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class GuardView : UserControl, IPageActivated
{
    public GuardView()
    {
        InitializeComponent();
        DataContext = GuardViewModel.I;
    }

    public void OnActivated() => GuardViewModel.I.Load();
}
