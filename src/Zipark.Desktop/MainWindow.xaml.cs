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
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
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

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
