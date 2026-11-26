using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class FormDesignerPage : ContentPage
{
    private readonly FormDesignerViewModel _vm;

    public FormDesignerPage(FormDesignerViewModel vm)
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