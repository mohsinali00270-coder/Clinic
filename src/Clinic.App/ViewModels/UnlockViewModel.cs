using Clinic.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

/// <summary>FR-AUTH-05: unlock the cached session with the offline PIN (works with no network).</summary>
public sealed partial class UnlockViewModel(SessionStore session, OfflinePinService pins) : ViewModelBase
{
    [ObservableProperty] public partial string Pin { get; set; } = "";
    [ObservableProperty] public partial string Greeting { get; set; } = "";

    public event EventHandler? Unlocked;
    public event EventHandler? PasswordRequested;

    public void Prepare()
    {
        Pin = "";
        ErrorMessage = null;
        var me = session.Me;
        Greeting = me is null ? "" : $"{me.FullName} · {me.TenantName}";
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (session.Me is not { } me) return;
        if (!OfflinePinService.IsValidFormat(Pin))
        {
            ErrorMessage = "Enter your 4–8 digit PIN.";
            return;
        }

        var remaining = await pins.VerifyAsync(me.UserId, Pin);
        Pin = "";
        switch (remaining)
        {
            case null:
                Unlocked?.Invoke(this, EventArgs.Empty);
                break;
            case 0:
                ErrorMessage = "Too many wrong PINs. Sign in with your password.";
                await Task.Delay(1200);
                PasswordRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                ErrorMessage = $"Wrong PIN. {remaining} attempt(s) left.";
                break;
        }
    }

    [RelayCommand]
    private void UsePassword() => PasswordRequested?.Invoke(this, EventArgs.Empty);
}
