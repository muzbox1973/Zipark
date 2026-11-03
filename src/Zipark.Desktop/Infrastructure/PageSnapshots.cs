using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zipark.Desktop.ViewModels;

namespace Zipark.Desktop.Infrastructure;

/// <summary>
/// Diagnostic: <c>Zipark.exe --screenshot-pages &lt;dir&gt; [--theme light|dark] [--lang ko|en]</c>
/// renders the real main window once per page to PNG (used for documentation and UI review), then exits.
/// </summary>
public static class PageSnapshots
{
    public static async Task RunAsync(MainWindow window, string dir, string? sampleArchive = null)
    {
        Directory.CreateDirectory(dir);
        var vm = window.ViewModel;
        if (sampleArchive is not null)
        {
            await vm.Explorer.LoadAsync(sampleArchive);
            if (vm.Explorer.Items.FirstOrDefault(i => i.IsDirectory) is { } folder) vm.Explorer.Folder = folder;
        }
        var keys = vm.Items.Select(i => i.Key).Append("settings").ToList();
        foreach (var key in keys)
        {
            vm.Navigate(key, null);
            await Settle();
            Save(window, Path.Combine(dir, $"{key}.png"));
            // Long pages: also capture the bottom.
            if (FindScrollViewer(window.Content as DependencyObject) is { ScrollableHeight: > 0 } sv)
            {
                sv.ScrollToEnd();
                await Settle();
                Save(window, Path.Combine(dir, $"{key}-end.png"));
                sv.ScrollToHome();
            }
        }
        // A notification bar sample on the home page.
        vm.Navigate("home", null);
        AppServices.Current.Notify.Success(Loc.T("s.kind.Compress") + " · " + Loc.T("s.stage.Completed"), "documents.7z", Loc.T("s.action.openFolder"), () => { });
        await Settle();
        Save(window, Path.Combine(dir, "notice.png"));
    }

    /// <summary>The page's own vertical ScrollViewer (skips the navigation list's).</summary>
    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        System.Windows.Controls.ScrollViewer? best = null;
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (d is System.Windows.Controls.ScrollViewer sv && sv.ActualWidth > 600 && sv.ScrollableHeight > 0
                && sv.TemplatedParent is not (System.Windows.Controls.ItemsControl or System.Windows.Controls.Primitives.TextBoxBase)
                && (best is null || sv.ActualHeight > best.ActualHeight))
                best = sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
        }
        return best;
    }

    private static async Task Settle()
    {
        for (int i = 0; i < 4; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);
        }
    }

    private static void Save(Window w, string file)
    {
        var dpi = VisualTreeHelper.GetDpi(w);
        var content = (FrameworkElement)w.Content;
        var bmp = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle((Brush)w.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            ctx.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
