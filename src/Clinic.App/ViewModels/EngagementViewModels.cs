using System.Collections.ObjectModel;
using Clinic.App.Services;
using Clinic.Client.Core.Clinical;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

internal static class EngagementUi
{
    /// <summary>FR-COM-01/03: pick a template and queue it for the patient (sent by the server after sync).</summary>
    public static async Task SendMessageAsync(SyncCoordinator sync, SessionStore session, Guid patientId, string? templateKey = null,
        IReadOnlyDictionary<string, string?>? values = null)
    {
        var templates = await sync.Messaging.TemplatesAsync();
        if (templateKey is null)
        {
            var names = templates.Select(t => t.Name).Distinct().ToList();
            var pick = await CareUi.PickAsync("Message", names);
            templateKey = templates.FirstOrDefault(t => t.Name == pick)?.Key;
            if (templateKey is null) return;
        }
        var all = new Dictionary<string, string?>(values ?? new Dictionary<string, string?>())
        {
            ["clinic"] = CareUi.Clinic(session),
            ["date"] = DateTime.Today.ToString("dd MMM yyyy"),
        };
        var queued = await sync.Messaging.QueueAsync(patientId, sync.CurrentBranchId, templateKey, all);
        await Ui.AlertAsync("Message", queued is null
            ? "Not sent: the patient has opted out of messages or has no phone number."
            : $"Queued as {queued.Channel.ToUpperInvariant()}. It is sent after the next sync; its status shows on the patient's timeline.\n\n{queued.Body}");
    }

    public static async Task EditPreferenceAsync(SyncCoordinator sync, Guid patientId)
    {
        var current = await sync.Messaging.PreferenceAsync(patientId);
        var language = await CareUi.PickAsync($"Language (now {(current.Language == "ur" ? "Urdu" : "English")})", ["English", "اردو (Urdu)"]);
        if (language is null) return;
        var channel = await CareUi.PickAsync($"Channel (now {current.Channel})", ["SMS", "WhatsApp", "Email"]);
        if (channel is null) return;
        var optOut = await Shell.Current.DisplayAlertAsync("Messages", "Does the patient agree to receive reminders and results messages?", "Yes, send", "No, opt out");
        await sync.Messaging.SetPreferenceAsync(patientId, language.StartsWith("English") ? "en" : "ur", channel.ToLowerInvariant(), !optOut);
    }

    /// <summary>FR-FBK-01: quick in-clinic feedback (stars for the visit, doctor and service, plus a comment).</summary>
    public static async Task RecordFeedbackAsync(SyncCoordinator sync, Guid patientId)
    {
        string[] stars = ["★★★★★ Excellent", "★★★★ Good", "★★★ OK", "★★ Poor", "★ Very poor"];
        int? Rate(string? pick) => pick is null ? null : 5 - Array.IndexOf(stars, pick);
        var overall = Rate(await CareUi.PickAsync("Overall visit", stars));
        if (overall is null) return;
        var doctors = await sync.Store.ListAsync<LocalDoctor>(d => d.IsActive);
        var latest = (await sync.Encounters.ForPatientAsync(patientId)).FirstOrDefault();
        var doctor = latest is null ? null : doctors.FirstOrDefault(d => d.Id == latest.DoctorId);
        var doctorRating = doctor is null ? null : Rate(await CareUi.PickAsync($"{doctor.FullName}", stars));
        var service = Rate(await CareUi.PickAsync("Reception, waiting and billing", stars));
        var comment = await Ui.PromptAsync("Comments", "Anything to tell us? (optional)");
        await sync.Feedback.RecordAsync(patientId, sync.CurrentBranchId, overall.Value, doctorRating, service, comment, latest?.Id, doctor?.Id);
        if (overall <= 2 && await Ui.ConfirmAsync("Feedback", "Record this as a complaint as well?", "Yes"))
            await sync.Feedback.RaiseAsync(sync.CurrentBranchId, comment is { Length: > 0 } ? comment[..Math.Min(comment.Length, 200)] : "Poor visit rating",
                comment, null, patientId);
    }
}

// =====================================================================================================
// FR-BIL-14 approvals
// =====================================================================================================

public sealed partial class ApprovalsViewModel(SyncCoordinator sync) : ViewModelBase
{
    public ObservableCollection<CareRow> Pending { get; } = [];

    [RelayCommand]
    public Task LoadAsync() => RunAsync(async () =>
    {
        var patients = (await sync.Store.ListAsync<LocalPatient>()).ToDictionary(p => p.Id, p => p.FullName);
        var invoices = (await sync.Store.ListAsync<LocalInvoice>()).ToDictionary(i => i.Id, i => i.InvoiceNo);
        Pending.Clear();
        foreach (var c in await sync.Billing.PendingCreditsAsync(sync.CurrentBranchId))
            Pending.Add(new CareRow(c.Id, $"{(c.Kind == CreditKind.WriteOff ? "Write-off" : "Credit note")} · Rs {c.Amount:N0} · {patients.GetValueOrDefault(c.PatientId)}",
                $"Invoice {invoices.GetValueOrDefault(c.InvoiceId)} · {c.Reason} · {c.RequestedAt.ToLocalTime():dd MMM HH:mm}"));
    });

    [RelayCommand]
    private async Task DecideAsync(CareRow row)
    {
        var pick = await CareUi.PickAsync(row.Title, ["Approve", "Reject"]);
        if (pick is null) return;
        var note = await Ui.PromptAsync(pick, "Note (optional)");
        await RunAsync(async () => { await sync.Billing.DecideCreditAsync(row.Id, pick == "Approve", note); await LoadAsync(); });
    }
}

// =====================================================================================================
// FR-BIL-17 expenses
// =====================================================================================================

public sealed partial class ExpensesViewModel(SyncCoordinator sync) : ViewModelBase
{
    public ObservableCollection<CareRow> Items { get; } = [];

    [ObservableProperty] public partial string TotalText { get; set; } = "";

    [RelayCommand]
    public Task LoadAsync() => RunAsync(async () =>
    {
        var from = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var categories = (await sync.Store.ListAsync<LocalLookupItem>(i => i.ListKey == "expense_categories")).ToDictionary(i => i.Code, i => i.LabelEn);
        var list = await sync.Expenses.ListAsync(sync.CurrentBranchId, from, DateTime.Today);
        TotalText = $"This month: Rs {list.Sum(e => e.Amount):N0} · " +
                    string.Join(", ", list.GroupBy(e => e.Category).OrderByDescending(g => g.Sum(e => e.Amount)).Take(4)
                        .Select(g => $"{categories.GetValueOrDefault(g.Key, g.Key)} Rs {g.Sum(e => e.Amount):N0}"));
        Items.Clear();
        foreach (var e in list)
            Items.Add(new CareRow(e.Id, $"{categories.GetValueOrDefault(e.Category, e.Category)} · Rs {e.Amount:N0}",
                $"{e.SpentOn:dd MMM} · {e.Method}" + (e.PaidTo is null ? "" : $" · {e.PaidTo}") + (e.Notes is null ? "" : $" · {e.Notes}")));
    });

    [RelayCommand]
    private async Task AddAsync()
    {
        var categories = (await sync.Store.ListAsync<LocalLookupItem>(i => i.ListKey == "expense_categories" && i.IsActive)).OrderBy(i => i.SortOrder).ToList();
        var pick = await CareUi.PickAsync("Category", categories.Select(c => c.LabelEn));
        var category = categories.FirstOrDefault(c => c.LabelEn == pick);
        if (category is null) return;
        var amount = await Shell.Current.DisplayPromptAsync("Expense", "Amount (Rs)", "Next", "Cancel", keyboard: Keyboard.Numeric);
        if (!decimal.TryParse(amount, out var a)) return;
        var paidTo = await Ui.PromptAsync("Expense", "Paid to (optional)");
        var method = await PaymentUi.PickMethodAsync(sync) ?? PaymentMethods.Cash;
        await RunAsync(async () => { await sync.Expenses.AddAsync(sync.CurrentBranchId, category.Code, a, DateTime.Today, method, paidTo); await LoadAsync(); });
    }
}

// =====================================================================================================
// FR-FBK-01/02 feedback and complaints
// =====================================================================================================

public sealed partial class ComplaintsViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase
{
    public ObservableCollection<CareRow> Complaints { get; } = [];
    public ObservableCollection<string> Ratings { get; } = [];

    [ObservableProperty] public partial bool ShowClosed { get; set; }
    partial void OnShowClosedChanged(bool value) => LoadCommand.Execute(null);
    public bool CanManage => session.Has(Permissions.Feedback.ManageComplaints);

    [RelayCommand]
    public Task LoadAsync() => RunAsync(async () =>
    {
        var patients = (await sync.Store.ListAsync<LocalPatient>()).ToDictionary(p => p.Id, p => p.FullName);
        Complaints.Clear();
        foreach (var c in await sync.Feedback.ComplaintsAsync(sync.CurrentBranchId, openOnly: !ShowClosed))
            Complaints.Add(new CareRow(c.Id, $"{c.Subject}", $"{ComplaintStatus.Display(c.Status)} · {c.RaisedAt.ToLocalTime():dd MMM}" +
                (c.PatientId is { } p ? $" · {patients.GetValueOrDefault(p)}" : "") + (c.AssignedName is null ? "" : $" · with {c.AssignedName}") +
                (c.Resolution is null ? "" : $" · {c.Resolution}"), c.Status == ComplaintStatus.Open, c));
        var doctors = (await sync.Store.ListAsync<LocalDoctor>()).ToDictionary(d => d.Id, d => d.FullName);
        Ratings.Clear();
        foreach (var r in await sync.Feedback.RatingsByDoctorAsync(sync.CurrentBranchId))
            Ratings.Add($"{doctors.GetValueOrDefault(r.DoctorId, "Doctor")}: {r.Overall:0.0}★ overall" + (r.Doctor is { } d ? $", {d:0.0}★ doctor" : "") + $" ({r.Count})");
    });

    [RelayCommand]
    private async Task RaiseAsync()
    {
        var subject = await Ui.PromptAsync("Complaint", "Subject");
        if (string.IsNullOrWhiteSpace(subject)) return;
        var details = await Ui.PromptAsync("Complaint", "Details (optional)");
        var categories = (await sync.Store.ListAsync<LocalLookupItem>(i => i.ListKey == "complaint_categories" && i.IsActive)).OrderBy(i => i.SortOrder).ToList();
        var pick = await CareUi.PickAsync("Category", categories.Select(c => c.LabelEn));
        await RunAsync(async () =>
        {
            await sync.Feedback.RaiseAsync(sync.CurrentBranchId, subject, details, categories.FirstOrDefault(c => c.LabelEn == pick)?.Code);
            await LoadAsync();
        });
    }

    [RelayCommand]
    private async Task WorkAsync(CareRow row)
    {
        if (!CanManage || row.Tag is not LocalComplaint c || c.Status == ComplaintStatus.Closed) return;
        var options = new List<string> { "Assign" };
        if (c.Status != ComplaintStatus.Resolved) options.Add("Resolve");
        if (c.Status == ComplaintStatus.Resolved) options.Add("Close");
        var pick = await CareUi.PickAsync(c.Subject, options);
        if (pick is null) return;
        string? text = pick switch
        {
            "Assign" => await Ui.PromptAsync("Assign", "Who is handling it?", session.Me?.FullName),
            "Resolve" => await Ui.PromptAsync("Resolve", "What was done?"),
            _ => "",
        };
        if (text is null) return;
        await RunAsync(async () =>
        {
            if (pick == "Assign") await sync.Feedback.AssignAsync(c.Id, null, text);
            else if (pick == "Resolve") await sync.Feedback.ResolveAsync(c.Id, text);
            else await sync.Feedback.CloseAsync(c.Id);
            await LoadAsync();
        });
    }
}
