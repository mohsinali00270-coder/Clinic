using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class InvoicePage : ContentPage
{
    public InvoicePage(InvoiceViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
