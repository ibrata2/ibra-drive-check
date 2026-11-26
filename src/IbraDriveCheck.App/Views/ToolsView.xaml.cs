using System.Windows.Controls;
using IbraDriveCheck.App.ViewModels;

namespace IbraDriveCheck.App.Views;

public partial class ToolsView : UserControl, IPageActivated
{
    public ToolsView()
    {
        InitializeComponent();
        DataContext = ToolsViewModel.I;
    }

    public void OnActivated()
    {
        if (ToolsViewModel.I.IsIdle) ToolsViewModel.I.Refresh();
    }
}
