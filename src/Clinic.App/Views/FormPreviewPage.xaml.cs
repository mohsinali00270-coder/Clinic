using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class FormPreviewPage : ContentPage
{
    private readonly FormPreviewViewModel _vm;

    public FormPreviewPage(FormPreviewViewModel vm)
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