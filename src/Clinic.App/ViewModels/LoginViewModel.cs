using Clinic.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly AuthService _auth;
    private readonly AppSettings _settings;

    public LoginViewModel(AuthService auth, AppSettings settings)
    {
        _auth = auth;
        _settings = settings;
        ServerUrl = settings.ServerUrl;
        TenantCode = settings.LastTenantCode;
        UserName = settings.LastUserName;
    }

    [ObservableProperty] public partial string ServerUrl { get; set; }
    [ObservableProperty] public partial string TenantCode { get; set; }
    [ObservableProperty] public partial string UserName { get; set; }
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial bool ShowServerSettings { get; set; }

    public event EventHandler? SignedIn;

    [RelayCommand]
    private void ToggleServerSettings() => ShowServerSettings = !ShowServerSettings;

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(TenantCode) || string.IsNullOrWhiteSpace(UserName) || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Enter the clinic code, user name and password.";
            return;
        }

        if (!Uri.TryCreate(ServerUrl?.Trim(), UriKind.Absolute, out _))
        {
            ErrorMessage = "The server address is not a valid URL.";
            ShowServerSettings = true;
            return;
        }

        _settings.ServerUrl = ServerUrl!;
        var ok = await RunAsync(() => _auth.LoginAsync(TenantCode.Trim(), UserName.Trim(), Password));
        if (!ok) return;

        Password = "";
        SignedIn?.Invoke(this, EventArgs.Empty);
    }
}
