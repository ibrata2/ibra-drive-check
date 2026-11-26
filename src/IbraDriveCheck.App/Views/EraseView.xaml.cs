using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class EraseView : UserControl
{
    public EraseView()
    {
        InitializeComponent();
        DataContext = EraseViewModel.I;
    }
}
