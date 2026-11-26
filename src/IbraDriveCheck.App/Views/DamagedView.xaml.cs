using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class DamagedView : UserControl, IPageActivated
{
    public DamagedView()
    {
        InitializeComponent();
        DataContext = DamagedViewModel.I;
    }

    public void OnActivated() => DamagedViewModel.I.Load();
}
