using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class UnlockPage : ContentPage
{
    private readonly UnlockViewModel _vm;

    public UnlockPage(UnlockViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        vm.Unlocked += async (_, _) => await AppShell.Instance.ShowSignedInAsync();
        vm.PasswordRequested += async (_, _) => await AppShell.Instance.ShowLoginAsync();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Prepare();
    }
}