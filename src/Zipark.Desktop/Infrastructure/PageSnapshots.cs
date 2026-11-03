using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zipark.Desktop.ViewModels;

namespace Zipark.Desktop.Infrastructure;

/// <summary>
/// Diagnostic: <c>Zipark.exe --screenshot-pages &lt;dir&gt; [--theme light|dark] [--lang ko|en] [--expert] [--sample archive]</c>
/// renders the real main window once per page (plus worst-case states and every settings tab) to PNG, and
/// writes <c>layout-report.txt</c>: for each capture, whether the page content needs more height than the
/// fixed window gives it (overflow) and whether any scrollbar is showing. Then exits.
/// </summary>
public static class PageSnapshots
{
    public static async Task RunAsync(MainWindow window, string dir, string? sampleArchive = null)
    {
        Directory.CreateDirectory(dir);
        var report = new StringBuilder();
        var vm = window.ViewModel;
        if (sampleArchive is not null)
        {
            await vm.Explorer.LoadAsync(sampleArchive);
            if (vm.Explorer.Items.FirstOrDefault(i => i.IsDirectory) is { } folder) vm.Explorer.Folder = folder;
        }

        async Task Capture(string name)
        {
            await Settle();
            Save(window, Path.Combine(dir, name + ".png"));
            report.AppendLine($"{name,-22} {Check(window, vm.CurrentPage)}");
        }

        foreach (var key in vm.Items.Select(i => i.Key))
        {
            vm.Navigate(key, null);
            await Capture(key);
        }

        // Worst case: every option of "new archive" opened at once.
        vm.Navigate("compress", null);
        vm.Compress.OnNavigatedTo(Enumerable.Range(1, 12).Select(i => $@"C:\Data\Folder {i:D2} with a fairly long name").ToArray());
        vm.Compress.Split = true;
        vm.Compress.SplitPreset = 0;
        vm.Compress.Encrypt = true;
        vm.Compress.UpdateWarnings();
        await Capture("compress-max");
        vm.Compress.Format = Core.ArchiveFormat.SevenZip;
        vm.Compress.EncryptNames = false;
        vm.Compress.UpdateWarnings();
        await Capture("compress-max-7z");

        // Every settings tab.
        vm.Navigate("settings", null);
        await Settle();
        if (FindDescendant<TabControl>(window.Content as DependencyObject) is { } tabs)
        {
            for (int i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                await Capture($"settings-tab{i + 1}");
            }
        }

        // A notification bar sample on the home page.
        vm.Navigate("home", null);
        AppServices.Current.Notify.Success(Loc.T("s.kind.Compress") + " · " + Loc.T("s.stage.Completed"), "documents.zip", Loc.T("s.action.openFolder"), () => { });
        await Capture("notice");

        File.WriteAllText(Path.Combine(dir, "layout-report.txt"), report.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Overflow = the page's natural height exceeds the space the fixed window gives it.</summary>
    private static string Check(Window window, object page)
    {
        var view = FindDescendant<UserControl>(window.Content as DependencyObject, u => ReferenceEquals(u.DataContext, page) && u is not Views.SecretInput);
        if (view?.Content is not FrameworkElement root) return "?";
        var available = view.ActualHeight;
        root.Measure(new Size(view.ActualWidth, double.PositiveInfinity));
        var natural = root.DesiredSize.Height;
        root.InvalidateMeasure();
        view.UpdateLayout();

        var bars = new List<string>();
        foreach (var sv in FindAll<ScrollViewer>(view))
        {
            if (sv.ComputedVerticalScrollBarVisibility == Visibility.Visible) bars.Add("V:" + (sv.TemplatedParent?.GetType().Name ?? "page"));
            if (sv.ComputedHorizontalScrollBarVisibility == Visibility.Visible) bars.Add("H:" + (sv.TemplatedParent?.GetType().Name ?? "page"));
        }
        var fit = natural <= available + 0.5 ? "OK  " : "OVERFLOW";
        var origin = view.TranslatePoint(new Point(0, 0), (UIElement)window.Content);
        var widthFit = origin.X + view.ActualWidth <= ((FrameworkElement)window.Content).ActualWidth + 0.5 ? "" : " WIDTH-OVERFLOW";
        return $"{fit} natural={natural:F0} available={available:F0} x={origin.X:F0} w={view.ActualWidth:F0} root={root.ActualWidth:F0}{widthFit} scrollbars=[{string.Join(",", bars)}]";
    }

    private static T? FindDescendant<T>(DependencyObject? root, Func<T, bool>? match = null) where T : DependencyObject =>
        FindAll<T>(root).FirstOrDefault(t => match?.Invoke(t) ?? true);

    private static IEnumerable<T> FindAll<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) yield break;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (d is T t) yield return t;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
        }
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
            // Map the visual 1:1 (a default VisualBrush stretches the content's drawn bounds and distorts margins).
            var area = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            var brush = new VisualBrush(content)
            {
                Viewbox = area,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = area,
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            ctx.DrawRectangle(brush, null, area);
        }
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
