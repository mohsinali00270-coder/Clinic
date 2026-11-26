using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class LoginPage : ContentPage
{
    private readonly LoginViewModel _vm;

    public LoginPage(LoginViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        vm.SignedIn += async (_, _) => await AppShell.Instance.ShowSignedInAsync();
    }
}