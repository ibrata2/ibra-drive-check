using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class CloneView : UserControl
{
    public CloneView()
    {
        InitializeComponent();
        DataContext = CloneViewModel.I;
    }
}
