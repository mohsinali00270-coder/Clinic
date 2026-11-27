using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class PatientBillingPage : ContentPage
{
    private readonly PatientBillingViewModel _vm;

    public PatientBillingPage(PatientBillingViewModel vm)
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
