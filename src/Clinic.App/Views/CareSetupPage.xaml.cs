using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class CareSetupPage : ContentPage
{
    private readonly CareSetupViewModel _vm;

    public CareSetupPage(CareSetupViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.LoadCommand.Execute(null);
    }
}
