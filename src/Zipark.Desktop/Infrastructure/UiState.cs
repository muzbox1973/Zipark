using System.Windows;
using Zipark.Crypto;

namespace Zipark.Desktop.Infrastructure;

/// <summary>Global UI flags bindable from any view: {Binding Expert, Source={x:Static infra:UiState.Instance}}.</summary>
public sealed class UiState : ObservableObject
{
    public static UiState Instance { get; } = new();

    private bool _expert;
    public bool Expert { get => _expert; set => Set(ref _expert, value); }
}

/// <summary>Implemented by the SecretInput control so view models can take a password without data binding it.</summary>
public interface IPasswordSource
{
    int Length { get; }
    StrengthLevel Strength { get; }
    /// <summary>Returns a copy of the current password, or null when empty.</summary>
    SecretPassword? Take();
    void Clear();
    event EventHandler? Changed;
}

public static class Ui
{
    public static void OnUi(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }
}
