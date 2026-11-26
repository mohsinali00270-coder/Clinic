using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class NotesPage : ContentPage
{
    private readonly NotesViewModel _vm;

    public NotesPage(NotesViewModel vm)
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