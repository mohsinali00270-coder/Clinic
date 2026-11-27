using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class AuditPage : ContentPage
{
    private readonly AuditViewModel _vm;

    public AuditPage(AuditViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }
}
