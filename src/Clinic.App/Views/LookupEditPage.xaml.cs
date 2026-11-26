using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class LookupEditPage : ContentPage
{
    private readonly LookupEditViewModel _vm;

    public LookupEditPage(LookupEditViewModel vm)
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