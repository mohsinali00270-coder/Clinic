using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class BranchEditPage : ContentPage
{
    private readonly BranchEditViewModel _vm;

    public BranchEditPage(BranchEditViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }
}