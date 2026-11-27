using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class CasePage : ContentPage
{
    public CasePage(CaseViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
