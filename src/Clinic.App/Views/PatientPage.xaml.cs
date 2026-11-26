using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class PatientPage : ContentPage
{
    private readonly PatientViewModel _vm;

    public PatientPage(PatientViewModel vm)
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