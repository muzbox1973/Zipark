using System.Windows;
using Zipark.Desktop.Infrastructure;
using Zipark.Desktop.ViewModels;

namespace Zipark.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) =>
        {
            ThemeManager.ApplyTitleBar(this);
            FitToWorkArea();
        };
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) vm.HandleDrop(files);
            e.Handled = true;
        };
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>
    /// The window has a fixed design size. If the screen's work area is smaller (e.g. 1366x768 or high
    /// display scaling), scale the whole layout down uniformly so nothing needs a scrollbar.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        const double chromeW = 16, chromeH = 40; // borders + title bar, generous for Windows 10 and 11
        var scale = Math.Min(1.0, Math.Min((area.Width - chromeW) / Root.Width, (area.Height - chromeH) / Root.Height));
        if (scale < 1.0) Root.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
