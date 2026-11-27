using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class FollowUpsPage : ContentPage
{
    private readonly FollowUpsViewModel _vm;

    public FollowUpsPage(FollowUpsViewModel vm)
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
