using System.Globalization;
using System.Windows;
using Zipark.Core;
using Zipark.Desktop.Infrastructure;

namespace Zipark.Desktop.Views;

/// <summary>"A file with this name already exists" — compares both files and lets the user choose.</summary>
public partial class ConflictDialog : Window
{
    public ConflictDecision Decision { get; private set; } = new(ConflictChoice.Cancel, false);

    public ConflictDialog(ConflictInfo info)
    {
        InitializeComponent();
        FileName.Text = Path.GetFileName(info.TargetPath);
        Folder.Text = Path.GetDirectoryName(info.TargetPath);
        Folder.ToolTip = info.TargetPath;

        ExistingSize.Text = ByteSize.Format(info.ExistingSize);
        IncomingSize.Text = ByteSize.Format(info.IncomingSize);
        ExistingDate.Text = Format(info.ExistingModified);
        IncomingDate.Text = Format(info.IncomingModified);

        var diff = info.IncomingModified - info.ExistingModified;
        if (diff > TimeSpan.FromSeconds(2)) IncomingNewer.Visibility = Visibility.Visible;
        else if (diff < TimeSpan.FromSeconds(-2)) ExistingNewer.Visibility = Visibility.Visible;
        if (info.ExistingSize == info.IncomingSize && Math.Abs(diff.TotalSeconds) <= 2) SameNote.Visibility = Visibility.Visible;

        KeepBothHint.Text = Loc.F("s.conflictdlg.keepBothHint", info.RenameTo);
        ApplyAll.Content = info.Remaining > 0 ? Loc.F("s.conflictdlg.applyAll", info.Remaining) : Loc.T("s.conflictdlg.applyAllNone");
        ApplyAll.IsEnabled = info.Remaining > 0;

        Loaded += (_, _) =>
        {
            ThemeManager.ApplyTitleBar(this);
            KeepBothButton.Focus(); // the choice that never loses data
        };
    }

    private static string Format(DateTime d) => d.ToString("yyyy-MM-dd  HH:mm:ss", CultureInfo.InvariantCulture);

    private void Choose(ConflictChoice c)
    {
        Decision = new ConflictDecision(c, ApplyAll.IsChecked == true);
        DialogResult = true;
    }

    private void OnOverwrite(object sender, RoutedEventArgs e) => Choose(ConflictChoice.Overwrite);
    private void OnKeepBoth(object sender, RoutedEventArgs e) => Choose(ConflictChoice.Rename);
    private void OnSkip(object sender, RoutedEventArgs e) => Choose(ConflictChoice.Skip);
    private void OnStop(object sender, RoutedEventArgs e) => Choose(ConflictChoice.Cancel);
}
