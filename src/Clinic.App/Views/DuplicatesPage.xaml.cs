using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class DuplicatesPage : ContentPage
{
    private readonly DuplicatesViewModel _vm;

    public DuplicatesPage(DuplicatesViewModel vm)
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
