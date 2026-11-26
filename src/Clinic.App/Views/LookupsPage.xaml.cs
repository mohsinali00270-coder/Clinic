using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class LookupsPage : ContentPage
{
    private readonly LookupsViewModel _vm;

    public LookupsPage(LookupsViewModel vm)
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