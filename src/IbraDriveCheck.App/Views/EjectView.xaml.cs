using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class EjectView : UserControl
{
    public EjectView()
    {
        InitializeComponent();
        DataContext = EjectViewModel.I;
    }
}
