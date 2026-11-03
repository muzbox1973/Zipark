using System.Windows;
using System.Windows.Threading;
using Zipark.Cache;
using Zipark.Core;
using Zipark.Desktop.Infrastructure;
using Zipark.Desktop.ViewModels;
using Zipark.Settings;
using Zipark.ShellIntegration;

namespace Zipark.Desktop;

public partial class App : System.Windows.Application
{
    private const string InstanceId = "Desktop";
    private SingleInstance? _instance;
    private ShellCommandHandler? _shell;
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Initialize("desktop");
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("fatal", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log.Error("unobserved task", ev.Exception);
            ev.SetObserved();
        };

        // Installer / command-line maintenance switches.
        if (e.Args.Contains("--register-shell") || e.Args.Contains("--unregister-shell"))
        {
            base.OnStartup(e);
            var settingsForShell = new SettingsStore().Load();
            Loc.Apply(settingsForShell.Language);
            var reg = new ShellRegistrar();
            if (e.Args.Contains("--register-shell")) reg.Register(Environment.ProcessPath!, ShellLabelsFactory.Create());
            else reg.Unregister();
            Shutdown(0);
            return;
        }

        var snapshotIdx = Array.IndexOf(e.Args, "--screenshot-pages");
        if (snapshotIdx >= 0 && snapshotIdx + 1 < e.Args.Length)
        {
            base.OnStartup(e);
            var snapSettings = new SettingsStore().Load();
            string? Arg(string name) { var i = Array.IndexOf(e.Args, name); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }
            if (Arg("--lang") is { } lang) snapSettings.Language = lang;
            if (Arg("--theme") is { } theme && Enum.TryParse<ThemeMode>(theme, true, out var tm)) snapSettings.Theme = tm;
            snapSettings.SimpleMode = !e.Args.Contains("--expert");
            // Worst case for the home page: a full recent list with long paths.
            snapSettings.RecentArchives = Enumerable.Range(1, 15)
                .Select(i => $@"C:\Users\someone\Documents\프로젝트 자료 모음\2026년 분기별 보고서\archive-{i:D2}-very-long-file-name.7z").ToList();
            AppServices.Initialize(new SettingsStore(Path.Combine(Path.GetTempPath(), "Zipark-snap-settings.json")), snapSettings);
            ThemeManager.Apply(snapSettings.Theme);
            Loc.Apply(snapSettings.Language);
            UiState.Instance.Expert = !snapSettings.SimpleMode;
            var snapWindow = new MainWindow(new MainViewModel());
            MainWindow = snapWindow;
            snapWindow.Show();
            snapWindow.Dispatcher.InvokeAsync(async () =>
            {
                await PageSnapshots.RunAsync(snapWindow, e.Args[snapshotIdx + 1], Arg("--sample"));
                Shutdown(0);
            });
            return;
        }

        _instance = new SingleInstance(InstanceId);
        if (!_instance.IsPrimary && e.Args.Length > 0 && SingleInstance.Forward(InstanceId, e.Args))
        {
            Shutdown(0);
            return;
        }
        if (!_instance.IsPrimary && e.Args.Length == 0 && SingleInstance.Forward(InstanceId, ["--activate"]))
        {
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        var store = new SettingsStore();
        var settings = store.Load();
        AppServices.Initialize(store, settings);
        ThemeManager.Apply(settings.Theme);
        ThemeManager.Watch();
        Loc.Apply(settings.Language);
        UiState.Instance.Expert = !settings.SimpleMode;
        _ = Task.Run(() => HybridChunkCache.CleanupStaleSessions());

        var vm = new MainViewModel();
        var window = new MainWindow(vm);
        MainWindow = window;
        _shell = new ShellCommandHandler(vm);

        // Context-menu work (compress, extract, test, mount, unmount) runs without the main window:
        // progress lives in the tray icon and results arrive as Windows notifications.
        bool background = IsBackgroundRequest(e.Args);
        _tray = new TrayController(background, () => _shell.Pending);
        _tray.OpenRequested += () => ShowMainWindow(window);
        _tray.ExitRequested += () => Shutdown(0);
        if (background) ShutdownMode = ShutdownMode.OnExplicitShutdown;
        else window.Show();

        _instance.ArgumentsReceived += args => Dispatcher.BeginInvoke(() =>
        {
            if (!IsBackgroundRequest(args)) ShowMainWindow(window);
            if (!args.Contains("--activate")) _shell.Submit(args);
        });
        if (e.Args.Length > 0) _shell.Submit(e.Args);
        Log.Info($"started; background={background} engine={AppServices.Current.EnginePath ?? "missing"} worker={AppServices.Current.UsingWorkerProcess}");
    }

    private static bool IsBackgroundRequest(string[] args) =>
        ShellInvocation.Parse(args) is { } inv && ShellVerbs.RunsInBackground(inv.Verb);

    /// <summary>Shows the main window (first time: leaves background mode, closing it then exits the app).</summary>
    private void ShowMainWindow(MainWindow window)
    {
        if (!window.IsVisible)
        {
            window.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            _tray?.LeaveBackground();
        }
        window.BringToFront();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("ui thread exception", e.Exception);
        try
        {
            AppServices.Current.Notify.Error(e.Exception);
            e.Handled = true; // keep the application alive; the error is shown in the notification bar
        }
        catch
        {
            // services not ready: let it crash and be logged
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            var s = AppServices.Current;
            s.Jobs.CancelAll();
            s.Drives.Dispose(); // ZIP/7Z drives are unmounted; ISO drives stay attached like Windows' own mount
        }
        catch
        {
            // services were never created (secondary instance)
        }
        _tray?.Dispose();
        ArchiveBrowserViewModel.CleanupPreviews();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
