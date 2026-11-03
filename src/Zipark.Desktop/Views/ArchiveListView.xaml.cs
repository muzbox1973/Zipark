using System.Windows.Controls;
using System.Windows.Input;
using Zipark.ArchiveExplorer;
using Zipark.Desktop.ViewModels;

namespace Zipark.Desktop.Views;

public partial class ArchiveListView : UserControl
{
    public ArchiveListView()
    {
        InitializeComponent();
    }

    private ArchiveBrowserViewModel? Vm => DataContext as ArchiveBrowserViewModel;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Vm?.SetSelection(List.SelectedItems.Cast<ArchiveNode>().ToList());

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is ArchiveNode n) Vm?.OpenItemCommand.Execute(n);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is ArchiveNode n)
        {
            Vm?.OpenItemCommand.Execute(n);
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            Vm?.UpCommand.Execute(null);
            e.Handled = true;
        }
    }
}
