using System.Windows.Controls;
using Zipark.Desktop.ViewModels;

namespace Zipark.Desktop.Views;

public partial class ExtractView : UserControl
{
    public ExtractView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ExtractViewModel vm) vm.PasswordInput = Pw;
        };
    }
}
