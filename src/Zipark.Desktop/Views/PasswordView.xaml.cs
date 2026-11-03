using System.Windows.Controls;
using Zipark.Desktop.ViewModels;

namespace Zipark.Desktop.Views;

public partial class PasswordView : UserControl
{
    public PasswordView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PasswordViewModel vm)
            {
                vm.ConfirmInput = Confirm;
                vm.PasswordInput = Pw;
                Confirm.Changed += (_, _) => vm.Evaluate();
            }
        };
    }
}
