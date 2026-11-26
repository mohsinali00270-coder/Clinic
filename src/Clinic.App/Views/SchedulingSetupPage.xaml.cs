using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class SchedulingSetupPage : ContentPage
{
    private readonly SchedulingSetupViewModel _vm;

    public SchedulingSetupPage(SchedulingSetupViewModel vm)
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