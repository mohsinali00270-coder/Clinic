using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class LabWorkPage : ContentPage
{
    private readonly LabWorkViewModel _vm;

    public LabWorkPage(LabWorkViewModel vm)
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
