using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class NoteEditPage : ContentPage
{
    private readonly NoteEditViewModel _vm;

    public NoteEditPage(NoteEditViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    /// <summary>FR-DYN-23: autosave the draft when leaving the page.</summary>
    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await _vm.OnLeavingAsync();
    }
}
