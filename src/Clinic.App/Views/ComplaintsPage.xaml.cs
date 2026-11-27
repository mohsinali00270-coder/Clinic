using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class ComplaintsPage : ContentPage
{
    private readonly ComplaintsViewModel _vm;

    public ComplaintsPage(ComplaintsViewModel vm)
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
