using System.Collections.ObjectModel;
using Clinic.App.Services;
using Clinic.Client.Core.Clinical;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Reports;
using Clinic.Shared.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

// =====================================================================================================
// §4.7 pharmacy stock
// =====================================================================================================

public sealed partial class StockViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase
{
    public ObservableCollection<CareRow> Items { get; } = [];
    public ObservableCollection<CareRow> Alerts { get; } = [];

    [ObservableProperty] public partial string? Search { get; set; }
    [ObservableProperty] public partial bool OnlyAlerts { get; set; }
    partial void OnSearchChanged(string? value) => LoadCommand.Execute(null);
    partial void OnOnlyAlertsChanged(bool value) => LoadCommand.Execute(null);

    public bool CanReceive => session.Has(Permissions.Inventory.StockIn);

    [RelayCommand]
    public Task LoadAsync() => RunAsync(async () =>
    {
        var stock = await sync.Inventory.StockAsync(sync.CurrentBranchId, OnlyAlerts);
        Alerts.Clear();
        foreach (var a in stock.SelectMany(s => s.Alerts).OrderBy(a => a.Kind))
            Alerts.Add(new CareRow(a.ItemId, a.ItemName, a.Message, a.Kind is StockAlertKind.Expired or StockAlertKind.Negative));
        Items.Clear();
        foreach (var s in stock.Where(s => string.IsNullOrWhiteSpace(Search) || s.Item.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)))
        {
            if (!OnlyAlerts && s.Batches.Count == 0 && string.IsNullOrWhiteSpace(Search)) continue; // not carried here
            Items.Add(new CareRow(s.Item.Id, $"{s.Item.Name} · {s.OnHand:0.##} {s.Item.Unit}(s)",
                string.Join(" · ", s.Batches.Where(b => b.OnHand != 0).Select(b => $"{b.BatchNo} {b.OnHand:0.##}" + (b.Expiry is { } e ? $" exp {e:MMM yy}" : ""))) +
                $" · Rs {s.Item.SalePrice:N0}", s.Alerts.Count > 0, s));
        }
    });

    /// <summary>FR-INV-01 stock in: a batch with number, expiry, cost and supplier.</summary>
    [RelayCommand]
    private async Task ReceiveAsync()
    {
        var items = await sync.Inventory.ItemsAsync(Search);
        var pick = await CareUi.PickAsync("Item received", items.Take(40).Select(i => i.Name));
        var item = items.FirstOrDefault(i => i.Name == pick);
        if (item is null) return;
        var batch = await Ui.PromptAsync(item.Name, "Batch number");
        if (string.IsNullOrWhiteSpace(batch)) return;
        var qty = await Shell.Current.DisplayPromptAsync(item.Name, $"Quantity ({item.Unit}s)", "Next", "Cancel", keyboard: Keyboard.Numeric);
        if (!decimal.TryParse(qty, out var q)) return;
        var months = await Shell.Current.DisplayPromptAsync(item.Name, "Expiry: months from now (blank if none)", "Next", "Cancel", keyboard: Keyboard.Numeric);
        var cost = await Shell.Current.DisplayPromptAsync(item.Name, "Cost per unit (Rs)", "Next", "Cancel", initialValue: "0", keyboard: Keyboard.Numeric);
        var supplier = await Ui.PromptAsync(item.Name, "Supplier / invoice no. (optional)");
        await RunAsync(async () =>
        {
            DateTime? expiry = int.TryParse(months, out var m) ? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(m + 1).AddDays(-1) : null;
            await sync.Inventory.ReceiveAsync(item.Id, sync.CurrentBranchId, batch, q, expiry, decimal.TryParse(cost, out var c) ? c : 0, supplier);
            await LoadAsync();
        });
    }

    /// <summary>Batch actions: count (adjust), write off, transfer to another branch.</summary>
    [RelayCommand]
    private async Task ItemActionAsync(CareRow row)
    {
        if (row.Tag is not ItemStock s || s.Batches.Count == 0) return;
        var batchPick = await CareUi.PickAsync(s.Item.Name, s.Batches.Select(b => $"{b.BatchNo} · {b.OnHand:0.##}"));
        var batch = s.Batches.FirstOrDefault(b => $"{b.BatchNo} · {b.OnHand:0.##}" == batchPick);
        if (batch is null) return;
        var options = new List<string> { "Stock count" };
        if (batch.OnHand > 0) options.AddRange(["Write off (expired / damaged)", "Transfer to another branch"]);
        var action = await CareUi.PickAsync($"Batch {batch.BatchNo}", options);
        await RunAsync(async () =>
        {
            switch (action)
            {
                case "Stock count":
                    var counted = await Shell.Current.DisplayPromptAsync("Stock count", $"Counted {s.Item.Unit}s in batch {batch.BatchNo}", "Save", "Cancel",
                        initialValue: batch.OnHand.ToString("0.##"), keyboard: Keyboard.Numeric);
                    if (decimal.TryParse(counted, out var c)) await sync.Inventory.CountAsync(batch.BatchId, c, "Stock count");
                    break;
                case { } a when a.StartsWith("Write off"):
                    await sync.Inventory.WriteOffAsync(batch.BatchId, await Ui.PromptAsync("Write off", "Reason", "Expired") ?? "Expired");
                    break;
                case { } a when a.StartsWith("Transfer"):
                    var branches = (session.Me?.Branches ?? []).Where(b => b.Id != sync.CurrentBranchId).ToList();
                    var to = await CareUi.PickAsync("To branch", branches.Select(b => b.Name));
                    var target = branches.FirstOrDefault(b => b.Name == to);
                    var qty = await Shell.Current.DisplayPromptAsync("Transfer", "Quantity", "Transfer", "Cancel", keyboard: Keyboard.Numeric);
                    if (target is not null && decimal.TryParse(qty, out var q)) await sync.Inventory.TransferAsync(batch.BatchId, target.Id, q);
                    break;
                default: return;
            }
            await LoadAsync();
        });
    }
}

/// <summary>Dispensing for one patient: their open prescription lines, plus over-the-counter items.</summary>
public sealed partial class DispenseViewModel(SyncCoordinator sync) : ViewModelBase, IQueryAttributable
{
    private Guid _patientId;

    public ObservableCollection<CareRow> Lines { get; } = [];
    public ObservableCollection<CareRow> Dispensed { get; } = [];

    [ObservableProperty] public partial string PatientName { get; set; } = "";

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("patientId", out var p) && p is Guid id) _patientId = id;
        LoadCommand.Execute(null);
    }

    [RelayCommand]
    public Task LoadAsync() => RunAsync(async () =>
    {
        PatientName = (await sync.Store.GetAsync<LocalPatient>(_patientId))?.FullName ?? "";
        Lines.Clear();
        foreach (var e in (await sync.Encounters.ForPatientAsync(_patientId)).Take(5))
            foreach (var l in await sync.Inventory.PrescriptionAsync(e.Id, sync.CurrentBranchId))
            {
                if (l.AlreadyDispensed) continue;
                Lines.Add(new CareRow(l.RxItem.Id, $"{l.RxItem.DrugName} {l.RxItem.Strength} · give {l.Suggested:0.##}",
                    l.Item is null ? "Not stocked as an item — add it to the item list" : $"{l.Item.Name} · {l.OnHand:0.##} in stock · {e.StartedAt.ToLocalTime():dd MMM}",
                    l.Item is null || l.OnHand < l.Suggested, l));
            }
        Dispensed.Clear();
        foreach (var d in (await sync.Store.ListAsync<LocalDispensation>(x => x.PatientId == _patientId)).OrderByDescending(x => x.DispensedAt).Take(20))
            Dispensed.Add(new CareRow(d.Id, $"{d.ItemName} × {d.Quantity:0.##}", $"{d.DispensedAt.ToLocalTime():dd MMM HH:mm} · Rs {d.Quantity * d.UnitPrice:N0}"));
    });

    [RelayCommand]
    private async Task DispenseLineAsync(CareRow row)
    {
        if (row.Tag is not DispenseLine l || l.Item is null) return;
        var qty = await Shell.Current.DisplayPromptAsync(l.Item.Name, "Quantity to give", "Dispense", "Cancel", initialValue: l.Suggested.ToString("0.##"), keyboard: Keyboard.Numeric);
        if (!decimal.TryParse(qty, out var q)) return;
        await DispenseAsync(l.Item, q, l.RxItem.Id);
    }

    [RelayCommand]
    private async Task OverTheCounterAsync()
    {
        var term = await Ui.PromptAsync("Over the counter", "Item name");
        if (string.IsNullOrWhiteSpace(term)) return;
        var items = await sync.Inventory.ItemsAsync(term);
        var pick = await CareUi.PickAsync("Item", items.Take(30).Select(i => i.Name));
        var item = items.FirstOrDefault(i => i.Name == pick);
        if (item is null) return;
        var qty = await Shell.Current.DisplayPromptAsync(item.Name, "Quantity", "Dispense", "Cancel", initialValue: "1", keyboard: Keyboard.Numeric);
        if (decimal.TryParse(qty, out var q)) await DispenseAsync(item, q, null);
    }

    private async Task DispenseAsync(LocalInventoryItem item, decimal qty, Guid? rxItemId)
    {
        try
        {
            await sync.Inventory.DispenseAsync(_patientId, sync.CurrentBranchId, item.Id, qty, rxItemId);
        }
        catch (InventoryException ex) when (ex.Message.StartsWith("Only"))
        {
            // The shelf may hold stock not yet entered: dispense anyway and let the count fix it (shows as negative stock).
            if (!await Ui.ConfirmAsync("Low stock", $"{ex.Message} Dispense anyway?", "Dispense")) return;
            await RunAsync(() => sync.Inventory.DispenseAsync(_patientId, sync.CurrentBranchId, item.Id, qty, rxItemId, allowShort: true));
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        await LoadAsync();
    }

    [RelayCommand]
    private Task BillAsync() => Ui.GoToAsync(Routes.PatientBilling, new Dictionary<string, object> { ["patientId"] = _patientId });
}

// =====================================================================================================
// FR-REP-01…03 dashboard and reports
// =====================================================================================================

public sealed partial class ReportsViewModel(SyncCoordinator sync, SessionStore session) : ViewModelBase
{
    private ReportSummary? _summary;
    private FieldBreakdown? _breakdown;

    public ObservableCollection<string> Periods { get; } = ["Today", "Last 7 days", "This month", "Last month"];
    public ObservableCollection<string> Doctors { get; } = [];
    public ObservableCollection<string> Diagnoses { get; } = [];
    public ObservableCollection<string> Revenue { get; } = [];
    public ObservableCollection<string> Pending { get; } = [];
    public ObservableCollection<string> Stock { get; } = [];
    public ObservableCollection<string> GroupBy { get; } = [];
    public ObservableCollection<string> Breakdown { get; } = [];

    [ObservableProperty] public partial int PeriodIndex { get; set; }
    [ObservableProperty] public partial string Headline { get; set; } = "";
    [ObservableProperty] public partial string AsOf { get; set; } = "";
    [ObservableProperty] public partial int GroupByIndex { get; set; } = -1;
    partial void OnPeriodIndexChanged(int value) => LoadCommand.Execute(null);
    partial void OnGroupByIndexChanged(int value) => LoadCommand.Execute(null);

    public bool ShowMoney => session.Has(Permissions.Reports.Financial) || session.Has(Permissions.Billing.View);

    private (DateTime From, DateTime To) Period()
    {
        var t = DateTime.Today;
        var month = new DateTime(t.Year, t.Month, 1);
        return PeriodIndex switch { 1 => (t.AddDays(-6), t), 2 => (month, t), 3 => (month.AddMonths(-1), month.AddDays(-1)), _ => (t, t) };
    }

    private List<Clinic.Shared.Forms.FieldDefinition> _fields = [];

    [RelayCommand]
    public Task LoadAsync() => RunAsync(async () =>
    {
        var (from, to) = Period();
        var s = _summary = await sync.Reports.SummaryAsync(sync.CurrentBranchId, from, to);
        Headline = $"{s.NewPatients} new patient(s) · {s.Visits} visit(s)" + (ShowMoney ? $" · Rs {s.RevenueNet:N0} received" : "");
        AsOf = sync.Outstanding > 0 ? "As of last sync · this device has unsynced changes" : $"As of last sync · {sync.StatusText}";
        Fill(Doctors, s.VisitsByDoctor.Select(r => $"{r.Label}: {r.Count}"));
        Fill(Diagnoses, s.TopDiagnoses.Select(r => $"{r.Label}: {r.Count}"));
        Fill(Revenue, ShowMoney ? s.RevenueByMethod.Select(r => $"{r.Key}: Rs {r.Net:N0}" + (r.Refunded > 0 ? $" (refunds Rs {r.Refunded:N0})" : "")) : []);
        Fill(Pending, ShowMoney ? s.OldestPending.Select(d => $"{d.InvoiceNo} · Rs {d.Due:N0} · {d.IssuedAt.ToLocalTime():dd MMM}").Prepend($"{s.PendingInvoices} invoice(s), Rs {s.PendingAmount:N0} due") : []);
        Fill(Stock, s.StockAlerts.Select(r => $"{r.Label}: {r.Count}"));

        if (_fields.Count == 0)
        {
            _fields = await sync.Reports.ReportableFieldsAsync(SyncEntityNames.Patient, sync.CurrentBranchId);
            Fill(GroupBy, _fields.Select(f => $"Patients by {f.Label.En.ToLowerInvariant()}"));
        }
        _breakdown = null;
        Breakdown.Clear();
        if (GroupByIndex >= 0 && GroupByIndex < _fields.Count)
        {
            _breakdown = await sync.Reports.BreakdownAsync(SyncEntityNames.Patient, _fields[GroupByIndex].Key, sync.CurrentBranchId, from, to);
            Fill(Breakdown, _breakdown.Rows.Select(r => $"{r.Label}: {r.Count}").Append($"(not recorded): {_breakdown.WithoutValue}"));
        }
    });

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> lines)
    {
        target.Clear();
        foreach (var l in lines) target.Add(l);
        if (target.Count == 0) target.Add("—");
    }

    [RelayCommand]
    private Task ExcelAsync() => RunAsync(async () =>
    {
        if (_summary is null) return;
        var path = Path.Combine(FileSystem.CacheDirectory, $"Report-{_summary.From:yyyyMMdd}-{_summary.To:yyyyMMdd}.xlsx");
        await File.WriteAllBytesAsync(path, ReportService.ToExcel(_summary, CareUi.Clinic(session), _breakdown));
        await Share.Default.RequestAsync(new ShareFileRequest("Report", new ShareFile(path, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")));
    });

    [RelayCommand]
    private Task PdfAsync() => RunAsync(async () =>
    {
        if (_summary is null) return;
        await CareUi.OpenPdfAsync("Report", ClinicLetterPdf.Render(ReportService.ToLetter(_summary, CareUi.Clinic(session), CareUi.BranchLine(sync, session), true, _breakdown)));
    });
}
