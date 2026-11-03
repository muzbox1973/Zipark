using System.IO;
using System.Windows;
using Zipark.Crypto;
using Zipark.Desktop.Views;
using Microsoft.Win32;

namespace Zipark.Desktop.Infrastructure;

public static class Dialogs
{
    public const string ArchiveFilter = "*.7z;*.zip;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.txz;*.zst;*.iso;*.cab;*.wim;*.001";

    private static Window? Owner => System.Windows.Application.Current?.MainWindow;

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
        MessageBox.Show(Owner!, message, title ?? "Zipark", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Asks for a password; returns null when the user cancels.</summary>
    public static SecretPassword? AskPassword(string archiveName, bool retry)
    {
        var d = System.Windows.Application.Current.Dispatcher;
        if (!d.CheckAccess()) return d.Invoke(() => AskPassword(archiveName, retry));
        var dlg = new PasswordDialog(archiveName, retry) { Owner = Owner };
        return dlg.ShowDialog() == true ? dlg.TakePassword() : null;
    }
}
