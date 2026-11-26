using Clinic.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Clinic.App.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool IsNotBusy => !IsBusy;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Runs an action with a busy flag and turns API problems into a visible message.</summary>
    protected async Task<bool> RunAsync(Func<Task> action)
    {
        if (IsBusy) return false;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await action();
            return true;
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.UserMessage;
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Something went wrong: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>A checkbox row for multi-select lists (roles, branches).</summary>
public sealed partial class SelectableOption(Guid id, string label, string? detail = null) : ObservableObject
{
    public Guid Id { get; } = id;
    public string Label { get; } = label;
    public string? Detail { get; } = detail;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Navigation and dialogs, kept in one place so view models stay testable.</summary>
public static class Ui
{
    public static Task GoToAsync(string route, IDictionary<string, object>? parameters = null) =>
        parameters is null ? Shell.Current.GoToAsync(route) : Shell.Current.GoToAsync(route, parameters);

    public static Task GoBackAsync() => Shell.Current.GoToAsync("..");

    public static Task AlertAsync(string title, string message) =>
        Shell.Current.DisplayAlertAsync(title, message, "OK");

    public static Task<bool> ConfirmAsync(string title, string message, string accept = "Yes") =>
        Shell.Current.DisplayAlertAsync(title, message, accept, "Cancel");

    public static Task<string?> PromptAsync(string title, string message, string? initial = null, bool password = false) =>
        Shell.Current.DisplayPromptAsync(title, message, "OK", "Cancel", initialValue: initial ?? "",
            keyboard: password ? Keyboard.Default : Keyboard.Text);
}
