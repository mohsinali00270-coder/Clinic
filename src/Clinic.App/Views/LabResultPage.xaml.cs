using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class LabResultPage : ContentPage
{
    public LabResultPage(LabResultViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
