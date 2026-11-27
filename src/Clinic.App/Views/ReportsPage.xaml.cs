using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class ReportsPage : ContentPage
{
    private readonly ReportsViewModel _vm;

    public ReportsPage(ReportsViewModel vm)
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
