using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class StockPage : ContentPage
{
    private readonly StockViewModel _vm;

    public StockPage(StockViewModel vm)
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
