using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class EncounterPage : ContentPage
{
    private readonly EncounterViewModel _vm;

    public EncounterPage(EncounterViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    /// <summary>FR-DYN-23: save the consultation notes when leaving the page.</summary>
    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await _vm.OnLeavingAsync();
    }
}