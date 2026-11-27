using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class ConflictPage : ContentPage
{
    private readonly ConflictViewModel _vm;

    public ConflictPage(ConflictViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }
}
