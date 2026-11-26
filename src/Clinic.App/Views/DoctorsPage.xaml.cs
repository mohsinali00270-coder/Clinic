using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class DoctorsPage : ContentPage
{
    private readonly DoctorsViewModel _vm;

    public DoctorsPage(DoctorsViewModel vm)
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