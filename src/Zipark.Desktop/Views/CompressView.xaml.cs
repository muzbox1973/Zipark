using System.Windows.Controls;
using Zipark.Desktop.ViewModels;

namespace Zipark.Desktop.Views;

public partial class CompressView : UserControl
{
    public CompressView()
    {
        InitializeComponent();
        Pw.Changed += (_, _) => (DataContext as CompressViewModel)?.UpdateWarnings();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CompressViewModel vm)
            {
                vm.PasswordInput = Pw;
                vm.ConfirmInput = Confirm;
            }
        };
    }
}
