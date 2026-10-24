using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class DownloadView : UserControl
{
    public DownloadView(DownloadViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Warm the featured catalogue in the background (best-effort; no-op if already loaded).
        _ = viewModel.LoadFeaturedAsync();
    }


}
