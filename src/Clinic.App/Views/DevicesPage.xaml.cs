using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class DevicesPage : ContentPage
{
    private readonly DevicesViewModel _vm;

    public DevicesPage(DevicesViewModel vm)
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