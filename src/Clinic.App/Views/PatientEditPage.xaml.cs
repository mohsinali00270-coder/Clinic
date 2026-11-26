using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class PatientEditPage : ContentPage
{
    private readonly PatientEditViewModel _vm;

    public PatientEditPage(PatientEditViewModel vm)
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