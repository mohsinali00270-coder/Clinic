using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class ReferralPage : ContentPage
{
    public ReferralPage(ReferralViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
