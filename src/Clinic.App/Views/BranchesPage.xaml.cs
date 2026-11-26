using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class BranchesPage : ContentPage
{
    private readonly BranchesViewModel _vm;

    public BranchesPage(BranchesViewModel vm)
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