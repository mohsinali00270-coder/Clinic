using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class AdmissionPage : ContentPage
{
    public AdmissionPage(AdmissionViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
