using System.Collections.ObjectModel;
using Clinic.App.Services;
using Clinic.Client.Core.Clinical;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Clinic.Shared.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

internal static class LabUi
{
    /// <summary>Pick tests by ticking them in a repeating action sheet (works on every platform, offline).</summary>
    public static async Task<IReadOnlyList<LocalLabTest>> PickTestsAsync(SyncCoordinator sync)
    {
        var tests = await sync.Labs.TestsAsync();
        var ticked = new bool[tests.Count];
        while (true)
        {
            var options = tests.Select((t, i) => $"{(ticked[i] ? "☑" : "☐")} {t.Name}" + (t.Fee is { } f ? $" · Rs {f:N0}" : "")).ToArray();
            var pick = await Shell.Current.DisplayActionSheetAsync("Tests to order — tap to tick", "Cancel", $"Order {ticked.Count(x => x)} test(s)", options);
            if (pick is null or "Cancel") return [];
            if (pick.StartsWith("Order ")) return tests.Where((_, i) => ticked[i]).ToList();
            var index = Array.IndexOf(options, pick);
            if (index >= 0) ticked[index] = !ticked[index];
        }
    }

    /// <summary>Orders tests for a patient (optionally from a consultation) and returns the order.</summary>
    public static async Task<LocalLabOrder?> OrderAsync(SyncCoordinator sync, SessionStore session, Guid patientId, Guid? encounterId)
    {
        var tests = await PickTestsAsync(sync);
        if (tests.Count == 0) return null;
        var urgent = await Shell.Current.DisplayActionSheetAsync("Priority", "Cancel", null, "Routine", "Urgent");
        if (urgent is null or "Cancel") return null;
        var notes = await Ui.PromptAsync("Clinical notes for the lab", "Optional");
        var me = await CareUi.MyDoctorAsync(sync, session);
        return await sync.Labs.OrderAsync(patientId, sync.CurrentBranchId, tests.Select(t => t.Id).ToList(), encounterId, me?.Id,
            urgent == "Urgent" ? LabPriority.Urgent : LabPriority.Routine, notes);
    }

    public static async Task PrintReportAsync(SyncCoordinator sync, SessionStore session, Guid orderId)
    {
        var report = await sync.Labs.BuildReportAsync(orderId, CareUi.Clinic(session), CareUi.BranchLine(sync, session));
        await CareUi.OpenPdfAsync("Lab report", ClinicLetterPdf.Render(report));
    }
}

// =====================================================================================================
// §4.5 the lab's work list and one test
// =====================================================================================================

public sealed partial class LabWorkViewModel(SyncCoordinator sync) : ViewModelBase
{
    public ObservableCollection<CareRow> Items { get; } = [];
    public ObservableCollection<string> Filters { get; } = ["To collect", "To result", "To verify", "Done today"];

    [ObservableProperty] public partial int FilterIndex { get; set; }
    partial void OnFilterIndexChanged(int value) => LoadCommand.Execute(null);

    [RelayCommand]
    public Task LoadAsync() => RunAsync(async () =>
    {
        var status = FilterIndex switch { 0 => LabStatus.Ordered, 1 => LabStatus.Collected, 2 => LabStatus.Resulted, _ => LabStatus.Verified };
        var patients = (await sync.Store.ListAsync<LocalPatient>()).ToDictionary(p => p.Id);
        var orders = (await sync.Store.ListAsync<LocalLabOrder>()).ToDictionary(o => o.Id);
        Items.Clear();
        foreach (var r in await sync.Labs.WorkListAsync(sync.CurrentBranchId, status))
        {
            if (status == LabStatus.Verified && r.VerifiedAt?.ToLocalTime().Date != DateTime.Today) continue;
            var o = orders.GetValueOrDefault(r.OrderId);
            var p = patients.GetValueOrDefault(r.PatientId);
            Items.Add(new CareRow(r.Id, $"{r.TestName} · {p?.FullName}",
                $"{p?.Mrn} · {r.SampleType} · ordered {o?.OrderedAt.ToLocalTime():dd MMM HH:mm}" + (r.SampleNo is null ? "" : $" · {r.SampleNo}") +
                (o?.Priority == LabPriority.Urgent ? " · URGENT" : "") + (r.IsAbnormal ? " · abnormal" : ""), o?.Priority == LabPriority.Urgent || r.IsAbnormal));
        }
    });

    [RelayCommand]
    private Task OpenAsync(CareRow row) => Ui.GoToAsync(Routes.LabResult, new Dictionary<string, object> { ["resultId"] = row.Id });
}

/// <summary>FR-LAB-02: collect, enter results in the test's form (flags live), verify, attach an outside report.</summary>
public sealed partial class LabResultViewModel(SyncCoordinator sync, SessionStore session, DocumentScanner scanner) : ViewModelBase, IQueryAttributable
{
    private Guid _resultId;
    private LocalLabResult? _result;

    [ObservableProperty] public partial string Title { get; set; } = "Lab test";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial DynamicFormModel? ResultModel { get; set; }
    [ObservableProperty] public partial string? Comment { get; set; }
    [ObservableProperty] public partial string? SampleNo { get; set; }
    [ObservableProperty] public partial bool CanCollect { get; set; }
    [ObservableProperty] public partial bool CanEnter { get; set; }
    [ObservableProperty] public partial bool CanVerify { get; set; }
    [ObservableProperty] public partial bool CanCancel { get; set; }
    public bool CanScan => scanner.IsSupported;

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _ = RunAsync(async () =>
    {
        if (query.TryGetValue("resultId", out var r) && r is Guid id) _resultId = id;
        await ReloadAsync();
    });

    private async Task ReloadAsync()
    {
        var r = _result = await sync.Store.GetAsync<LocalLabResult>(_resultId) ?? throw new InvalidOperationException("Test not found.");
        var patient = await sync.Store.GetAsync<LocalPatient>(r.PatientId);
        var order = await sync.Store.GetAsync<LocalLabOrder>(r.OrderId);
        Title = r.TestName;
        Subtitle = $"{patient?.FullName} · {patient?.Gender} · MRN {patient?.Mrn}" + (order?.ClinicalNotes is { } n ? $"\nNotes: {n}" : "");
        StatusText = LabStatus.Display(r.Status) + (r.SampleNo is null ? "" : $" · sample {r.SampleNo}") + (r.IsAbnormal ? " · abnormal" : "");
        Comment = r.Comment;
        SampleNo = r.SampleNo;
        var canWork = session.Has(Permissions.Labs.EnterResults);
        CanCollect = canWork && r.Status == LabStatus.Ordered;
        CanEnter = canWork && r.Status is LabStatus.Collected or LabStatus.Resulted;
        CanVerify = session.Has(Permissions.Labs.Verify) && r.Status == LabStatus.Resulted;
        CanCancel = (canWork || session.Has(Permissions.Labs.Order)) && r.Status is LabStatus.Ordered or LabStatus.Collected;

        ResultModel = null;
        if (r.FormVersionId is { } v && await sync.Forms.GetVersionAsync(v) is { } form && r.Status != LabStatus.Ordered)
        {
            var values = await sync.Forms.LoadValuesAsync(SyncEntityNames.LabResult, r.Id);
            ResultModel = new DynamicFormModel(form, values, LabService.ContextFor(r, session.Me?.Roles), await sync.Forms.LoadLookupsAsync(form.Definition),
                isNewRecord: !LabStatus.IsLocked(r.Status));
        }
    }

    [RelayCommand]
    private Task CollectAsync() => RunAsync(async () => { await sync.Labs.CollectAsync(_resultId, SampleNo); await ReloadAsync(); });

    [RelayCommand]
    private Task SaveResultsAsync() => RunAsync(async () =>
    {
        if (ResultModel is not { } m) return;
        if (!m.Validate()) throw new InvalidOperationException("Check the highlighted values.");
        await sync.Labs.EnterResultsAsync(_resultId, m.ValuesToSave(), Comment);
        await ReloadAsync();
    });

    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (ResultModel is { IsDirty: true }) { await Ui.AlertAsync("Verify", "Save the results first."); return; }
        if (!await Ui.ConfirmAsync("Verify", "Verified results are locked and released to the doctor.", "Verify")) return;
        await RunAsync(async () => { await sync.Labs.VerifyAsync(_resultId); await ReloadAsync(); });
    }

    [RelayCommand]
    private async Task CancelTestAsync()
    {
        var reason = await Ui.PromptAsync("Cancel test", "Reason (e.g. sample haemolysed, patient left)");
        if (string.IsNullOrWhiteSpace(reason)) return;
        await RunAsync(async () => { await sync.Labs.CancelAsync(_resultId, reason); await ReloadAsync(); });
    }

    /// <summary>FR-AI-21: read an analyser printout into the result form (ticked values only).</summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (ResultModel is not { } m) return;
        var n = await OcrReview.ScanIntoAsync(scanner, m);
        if (n > 0) await Ui.AlertAsync("Scan", $"{n} value(s) filled in. Check them, then save.");
    }

    /// <summary>FR-LAB-03: attach an outside lab's report (photo or PDF) to this test.</summary>
    [RelayCommand]
    private async Task AttachAsync()
    {
        if (_result is null) return;
        FileResult? file;
        try { file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Outside report (photo or PDF)" }); }
        catch (Exception ex) { await Ui.AlertAsync("Attach", ex.Message); return; }
        if (file is null) return;
        await RunAsync(async () =>
        {
            await using var stream = await file.OpenReadAsync();
            var type = file.ContentType ?? (file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "image/jpeg");
            await sync.Attachments.AddAsync(SyncEntityNames.LabResult, _result.Id, _result.BranchId, stream, file.FileName, type, $"Outside report · {_result.TestName}", "lab_report");
        });
        await Ui.AlertAsync("Attach", "The report is attached and will upload with the next sync.");
    }

    [RelayCommand]
    private Task ReportAsync() => _result is null ? Task.CompletedTask : RunAsync(() => LabUi.PrintReportAsync(sync, session, _result.OrderId));
}

// =====================================================================================================
// §4.6 billing for one patient, one invoice, and the cash closing
// =====================================================================================================

public sealed partial class ChargeRow(ChargeCandidate charge) : ObservableObject
{
    public ChargeCandidate Charge { get; } = charge;
    public string Text => $"{Charge.Description} — Rs {Charge.UnitPrice * Charge.Quantity:N0}";
    [ObservableProperty] public partial bool Selected { get; set; } = true;
}

public sealed partial class PatientBillingViewModel(SyncCoordinator sync) : ViewModelBase, IQueryAttributable
{
    private Guid _patientId;

    public ObservableCollection<ChargeRow> Unbilled { get; } = [];
    public ObservableCollection<CareRow> Invoices { get; } = [];
    public ObservableCollection<CareRow> Packages { get; } = [];
    public ObservableCollection<CareRow> Panels { get; } = [];

    [ObservableProperty] public partial string PatientName { get; set; } = "";
    [ObservableProperty] public partial string BalanceText { get; set; } = "";

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("patientId", out var p) && p is Guid id) _patientId = id;
        LoadCommand.Execute(null);
    }

    [RelayCommand]
    public Task LoadAsync() => RunAsync(async () =>
    {
        if (_patientId == Guid.Empty) return;
        PatientName = (await sync.Store.GetAsync<LocalPatient>(_patientId))?.FullName ?? "";
        var balance = await sync.Billing.BalanceAsync(_patientId);
        BalanceText = balance.Balance switch
        {
            > 0 => $"Owes Rs {balance.Balance:N0}",
            < 0 => $"Credit Rs {-balance.Balance:N0}",
            _ => "Nothing owed",
        } + $" · invoiced Rs {balance.Invoiced:N0} · paid Rs {balance.Paid - balance.Refunded + balance.Deposits:N0}";
        if (balance.Credit > 0) BalanceText += $" · advance credit Rs {balance.Credit:N0}";
        Unbilled.Clear();
        foreach (var c in await sync.Billing.UnbilledAsync(_patientId)) Unbilled.Add(new ChargeRow(c));
        Packages.Clear();
        foreach (var p in await sync.Billing.PackagesAsync(_patientId))
            Packages.Add(new CareRow(p.Package.Id, $"{p.Package.Name} · {p.Package.Status}",
                string.Join(" · ", p.Items.Select(i => $"{i.Item.Kind}{(i.Item.Code is null ? "" : " " + i.Item.Code)}: {i.Used}/{i.Item.Count} used")) +
                $" · until {p.Package.ExpiresAt.ToLocalTime():dd MMM yyyy}", !p.IsUsable(DateTime.UtcNow)));
        Panels.Clear();
        foreach (var p in await sync.Billing.PanelsAsync(_patientId))
            Panels.Add(new CareRow(p.Panel.Id, $"{p.Payer.Name} · {p.Panel.MemberNo}", $"co-pay {p.CoPay:0.#}%" + (p.Panel.ValidTo is { } t ? $" · valid to {t:dd MMM yyyy}" : "")));
        Invoices.Clear();
        foreach (var i in await sync.Billing.ForPatientAsync(_patientId))
        {
            var s = await sync.Billing.SummaryAsync(i.Id);
            Invoices.Add(new CareRow(i.Id, $"{i.InvoiceNo} · Rs {i.Total:N0}", $"{i.Status} · {i.IssuedAt.ToLocalTime():dd MMM yyyy}" +
                (i.Status == InvoiceStatus.Final ? (s.Due > 0 ? $" · due Rs {s.Due:N0}" : " · paid") : ""), s.Due > 0 && i.Status == InvoiceStatus.Final));
        }
    });

    [RelayCommand]
    private Task CreateInvoiceAsync() => RunAsync(async () =>
    {
        var invoice = await sync.Billing.CreateInvoiceAsync(_patientId, sync.CurrentBranchId, Unbilled.Where(c => c.Selected).Select(c => c.Charge));
        await Ui.GoToAsync(Routes.Invoice, new Dictionary<string, object> { ["invoiceId"] = invoice.Id });
    });

    [RelayCommand]
    private async Task DepositAsync()
    {
        var amount = await Shell.Current.DisplayPromptAsync("Advance deposit", "Amount (Rs)", "Next", "Cancel", keyboard: Keyboard.Numeric);
        if (!decimal.TryParse(amount, out var a)) return;
        var method = await PaymentUi.PickMethodAsync(sync);
        if (method is null) return;
        await RunAsync(async () => { await sync.Billing.DepositAsync(_patientId, sync.CurrentBranchId, a, method); await LoadAsync(); });
    }

    [RelayCommand]
    private Task OpenInvoiceAsync(CareRow row) => Ui.GoToAsync(Routes.Invoice, new Dictionary<string, object> { ["invoiceId"] = row.Id });

    /// <summary>FR-BIL-10: sell a package; its invoice opens for payment.</summary>
    [RelayCommand]
    private async Task SellPackageAsync()
    {
        var plans = await sync.Billing.PlansAsync();
        var pick = await CareUi.PickAsync("Package", plans.Select(p => $"{p.Name} · Rs {p.Price:N0}"));
        var plan = plans.FirstOrDefault(p => $"{p.Name} · Rs {p.Price:N0}" == pick);
        if (plan is null) return;
        var openCase = (await sync.Cases.ForPatientAsync(_patientId, openOnly: true)).FirstOrDefault();
        LocalInvoice? invoice = null;
        if (await RunAsync(async () => invoice = (await sync.Billing.SellPackageAsync(_patientId, sync.CurrentBranchId, plan.Id, openCase?.Id)).Invoice))
            await Ui.GoToAsync(Routes.Invoice, new Dictionary<string, object> { ["invoiceId"] = invoice!.Id });
    }

    /// <summary>FR-BIL-12: record the patient's company panel or insurance.</summary>
    [RelayCommand]
    private async Task AddPanelAsync()
    {
        var payers = await sync.Store.ListAsync<LocalPayer>(p => p.IsActive);
        var pick = await CareUi.PickAsync("Panel / insurer", payers.Select(p => p.Name));
        var payer = payers.FirstOrDefault(p => p.Name == pick);
        if (payer is null) return;
        var member = await Ui.PromptAsync(payer.Name, "Member / policy / employee number");
        if (string.IsNullOrWhiteSpace(member)) return;
        await RunAsync(async () => { await sync.Billing.AddPanelAsync(_patientId, payer.Id, member); await LoadAsync(); });
    }
}

internal static class PaymentUi
{
    public static async Task<string?> PickMethodAsync(SyncCoordinator sync)
    {
        var methods = (await sync.Store.ListAsync<LocalLookupItem>(i => i.ListKey == "payment_methods" && i.IsActive)).OrderBy(i => i.SortOrder).ToList();
        var pick = await CareUi.PickAsync("Paid by", methods.Select(m => m.LabelEn));
        return methods.FirstOrDefault(m => m.LabelEn == pick)?.Code;
    }
}

/// <summary>FR-BIL-02…05: one invoice — lines, discounts, finalise, payments, refunds, void, receipt.</summary>
public sealed partial class InvoiceViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase, IQueryAttributable
{
    private Guid _invoiceId;
    private InvoiceSummary? _summary;

    public ObservableCollection<CareRow> Lines { get; } = [];
    public ObservableCollection<CareRow> Payments { get; } = [];

    [ObservableProperty] public partial string Title { get; set; } = "Invoice";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string TotalsText { get; set; } = "";
    [ObservableProperty] public partial bool IsDraft { get; set; }
    [ObservableProperty] public partial bool IsFinal { get; set; }
    [ObservableProperty] public partial bool HasDue { get; set; }

    public bool CanDiscount => IsDraft && session.Has(Permissions.Billing.Discount);
    public ObservableCollection<CareRow> Credits { get; } = [];
    [ObservableProperty] public partial string? PanelText { get; set; }
    [ObservableProperty] public partial bool HasCredit { get; set; }
    public bool CanRefund => session.Has(Permissions.Billing.Refund) && _summary is { } s && s.Paid - s.Refunded > 0;
    partial void OnIsDraftChanged(bool value) => OnPropertyChanged(nameof(CanDiscount));

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _ = RunAsync(async () =>
    {
        if (query.TryGetValue("invoiceId", out var i) && i is Guid id) _invoiceId = id;
        await ReloadAsync();
    });

    private async Task ReloadAsync()
    {
        var s = _summary = await sync.Billing.SummaryAsync(_invoiceId);
        var inv = s.Invoice;
        var patient = await sync.Store.GetAsync<LocalPatient>(inv.PatientId);
        Title = $"Invoice {inv.InvoiceNo}";
        Subtitle = $"{patient?.FullName} · {inv.Status}" + (inv.VoidReason is { } v ? $" · {v}" : "");
        IsDraft = inv.Status == InvoiceStatus.Draft;
        IsFinal = inv.Status == InvoiceStatus.Final;
        HasDue = inv.Status != InvoiceStatus.Void && s.Due > 0;
        TotalsText = $"Subtotal Rs {inv.Subtotal:N0}" + (inv.Discount > 0 ? $" · discount Rs {inv.Discount:N0}" : "") + (inv.Tax > 0 ? $" · tax Rs {inv.Tax:N0}" : "") +
                     $"\nTotal Rs {inv.Total:N0} · paid Rs {s.Paid - s.Refunded:N0} · due Rs {s.Due:N0}";
        Lines.Clear();
        foreach (var l in s.Lines)
            Lines.Add(new CareRow(l.Id, l.Description, $"{l.Quantity:0.##} × Rs {l.UnitPrice:N0}" + (l.Discount > 0 ? $" − Rs {l.Discount:N0}" : "") +
                (l.TaxPercent > 0 ? $" + {l.TaxPercent:0.#}% tax" : "") + $" = Rs {l.Amount:N0}"));
        PanelText = null;
        if (inv.PayerId is { } payerId && await sync.Store.GetAsync<LocalPayer>(payerId) is { } payer)
            PanelText = $"Billed to {payer.Name}: Rs {inv.PayerAmount:N0} · patient pays Rs {inv.PatientAmount:N0}";
        HasCredit = IsFinal && HasDue && (await sync.Billing.BalanceAsync(inv.PatientId)).Credit > 0;
        Credits.Clear();
        foreach (var c in s.Credits)
            Credits.Add(new CareRow(c.Id, $"{(c.Kind == CreditKind.WriteOff ? "Write-off" : "Credit note")} · Rs {c.Amount:N0} · {c.Status}", c.Reason, c.Status == ApprovalStatus.Rejected));
        Payments.Clear();
        foreach (var p in s.Payments)
            Payments.Add(new CareRow(p.Id, $"{p.Kind} · Rs {p.Amount:N0} · {p.Method}", $"{p.ReceivedAt.ToLocalTime():dd MMM HH:mm}" +
                (p.ReceiptNo is null ? "" : $" · {p.ReceiptNo}") + (p.Reason is null ? "" : $" · {p.Reason}"), p.Kind == PaymentKind.Refund));
        OnPropertyChanged(nameof(CanRefund));
    }

    [RelayCommand]
    private async Task AddServiceAsync()
    {
        var services = await sync.Billing.ServicesAsync(sync.CurrentBranchId);
        var pick = await CareUi.PickAsync("Add a service", services.Select(s => $"{s.Name} · Rs {s.Price:N0}"));
        var service = services.FirstOrDefault(s => $"{s.Name} · Rs {s.Price:N0}" == pick);
        if (service is null) return;
        await RunAsync(async () => { await sync.Billing.AddServiceAsync(_invoiceId, service.Id); await ReloadAsync(); });
    }

    [RelayCommand]
    private async Task AddUnbilledAsync()
    {
        if (_summary is null) return;
        var charges = await sync.Billing.UnbilledAsync(_summary.Invoice.PatientId);
        var pick = await CareUi.PickAsync("Add a charge", charges.Select(c => $"{c.Description} · Rs {c.UnitPrice:N0}"));
        var charge = charges.FirstOrDefault(c => $"{c.Description} · Rs {c.UnitPrice:N0}" == pick);
        if (charge is null) return;
        await RunAsync(async () => { await sync.Billing.AddChargeAsync(_invoiceId, charge); await ReloadAsync(); });
    }

    [RelayCommand]
    private async Task AddManualAsync()
    {
        var text = await Ui.PromptAsync("Manual charge", "Description");
        if (string.IsNullOrWhiteSpace(text)) return;
        var price = await Shell.Current.DisplayPromptAsync("Manual charge", "Price (Rs)", "Add", "Cancel", keyboard: Keyboard.Numeric);
        if (!decimal.TryParse(price, out var p)) return;
        await RunAsync(async () => { await sync.Billing.AddManualAsync(_invoiceId, text, p); await ReloadAsync(); });
    }

    [RelayCommand]
    private async Task LineActionAsync(CareRow row)
    {
        if (!IsDraft) return;
        var options = CanDiscount ? new[] { "Remove", "Discount on this line" } : ["Remove"];
        var pick = await CareUi.PickAsync(row.Title, options);
        if (pick == "Remove") await RunAsync(async () => { await sync.Billing.RemoveLineAsync(row.Id); await ReloadAsync(); });
        else if (pick is not null)
        {
            var amount = await Shell.Current.DisplayPromptAsync("Line discount", "Amount (Rs)", "Apply", "Cancel", keyboard: Keyboard.Numeric);
            if (decimal.TryParse(amount, out var a)) await RunAsync(async () => { await sync.Billing.SetLineDiscountAsync(row.Id, a); await ReloadAsync(); });
        }
    }

    [RelayCommand]
    private async Task DiscountAsync()
    {
        var reasons = (await sync.Store.ListAsync<LocalLookupItem>(i => i.ListKey == "discount_reasons" && i.IsActive)).OrderBy(i => i.SortOrder).ToList();
        var reason = await CareUi.PickAsync("Discount reason", reasons.Select(r => r.LabelEn));
        if (reason is null) return;
        var amount = await Shell.Current.DisplayPromptAsync("Discount", "Amount off the invoice (Rs)", "Apply", "Cancel", keyboard: Keyboard.Numeric);
        if (!decimal.TryParse(amount, out var a)) return;
        await RunAsync(async () => { await sync.Billing.SetInvoiceDiscountAsync(_invoiceId, a, reason); await ReloadAsync(); });
    }

    [RelayCommand]
    private Task FinaliseAsync() => RunAsync(async () => { await sync.Billing.FinaliseAsync(_invoiceId); await ReloadAsync(); });

    /// <summary>FR-BIL-12: bill this draft to one of the patient's panels (or back to the patient).</summary>
    [RelayCommand]
    private async Task PanelAsync()
    {
        if (_summary is null) return;
        var panels = await sync.Billing.PanelsAsync(_summary.Invoice.PatientId);
        if (panels.Count == 0) { await Ui.AlertAsync("Panel", "Add the patient's panel on the Billing page first."); return; }
        var options = panels.Select(p => $"{p.Payer.Name} · {p.Panel.MemberNo}").Append("Patient pays (no panel)").ToList();
        var pick = await CareUi.PickAsync("Bill to", options);
        if (pick is null) return;
        var panel = panels.FirstOrDefault(p => $"{p.Payer.Name} · {p.Panel.MemberNo}" == pick);
        await RunAsync(async () => { await sync.Billing.SetPanelAsync(_invoiceId, panel?.Panel.Id); await ReloadAsync(); });
    }

    /// <summary>FR-BIL-13: settle from the patient's advance credit.</summary>
    [RelayCommand]
    private Task ApplyCreditAsync() => RunAsync(async () => { await sync.Billing.ApplyCreditAsync(_invoiceId); await ReloadAsync(); });

    /// <summary>FR-BIL-14: ask for a credit note or write-off; someone with Billing.Approve decides.</summary>
    [RelayCommand]
    private async Task RequestCreditAsync()
    {
        var kind = await CareUi.PickAsync("Request", ["Credit note", "Write-off"]);
        if (kind is null) return;
        var amount = await Shell.Current.DisplayPromptAsync(kind, "Amount (Rs)", "Next", "Cancel", initialValue: _summary?.Due.ToString("0"), keyboard: Keyboard.Numeric);
        if (!decimal.TryParse(amount, out var a)) return;
        var reason = await Ui.PromptAsync(kind, "Reason");
        if (string.IsNullOrWhiteSpace(reason)) return;
        await RunAsync(async () =>
        {
            await sync.Billing.RequestCreditAsync(_invoiceId, kind == "Write-off" ? CreditKind.WriteOff : CreditKind.CreditNote, a, reason);
            await ReloadAsync();
        });
    }

    [RelayCommand]
    private async Task PayAsync()
    {
        if (_summary is null) return;
        var amount = await Shell.Current.DisplayPromptAsync("Payment", "Amount (Rs)", "Next", "Cancel", initialValue: _summary.Due.ToString("0"), keyboard: Keyboard.Numeric);
        if (!decimal.TryParse(amount, out var a)) return;
        var method = await PaymentUi.PickMethodAsync(sync);
        if (method is null) return;
        var reference = method == PaymentMethods.Cash ? null : await Ui.PromptAsync("Reference", "Card slip / transaction number (optional)");
        await RunAsync(async () => { await sync.Billing.PayAsync(_invoiceId, a, method, reference); await ReloadAsync(); });
        if (!HasDue && await Ui.ConfirmAsync("Paid", "Print the receipt?", "Print")) await ReceiptAsync();
    }

    [RelayCommand]
    private async Task RefundAsync()
    {
        if (_summary is null) return;
        var amount = await Shell.Current.DisplayPromptAsync("Refund", "Amount (Rs)", "Next", "Cancel", keyboard: Keyboard.Numeric);
        if (!decimal.TryParse(amount, out var a)) return;
        var reason = await Ui.PromptAsync("Refund", "Reason");
        if (string.IsNullOrWhiteSpace(reason)) return;
        var method = await PaymentUi.PickMethodAsync(sync) ?? PaymentMethods.Cash;
        await RunAsync(async () =>
        {
            await sync.Billing.RefundAsync(_summary.Invoice.PatientId, _summary.Invoice.BranchId, _invoiceId, a, reason, method);
            await ReloadAsync();
        });
    }

    [RelayCommand]
    private async Task VoidAsync()
    {
        var reason = await Ui.PromptAsync("Void invoice", "Reason");
        if (string.IsNullOrWhiteSpace(reason)) return;
        await RunAsync(async () => { await sync.Billing.VoidAsync(_invoiceId, reason); await ReloadAsync(); });
    }

    [RelayCommand]
    private Task ReceiptAsync() => RunAsync(async () =>
    {
        var doc = await sync.Billing.BuildReceiptAsync(_invoiceId, CareUi.Clinic(session), CareUi.BranchLine(sync, session));
        await CareUi.OpenPdfAsync("Receipt", ClinicLetterPdf.Render(doc));
    });
}

/// <summary>FR-BIL-06: today's takings by method (mine and the branch), count the drawer, close.</summary>
public sealed partial class CashClosingViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase
{
    public ObservableCollection<string> MyMethods { get; } = [];
    public ObservableCollection<CareRow> Closings { get; } = [];

    [ObservableProperty] public partial DateTime Date { get; set; } = DateTime.Today;
    [ObservableProperty] public partial string MySummary { get; set; } = "";
    [ObservableProperty] public partial string BranchSummary { get; set; } = "";
    [ObservableProperty] public partial string? Counted { get; set; }
    [ObservableProperty] public partial string? Notes { get; set; }
    partial void OnDateChanged(DateTime value) => LoadCommand.Execute(null);

    [RelayCommand]
    public Task LoadAsync() => RunAsync(async () =>
    {
        var me = session.Me?.UserId;
        var mine = await sync.Billing.DaySummaryAsync(sync.CurrentBranchId, Date, me);
        var branch = await sync.Billing.DaySummaryAsync(sync.CurrentBranchId, Date);
        MySummary = $"My takings: Rs {mine.Net:N0} ({mine.Count} entries) · cash in drawer should be Rs {mine.Cash:N0}";
        BranchSummary = $"Branch: Rs {branch.Net:N0} received net ({branch.Count} entries)" +
                        (sync.Outstanding > 0 ? " · this device has unsynced changes" : " · as of last sync");
        MyMethods.Clear();
        foreach (var m in mine.Methods) MyMethods.Add($"{m.Method}: Rs {m.Received:N0}" + (m.Refunded > 0 ? $" − refunds Rs {m.Refunded:N0}" : "") + $" = Rs {m.Net:N0}");
        Counted ??= mine.Cash.ToString("0");
        Closings.Clear();
        foreach (var c in (await sync.Store.ListAsync<LocalCashClosing>(c => c.UserId == me)).OrderByDescending(c => c.ClosedAt).Take(14))
        {
            var diff = c.CountedCash - c.ExpectedCash;
            var server = c.SyncStatus == SyncStatus.Synced ? (c.Mismatch ? $" · ⚠ server expected Rs {c.ServerExpectedCash:N0}" : " · ✔ matches server") : " · not synced yet";
            Closings.Add(new CareRow(c.Id, $"{c.BusinessDate:ddd dd MMM} · counted Rs {c.CountedCash:N0}",
                $"expected Rs {c.ExpectedCash:N0} · {(diff == 0 ? "exact" : diff > 0 ? $"over Rs {diff:N0}" : $"short Rs {-diff:N0}")}{server}", c.Mismatch || diff != 0));
        }
    });

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (!decimal.TryParse(Counted, out var counted)) { ErrorMessage = "Enter the cash you counted."; return; }
        if (!await Ui.ConfirmAsync("Close cash", $"Close {Date:dd MMM} with Rs {counted:N0} counted?", "Close")) return;
        await RunAsync(async () =>
        {
            await sync.Billing.CloseCashAsync(sync.CurrentBranchId, Date, counted, Notes);
            Notes = null;
            sync.RequestSync();
            await LoadAsync();
        });
    }
}
