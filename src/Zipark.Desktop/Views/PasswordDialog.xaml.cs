using System.Windows;
using Zipark.Crypto;
using Zipark.Desktop.Infrastructure;

namespace Zipark.Desktop.Views;

public partial class PasswordDialog : Window
{
    private SecretPassword? _result;

    public PasswordDialog(string archiveName, bool retry)
    {
        InitializeComponent();
        ArchiveText.Text = Loc.F("s.pwdlg.for", archiveName);
        RetryText.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            ThemeManager.ApplyTitleBar(this);
            Input.Focus();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _result = Input.Take();
        Input.Clear();
        if (_result is null) return;
        DialogResult = true;
    }

    public SecretPassword? TakePassword()
    {
        var r = _result;
        _result = null;
        return r;
    }
}
