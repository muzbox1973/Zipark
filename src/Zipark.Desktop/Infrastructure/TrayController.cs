using System.ComponentModel;
using System.Windows.Threading;
using Zipark.Core;
using Forms = System.Windows.Forms;

namespace Zipark.Desktop.Infrastructure;

/// <summary>
/// Notification-area presence for work started from the Explorer context menu.
/// <para>
/// In background mode the main window is never shown: the tray icon's tooltip shows progress, its menu can
/// open Zipark or cancel, and results arrive as Windows notifications (clicking one opens the result).
/// When nothing is left to do — no queued or running job, no pending context-menu request, no mounted
/// ZIP/7Z drive and no open dialog — the app exits by itself after a short grace period.
/// </para>
/// When the main window is open but not in front, results of context-menu work are also shown as
/// notifications so the window never has to pop up.
/// </summary>
public sealed class TrayController : IDisposable
{
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(6);

    private readonly Forms.NotifyIcon _icon;
    private readonly DispatcherTimer _timer;
    private readonly Func<int> _pendingShellWork;
    private Action? _balloonAction;
    private DateTime _idleSince = DateTime.UtcNow;

    public bool Background { get; private set; }
    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayController(bool background, Func<int> pendingShellWork)
    {
        Background = background;
        _pendingShellWork = pendingShellWork;
        var exe = Environment.ProcessPath;
        _icon = new Forms.NotifyIcon
        {
            Icon = exe is not null ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : System.Drawing.SystemIcons.Application,
            Text = AppInfo.ProductName,
            Visible = background,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) =>
        {
            var a = _balloonAction;
            _balloonAction = null;
            a?.Invoke();
        };
        Loc.Changed += () => _icon.ContextMenuStrip = BuildMenu();
        AppServices.Current.Notify.PropertyChanged += OnNotice;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>The user opened the main window: from now on it behaves like a normal session.</summary>
    public void LeaveBackground()
    {
        Background = false;
        _icon.Visible = false;
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("s.tray.open"), null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(Loc.T("s.jobs.cancelAll"), null, (_, _) => AppServices.Current.Jobs.CancelAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("s.tray.exit"), null, (_, _) => ExitRequested?.Invoke());
        return menu;
    }

    private void OnNotice(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Notifier.Current)) return;
        var n = AppServices.Current.Notify.Current;
        if (n is null || n.Kind == NoticeKind.Info) return;
        var main = System.Windows.Application.Current?.MainWindow;
        if (!Background && main is { IsVisible: true, IsActive: true }) return; // the in-window bar is enough
        _balloonAction = n.Action;
        _icon.Visible = true;
        var icon = n.Kind switch
        {
            NoticeKind.Error => Forms.ToolTipIcon.Error,
            NoticeKind.Warning => Forms.ToolTipIcon.Warning,
            _ => Forms.ToolTipIcon.Info,
        };
        var text = string.IsNullOrWhiteSpace(n.Detail) ? n.Title : n.Detail!;
        _icon.ShowBalloonTip(8000, n.Title, Trim(text, 250), icon);
        Log.Info($"tray notification shown: {n.Kind}");
    }

    private void Tick()
    {
        var s = AppServices.Current;
        var active = s.Jobs.Items.Where(j => j.IsActive).ToList();
        var archiveDrives = s.Drives.Drives.Count(d => d.Format != ArchiveFormat.Iso);
        string tip = AppInfo.ProductName;
        if (active.Count > 0) tip = Loc.F("s.tray.working", active.Count, active.Average(j => j.Percent));
        else if (archiveDrives > 0) tip = Loc.F("s.tray.drives", archiveDrives);
        _icon.Text = Trim(tip, 127);

        if (!Background) return;
        var dialogsOpen = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>()
            .Any(w => w.IsVisible && w != System.Windows.Application.Current.MainWindow);
        bool busy = active.Count > 0 || archiveDrives > 0 || _pendingShellWork() > 0 || dialogsOpen;
        if (busy) _idleSince = DateTime.UtcNow;
        else if (DateTime.UtcNow - _idleSince > IdleGrace)
        {
            Log.Info("background session finished; exiting");
            ExitRequested?.Invoke();
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        _timer.Stop();
        AppServices.Current.Notify.PropertyChanged -= OnNotice;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
