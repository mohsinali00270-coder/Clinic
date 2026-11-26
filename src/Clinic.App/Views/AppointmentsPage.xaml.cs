using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class AppointmentsPage : ContentPage
{
    private readonly AppointmentsViewModel _vm;

    public AppointmentsPage(AppointmentsViewModel vm)
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