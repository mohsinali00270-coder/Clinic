using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class DoctorEditPage : ContentPage
{
    private readonly DoctorEditViewModel _vm;

    public DoctorEditPage(DoctorEditViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // loaded from query attributes
    }
}