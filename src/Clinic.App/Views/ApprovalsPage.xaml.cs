using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class ApprovalsPage : ContentPage
{
    private readonly ApprovalsViewModel _vm;

    public ApprovalsPage(ApprovalsViewModel vm)
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
