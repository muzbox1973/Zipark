using System.Collections.ObjectModel;
using System.Windows.Input;
using Zipark.Core;
using Zipark.Crypto;
using Zipark.Desktop.Infrastructure;

namespace Zipark.Desktop.ViewModels;

/// <summary>
/// Password workshop: strength meter, confirmation and guidance. Nothing is stored — a password only
/// moves to "new archive" in memory when the user presses the button.
/// </summary>
public sealed class PasswordViewModel : ObservableObject
{
    private IPasswordSource? _input;
    private StrengthLevel _strength;
    private string _entropy = "";
    private bool _matches;

    public PasswordViewModel()
    {
        UseCommand = new RelayCommand(Use, () => _input is { Length: > 0 } && _matches);
    }

    public ICommand UseCommand { get; }
    public IPasswordSource? ConfirmInput { get; set; }

    public IPasswordSource? PasswordInput
    {
        get => _input;
        set
        {
            _input = value;
            if (_input is not null) _input.Changed += (_, _) => Evaluate();
        }
    }

    public StrengthLevel Strength { get => _strength; private set => Set(ref _strength, value); }
    public string StrengthText => Loc.T("s.strength." + _strength);
    public double StrengthValue => ((int)_strength + 1) * 20;
    public string Entropy { get => _entropy; private set => Set(ref _entropy, value); }
    public bool Matches { get => _matches; private set => Set(ref _matches, value); }
    public ObservableCollection<string> Hints { get; } = [];

    public void Evaluate()
    {
        Hints.Clear();
        using var pw = _input?.Take();
        using var confirm = ConfirmInput?.Take();
        if (pw is null)
        {
            Strength = StrengthLevel.VeryWeak;
            Entropy = "";
            Matches = false;
        }
        else
        {
            var r = PasswordStrength.Evaluate(pw.Span);
            Strength = r.Level;
            Entropy = Loc.F("s.pw.entropy", r.EntropyBits);
            foreach (var h in r.Hints.Where(h => h != "pw.hint.longer" || r.Level < StrengthLevel.Strong)) Hints.Add(Loc.T("s." + h));
            Matches = pw.ContentEquals(confirm);
        }
        Raise(nameof(StrengthText));
        Raise(nameof(StrengthValue));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Use()
    {
        var pw = _input?.Take();
        if (pw is null) return;
        _input?.Clear();
        ConfirmInput?.Clear();
        Evaluate();
        AppServices.Current.Navigate?.Invoke("compress", new CompressPreset([], ArchiveFormat.SevenZip, pw));
    }
}
