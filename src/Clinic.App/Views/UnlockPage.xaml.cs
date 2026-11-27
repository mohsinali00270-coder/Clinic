using Clinic.App.ViewModels;

namespace Clinic.App.Views;

public partial class UnlockPage : ContentPage
{
    private readonly UnlockViewModel _vm;

    public UnlockPage(UnlockViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        vm.Unlocked += async (_, _) =>
        {
            if (IsLockScreen) await AppShell.Instance.Lock.UnlockedAsync();
            else await AppShell.Instance.ShowSignedInAsync();
        };
        vm.PasswordRequested += async (_, _) =>
        {
            if (IsLockScreen) await AppShell.Instance.Lock.UnlockedAsync();
            await AppShell.Instance.ShowLoginAsync();
        };
    }

    /// <summary>FR-SEC-05: shown modally over the signed-in app after inactivity (back does not dismiss it).</summary>
    public bool IsLockScreen { get; set; }

    protected override bool OnBackButtonPressed() => IsLockScreen || base.OnBackButtonPressed();

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Prepare();
    }
}