using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class WriteLockView : UserControl, IPageActivated
{
    public WriteLockView()
    {
        InitializeComponent();
        DataContext = WriteLockViewModel.I;
    }

    public void OnActivated() => WriteLockViewModel.I.Load();
}
