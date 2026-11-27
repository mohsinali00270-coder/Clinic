using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class DispensePage : ContentPage
{
    private readonly DispenseViewModel _vm;

    public DispensePage(DispenseViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }
}
