using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class UserEditPage : ContentPage
{
    private readonly UserEditViewModel _vm;

    public UserEditPage(UserEditViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }
}