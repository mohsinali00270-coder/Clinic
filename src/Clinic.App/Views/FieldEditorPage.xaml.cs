using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class FieldEditorPage : ContentPage
{
    private readonly FieldEditorViewModel _vm;

    public FieldEditorPage(FieldEditorViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Prepare();
    }
}