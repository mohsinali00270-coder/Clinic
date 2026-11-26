using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class SyncCenterPage : ContentPage
{
    private readonly SyncCenterViewModel _vm;

    public SyncCenterPage(SyncCenterViewModel vm)
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