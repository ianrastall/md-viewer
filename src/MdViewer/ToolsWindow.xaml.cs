using MdViewer.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MdViewer;

/// <summary>Shows Pandoc and Tesseract: what is installed, what is current, and the OCR preferences.</summary>
public sealed partial class ToolsWindow : Window
{
    public ToolsViewModel ViewModel { get; }
    private bool sized;

    public ToolsWindow(ToolsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = ((SolidColorBrush)Application.Current.Resources["TextBrush"]).Color;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = ((SolidColorBrush)Application.Current.Resources["LineBrush"]).Color;
        Closed += (_, _) => ViewModel.CancelCommand.Execute(null);
        RootGrid.Loaded += async (_, _) =>
        {
            if (sized) return;
            sized = true;
            var scale = RootGrid.XamlRoot.RasterizationScale;
            var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var width = Math.Min((int)(900 * scale), workArea.Width - 48);
            var height = Math.Min((int)(860 * scale), workArea.Height - 48);
            AppWindow.MoveAndResize(new(workArea.X + (workArea.Width - width) / 2, workArea.Y + (workArea.Height - height) / 2, width, height));
            await ViewModel.RefreshAsync(checkLatest: true);
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        };
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tool }) await ViewModel.InstallCommand.ExecuteAsync(tool);
    }
}
