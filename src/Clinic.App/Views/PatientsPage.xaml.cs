using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class PatientsPage : ContentPage
{
    private readonly PatientsViewModel _vm;

    public PatientsPage(PatientsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.SearchCommand.Execute(null);
    }
}