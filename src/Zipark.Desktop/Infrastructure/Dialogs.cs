using System.IO;
using System.Windows;
using Zipark.Core;
using Zipark.Crypto;
using Zipark.Desktop.Views;
using Microsoft.Win32;

namespace Zipark.Desktop.Infrastructure;

public static class Dialogs
{
    public const string ArchiveFilter = "*.7z;*.zip;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.txz;*.zst;*.iso;*.cab;*.wim;*.001";

    /// <summary>The main window when it is on screen; dialogs opened from background (tray) work have no owner.</summary>
    private static Window? Owner => System.Windows.Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    public static string? OpenArchive(string? title = null) =>
        OpenFiles(title ?? Loc.T("s.dlg.openArchive"), $"{Loc.T("s.dlg.archives")}|{ArchiveFilter}|{Loc.T("s.dlg.allFiles")}|*.*", false).FirstOrDefault();

    public static IReadOnlyList<string> OpenArchives() =>
        OpenFiles(Loc.T("s.dlg.openArchive"), $"{Loc.T("s.dlg.archives")}|{ArchiveFilter}|{Loc.T("s.dlg.allFiles")}|*.*", true);

    public static IReadOnlyList<string> OpenFiles(string title, string filter = "", bool multi = true)
    {
        var dlg = new OpenFileDialog { Title = title, Multiselect = multi, Filter = filter.Length > 0 ? filter : $"{Loc.T("s.dlg.allFiles")}|*.*" };
        return dlg.ShowDialog(Owner) == true ? dlg.FileNames : [];
    }

    public static string? PickFolder(string? title = null, string? initial = null)
    {
        var dlg = new OpenFolderDialog { Title = title ?? Loc.T("s.dlg.pickFolder") };
        if (initial is not null && Directory.Exists(initial)) dlg.InitialDirectory = initial;
        return dlg.ShowDialog(Owner) == true ? dlg.FolderName : null;
    }

    public static IReadOnlyList<string> PickFolders(string? title = null)
    {
        var dlg = new OpenFolderDialog { Title = title ?? Loc.T("s.dlg.pickFolder"), Multiselect = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FolderNames : [];
    }

    public static string? SaveFile(string title, string fileName, string filter)
    {
        var dlg = new SaveFileDialog { Title = title, FileName = fileName, Filter = filter, OverwritePrompt = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public static bool Confirm(string message, string? title = null) =>
        (Owner is { } o
            ? MessageBox.Show(o, message, title ?? "Zipark", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(message, title ?? "Zipark", MessageBoxButton.YesNo, MessageBoxImage.Question)) == MessageBoxResult.Yes;

    private static readonly SemaphoreSlim ConflictGate = new(1, 1);

    /// <summary>
    /// Asks what to do with a file that already exists. Questions from parallel jobs are shown one at a time;
    /// closing the dialog stops the extraction.
    /// </summary>
    public static async Task<ConflictDecision> AskConflictAsync(ConflictInfo info)
    {
        await ConflictGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var owner = Owner;
                var dlg = new ConflictDialog(info) { Owner = owner };
                if (owner is null)
                {
                    // Started from the Explorer context menu: no app window, show it in front.
                    dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    dlg.ShowInTaskbar = true;
                    dlg.Topmost = true;
                }
                return dlg.ShowDialog() == true ? dlg.Decision : new ConflictDecision(ConflictChoice.Cancel, false);
            });
        }
        finally
        {
            ConflictGate.Release();
        }
    }

    /// <summary>Asks for a password; returns null when the user cancels.</summary>
    public static SecretPassword? AskPassword(string archiveName, bool retry)
    {
        var d = System.Windows.Application.Current.Dispatcher;
        if (!d.CheckAccess()) return d.Invoke(() => AskPassword(archiveName, retry));
        var owner = Owner;
        var dlg = new PasswordDialog(archiveName, retry) { Owner = owner };
        if (owner is null)
        {
            // Background work started from Explorer: no app window, so show the prompt in front, centred on screen.
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowInTaskbar = true;
            dlg.Topmost = true;
        }
        return dlg.ShowDialog() == true ? dlg.TakePassword() : null;
    }
}
