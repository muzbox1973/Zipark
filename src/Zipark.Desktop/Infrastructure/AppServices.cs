using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Zipark.Application.Drives;
using Zipark.Application.Jobs;
using Zipark.Compression;
using Zipark.Core;
using Zipark.Settings;

namespace Zipark.Desktop.Infrastructure;

public enum NoticeKind { Info, Success, Warning, Error }

public sealed class Notice : ObservableObject
{
    public required NoticeKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public string? ActionLabel { get; init; }
    public Action? Action { get; init; }
    public DateTime Time { get; } = DateTime.Now;
}

/// <summary>In-window notification bar (no OS toasts, nothing leaves the machine).</summary>
public sealed class Notifier : ObservableObject
{
    private Notice? _current;
    public Notice? Current { get => _current; private set => Set(ref _current, value); }
    public ObservableCollection<Notice> History { get; } = [];

    public void Show(NoticeKind kind, string title, string? detail = null, string? actionLabel = null, Action? action = null)
    {
        void Do()
        {
            var n = new Notice { Kind = kind, Title = title, Detail = detail, ActionLabel = actionLabel, Action = action };
            Current = n;
            History.Insert(0, n);
            while (History.Count > 50) History.RemoveAt(History.Count - 1);
        }
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Do(); else d.BeginInvoke(Do);
    }

    public void Info(string title, string? detail = null) => Show(NoticeKind.Info, title, detail);
    public void Success(string title, string? detail = null, string? actionLabel = null, Action? action = null) => Show(NoticeKind.Success, title, detail, actionLabel, action);
    public void Warn(string title, string? detail = null) => Show(NoticeKind.Warning, title, detail);

    public void Error(Exception ex)
    {
        if (ex is OperationCanceledException or ZiparkException { Code: ErrorCode.Cancelled }) return;
        Log.Error("ui error", ex);
        Show(NoticeKind.Error, Loc.T("s.notice.error"), Loc.Describe(ex));
    }

    public void Dismiss() => Current = null;
}

/// <summary>Composition root shared by all view models.</summary>
public sealed class AppServices
{
    private static AppServices? _current;
    public static AppServices Current => _current ?? throw new InvalidOperationException("Services not initialized.");

    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public Notifier Notify { get; } = new();
    public JobQueue Jobs { get; }
    public VirtualDriveService Drives { get; }
    public string? EnginePath { get; private set; }
    public string? EngineVersion { get; private set; }
    public bool UsingWorkerProcess { get; }
    private SevenZipEngine? _engine;

    public Action<string, object?>? Navigate { get; set; }

    private AppServices(SettingsStore store, AppSettings settings)
    {
        SettingsStore = store;
        Settings = settings;
        SevenZipLocator.OverridePath = string.IsNullOrWhiteSpace(settings.SevenZipPath) ? null : settings.SevenZipPath;
        RefreshEngine();
        UsingWorkerProcess = settings.UseWorkerProcess && WorkerProcessJobExecutor.IsAvailable();
        IJobExecutor exec = UsingWorkerProcess ? new WorkerProcessJobExecutor { ConflictPrompt = Dialogs.AskConflictAsync } : new LazyInProcessExecutor(this);
        Jobs = new JobQueue(exec, settings.MaxParallelJobs);
        Drives = new VirtualDriveService(DriveOptions());
    }

    public static AppServices Initialize(SettingsStore store, AppSettings settings) => _current = new AppServices(store, settings);

    public VirtualDriveOptions DriveOptions() =>
        new(Settings.MemoryCacheMb, Settings.DiskCacheMb, Settings.PreferredDriveLetter, null, SevenZipLocator.OverridePath);

    public void RefreshEngine()
    {
        EnginePath = SevenZipLocator.TryFind();
        EngineVersion = EnginePath is null ? null : FileVersionInfo.GetVersionInfo(EnginePath).ProductVersion;
        _engine = EnginePath is null ? null : new SevenZipEngine(EnginePath);
    }

    /// <summary>The archive engine, or a localized EngineNotFound error.</summary>
    public SevenZipEngine Engine => _engine ?? throw new ZiparkException(ErrorCode.EngineNotFound, "");

    public void SaveSettings()
    {
        try
        {
            SettingsStore.Save(Settings);
        }
        catch (Exception ex)
        {
            Notify.Error(ex);
        }
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("open explorer: " + ex.Message);
        }
    }

    /// <summary>Opens a web page in the user's browser. Only called from explicit user clicks.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("open url: " + ex.Message);
        }
    }

    /// <summary>Opens an offline document installed next to the app (manual, version history).</summary>
    public static void OpenDocument(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            Current.Notify.Warn(Loc.T("s.notice.docMissing"), path);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("open document: " + ex.Message);
        }
    }

    private sealed class LazyInProcessExecutor(AppServices s) : IJobExecutor
    {
        public Task<JobResult> ExecuteAsync(JobRequest request, Crypto.SecretPassword? password, IProgress<JobProgress>? progress, CancellationToken ct) =>
            new InProcessJobExecutor(s.Engine, Dialogs.AskConflictAsync).ExecuteAsync(request, password, progress, ct);
    }
}
