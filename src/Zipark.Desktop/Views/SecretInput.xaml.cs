using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zipark.Crypto;
using Zipark.Desktop.Infrastructure;

namespace Zipark.Desktop.Views;

/// <summary>
/// Password field with show/hide toggle and optional strength meter. The value is read from
/// PasswordBox.SecurePassword into a <see cref="SecretPassword"/> on demand; it is never data-bound.
/// While "show" is on, the text necessarily lives in a TextBox; hiding it again clears that TextBox.
/// </summary>
public partial class SecretInput : UserControl, IPasswordSource
{
    private bool _syncing;

    public SecretInput()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ShowMeterProperty =
        DependencyProperty.Register(nameof(ShowMeter), typeof(bool), typeof(SecretInput), new PropertyMetadata(false));

    public bool ShowMeter
    {
        get => (bool)GetValue(ShowMeterProperty);
        set => SetValue(ShowMeterProperty, value);
    }

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SecretInput), new PropertyMetadata(""));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public int Length => Eye.IsChecked == true ? Plain.Text.Length : Box.SecurePassword.Length;
    public StrengthLevel Strength { get; private set; }
    public event EventHandler? Changed;

    public SecretPassword? Take()
    {
        if (Eye.IsChecked == true) return Plain.Text.Length == 0 ? null : SecretPassword.FromChars(Plain.Text);
        using var secure = Box.SecurePassword;
        return secure.Length == 0 ? null : SecretPassword.FromSecureString(secure);
    }

    public void Clear()
    {
        _syncing = true;
        Box.Clear();
        Plain.Clear();
        _syncing = false;
        Eye.IsChecked = false;
        Update();
    }

    public new void Focus() => (Eye.IsChecked == true ? (Control)Plain : Box).Focus();

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing) Update();
    }

    private void OnPlainChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing) Update();
    }

    private void OnShow(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        using (var pw = SecretPasswordFromBox()) Plain.Text = pw is null ? "" : new string(pw.Span);
        Box.Visibility = Visibility.Collapsed;
        Plain.Visibility = Visibility.Visible;
        _syncing = false;
        Plain.CaretIndex = Plain.Text.Length;
        Plain.Focus();
    }

    private void OnHide(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        Box.Password = Plain.Text;
        Plain.Clear();
        Plain.Visibility = Visibility.Collapsed;
        Box.Visibility = Visibility.Visible;
        _syncing = false;
        Box.Focus();
    }

    private SecretPassword? SecretPasswordFromBox()
    {
        using var secure = Box.SecurePassword;
        return secure.Length == 0 ? null : SecretPassword.FromSecureString(secure);
    }

    private void Update()
    {
        using (var pw = Take())
        {
            var r = PasswordStrength.Evaluate(pw is null ? ReadOnlySpan<char>.Empty : pw.Span);
            Strength = r.Level;
            Meter.Visibility = ShowMeter && pw is not null ? Visibility.Visible : Visibility.Collapsed;
            Bar.Value = ((int)r.Level + 1) * 20;
            Bar.Foreground = new StrengthBrushConverter().Convert(r.Level, typeof(Brush), null, System.Globalization.CultureInfo.CurrentCulture) as Brush;
            BarText.Text = Loc.T("s.strength." + r.Level);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
