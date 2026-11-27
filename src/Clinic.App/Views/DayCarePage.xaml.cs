using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class DayCarePage : ContentPage
{
    private readonly DayCareViewModel _vm;

    public DayCarePage(DayCareViewModel vm)
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
