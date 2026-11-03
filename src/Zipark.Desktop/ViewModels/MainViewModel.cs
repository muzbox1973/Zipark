using System.Collections.ObjectModel;
using System.Windows.Input;
using Zipark.Core;
using Zipark.Desktop.Infrastructure;

namespace Zipark.Desktop.ViewModels;

public interface INavigable
{
    void OnNavigatedTo(object? parameter);
}

public sealed class NavItem : ObservableObject
{
    public NavItem(string key, string glyph, ObservableObject page)
    {
        Key = key;
        Glyph = glyph;
        Page = page;
        Loc.Changed += () => Raise(nameof(Title));
    }

    public string Key { get; }
    public string Glyph { get; }
    public string Title => Loc.T("s.nav." + Key);
    public ObservableObject Page { get; }
}

public sealed class MainViewModel : ObservableObject
{
    private NavItem _selected;
    private int _activeJobs;

    public MainViewModel()
    {
        var s = AppServices.Current;
        Home = new HomeViewModel();
        Explorer = new ExplorerViewModel();
        Compress = new CompressViewModel();
        Extract = new ExtractViewModel();
        Split = new SplitViewModel();
        Password = new PasswordViewModel();
        Drives = new DrivesViewModel();
        Iso = new IsoViewModel();
        Jobs = new JobsViewModel();
        Verify = new VerifyViewModel();
        Settings = new SettingsViewModel();

        Items =
        [
            new("home", "", Home),
            new("explorer", "", Explorer),
            new("compress", "", Compress),
            new("extract", "", Extract),
            new("split", "", Split),
            new("password", "", Password),
            new("drives", "", Drives),
            new("iso", "", Iso),
            new("jobs", "", Jobs),
            new("verify", "", Verify),
        ];
        SettingsItem = new NavItem("settings", "", Settings);
        _selected = Items[0];

        s.Navigate = Navigate;
        s.Jobs.JobAdded += _ => UpdateJobCount();
        s.Jobs.JobFinished += _ => UpdateJobCount();
        DismissNoticeCommand = new RelayCommand(() => s.Notify.Dismiss());
        NoticeActionCommand = new RelayCommand(() =>
        {
            var n = s.Notify.Current;
            s.Notify.Dismiss();
            n?.Action?.Invoke();
        });
        GoCommand = new RelayCommand(p => Navigate(p as string ?? "home", null));
    }

    public HomeViewModel Home { get; }
    public ExplorerViewModel Explorer { get; }
    public CompressViewModel Compress { get; }
    public ExtractViewModel Extract { get; }
    public SplitViewModel Split { get; }
    public PasswordViewModel Password { get; }
    public DrivesViewModel Drives { get; }
    public IsoViewModel Iso { get; }
    public JobsViewModel Jobs { get; }
    public VerifyViewModel Verify { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<NavItem> Items { get; }
    public NavItem SettingsItem { get; }
    public Notifier Notify => AppServices.Current.Notify;

    public ICommand DismissNoticeCommand { get; }
    public ICommand NoticeActionCommand { get; }
    public ICommand GoCommand { get; }

    public NavItem Selected
    {
        get => _selected;
        set
        {
            if (value is null || !Set(ref _selected, value)) return;
            Raise(nameof(CurrentPage));
            Raise(nameof(IsSettingsSelected));
            Raise(nameof(NavSelected));
        }
    }

    /// <summary>Selection for the main nav list: null while Settings (outside the list) is shown.</summary>
    public NavItem? NavSelected
    {
        get => Items.Contains(_selected) ? _selected : null;
        set
        {
            if (value is not null) Selected = value;
        }
    }

    public bool IsSettingsSelected
    {
        get => _selected == SettingsItem;
        set
        {
            if (value) Selected = SettingsItem;
        }
    }

    public ObservableObject CurrentPage => _selected.Page;

    public int ActiveJobs { get => _activeJobs; private set => Set(ref _activeJobs, value); }

    private void UpdateJobCount() => Ui.OnUi(() => ActiveJobs = AppServices.Current.Jobs.Items.Count(j => j.IsActive));

    public void Navigate(string key, object? parameter)
    {
        Ui.OnUi(() =>
        {
            var item = key == "settings" ? SettingsItem : Items.FirstOrDefault(i => i.Key == key) ?? Items[0];
            Selected = item;
            if (parameter is not null && item.Page is INavigable n)
            {
                try
                {
                    n.OnNavigatedTo(parameter);
                }
                catch (Exception ex)
                {
                    AppServices.Current.Notify.Error(ex);
                }
            }
        });
    }

    /// <summary>Files dropped anywhere on the window.</summary>
    public void HandleDrop(string[] paths)
    {
        if (paths.Length == 0) return;
        if (paths.Length == 1 && File.Exists(paths[0]) && ArchiveFormats.Detect(paths[0]) != ArchiveFormat.Unknown)
        {
            Navigate(ArchiveFormats.Detect(paths[0]) == ArchiveFormat.Iso ? "iso" : "explorer", paths[0]);
            return;
        }
        if (Selected.Page == Extract && paths.All(File.Exists))
        {
            Navigate("extract", paths);
            return;
        }
        if (Selected.Page == Verify)
        {
            Navigate("verify", paths);
            return;
        }
        Navigate("compress", paths);
    }
}
