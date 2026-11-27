using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class HomePage : ContentPage
{
    private readonly HomeViewModel _vm;

    public HomePage(HomeViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        _vm.ApplyWidth(width);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.AppearingCommand.Execute(null);
    }
}
