using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class BookingPage : ContentPage
{
    private readonly BookingViewModel _vm;

    public BookingPage(BookingViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // loaded from query attributes
    }
}