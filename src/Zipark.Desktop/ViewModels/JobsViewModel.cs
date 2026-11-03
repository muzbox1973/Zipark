using System.Collections.ObjectModel;
using System.Windows.Input;
using Zipark.Application.Jobs;
using Zipark.Core;
using Zipark.Desktop.Infrastructure;

namespace Zipark.Desktop.ViewModels;

public sealed class JobsViewModel : ObservableObject
{
    public JobsViewModel()
    {
        var q = AppServices.Current.Jobs;
        foreach (var j in q.Items) Jobs.Insert(0, j);
        q.JobAdded += j => Ui.OnUi(() => Jobs.Insert(0, j));
        CancelCommand = new RelayCommand(p => (p as JobItem)?.Cancel());
        CancelAllCommand = new RelayCommand(q.CancelAll);
        ClearCommand = new RelayCommand(() =>
        {
            q.ClearFinished();
            foreach (var j in Jobs.Where(j => !j.IsActive).ToList()) Jobs.Remove(j);
        });
        OpenOutputCommand = new RelayCommand(p =>
        {
            if (p is JobItem { Result.Outputs: { Count: > 0 } outs }) AppServices.OpenInExplorer(outs[0]);
        });
    }

    public ObservableCollection<JobItem> Jobs { get; } = [];
    public ICommand CancelCommand { get; }
    public ICommand CancelAllCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand OpenOutputCommand { get; }

    public int MaxParallel
    {
        get => AppServices.Current.Jobs.MaxParallel;
        set
        {
            var v = Math.Clamp(value, 1, 8);
            AppServices.Current.Jobs.MaxParallel = v;
            AppServices.Current.Settings.MaxParallelJobs = v;
            AppServices.Current.SaveSettings();
            Raise();
        }
    }

    private void Raise() => Raise(nameof(MaxParallel));

    public IReadOnlyList<int> ParallelChoices { get; } = [1, 2, 3, 4];

    /// <summary>Shows a completion notice for a job (with "open folder" when there is an output).</summary>
    public static void ReportWhenDone(JobItem job, string? openTarget, bool autoOpen = false)
    {
        _ = job.Completion.ContinueWith(t =>
        {
            var r = t.Result;
            var n = AppServices.Current.Notify;
            if (r.Success)
            {
                var detail = job.Title;
                if (r.Warnings.Count > 0) detail += "\n" + string.Join(", ", r.Warnings.Select(DescribeWarning));
                var target = openTarget ?? r.Outputs.FirstOrDefault();
                n.Success(Loc.T("s.kind." + job.Request.Kind) + " · " + Loc.T("s.stage.Completed"), detail,
                    target is null ? null : Loc.T("s.action.openFolder"), target is null ? null : () => AppServices.OpenInExplorer(target));
                if (autoOpen && target is not null) Ui.OnUi(() => AppServices.OpenInExplorer(target));
            }
            else if (r.Error != ErrorCode.Cancelled)
            {
                n.Show(NoticeKind.Error, Loc.Error(r.Error), job.Title + (string.IsNullOrWhiteSpace(r.Message) ? "" : "\n" + r.Message));
            }
        }, TaskScheduler.Default);
    }

    private static string DescribeWarning(string w)
    {
        var parts = w.Split(':');
        return parts.Length == 2 ? Loc.F("s.warn." + parts[0], parts[1]) : Loc.T("s.warn." + w);
    }
}
