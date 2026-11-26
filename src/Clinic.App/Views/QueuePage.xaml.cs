using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class QueuePage : ContentPage
{
    private readonly QueueViewModel _vm;

    public QueuePage(QueueViewModel vm)
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