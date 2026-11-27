using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Clinic.Client.Core.Clinical;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Contracts;
using Clinic.Shared.Forms;
using Clinic.Shared.Reports;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;

namespace Clinic.Api.Tests;

public sealed class InventoryFixture : IAsyncLifetime
{
    public ClinicApiFactory Factory { get; } = new();

    public async Task InitializeAsync()
    {
        using var admin = await Factory.DemoAdminAsync();
        foreach (var (template, context) in new[] { ("patient_standard", (string?)null), ("vitals_standard", null), ("opd_general", "general") })
        {
            var t = (await admin.GetFromJsonAsync<List<FormTemplateDto>>(FormRoutes.FormTemplates))!.Single(x => x.Key == template);
            var form = await (await admin.PostAsJsonAsync(FormRoutes.Forms, new CreateFormRequest(t.Name, t.EntityType, null, context, template))).ReadAsync<FormDto>();
            await (await admin.PostAsync($"{FormRoutes.Forms}/{form.Id}/publish", null)).EnsureSuccessAsync();
        }
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

public sealed class InventoryReportsTests(InventoryFixture fixture) : IClassFixture<InventoryFixture>
{
    private ClinicApiFactory Factory => fixture.Factory;

    private sealed record DoctorSetup(DoctorDto Profile, string UserName);

    private async Task<DoctorSetup> DoctorAsync()
    {
        var userName = await Factory.CreateUserAsync("Doctor");
        using var admin = await Factory.DemoAdminAsync();
        var user = (await admin.GetFromJsonAsync<PagedResult<UserDto>>($"{ApiRoutes.Users}?search={userName}"))!.Items.Single();
        var main = (await admin.GetFromJsonAsync<List<BranchDto>>(ApiRoutes.Branches))!.Single(b => b.Code == "MAIN");
        var doctor = await (await admin.PostAsJsonAsync(SchedulingRoutes.Doctors, new SaveDoctorRequest(
            $"Dr {userName}", "general", "M", "PMDC-21", DateTime.Today.AddYears(1), DoctorTypes.Permanent, 1000, 10, true, user.Id))).ReadAsync<DoctorDto>();
        await (await admin.PostAsJsonAsync($"{SchedulingRoutes.Doctors}/{doctor.Id}/schedules",
            new SaveScheduleRequest(main.Id, (int)DateTime.Today.DayOfWeek, TimeSpan.Zero, new TimeSpan(23, 59, 0)))).EnsureSuccessAsync();
        return new DoctorSetup(doctor, userName);
    }

    private async Task<ClientDevice> DeviceForAsync(string userName)
    {
        var d = await ClientDevice.CreateAsync(await Factory.DeviceAsync("demo", userName, SyncTestExtensions.TestPassword));
        await d.Engine.SyncAsync();
        return d;
    }

    private async Task<(LocalPatient Patient, LocalAppointment WalkIn)> WalkInAsync(DoctorSetup doctor, string city = "KHI")
    {
        await using var reception = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var forms = new DynamicDataService(reception.Db);
        var version = (await forms.ResolveFormAsync(SyncEntityNames.Patient, reception.Session.BranchId))!;
        var model = new DynamicFormModel(version, [], new EvaluationContext { Variables = new Dictionary<string, object?> { ["gender"] = "M", ["ageYears"] = 50d } },
            await forms.LoadLookupsAsync(version.Definition));
        model.SetValue("allergies", new JsonArray(JsonValue.Create("none")));
        model.SetValue("city", city);
        var patient = await reception.Patients.RegisterAsync(
            new PatientInput($"Patient {Guid.NewGuid():N}"[..16], "M", new DateTime(1976, 3, 1), false, "0345-1231231", reception.Session.BranchId), version, model.ValuesToSave());
        var walkIn = await reception.Scheduling.WalkInAsync(patient.Id, doctor.Profile.Id, reception.Session.BranchId);
        Assert.Equal(0, (await reception.Engine.SyncAsync()).Outstanding);
        return (patient, walkIn);
    }

    /// <summary>The doctor sees the walk-in, codes a diagnosis and prescribes paracetamol.</summary>
    private async Task<LocalEncounter> ConsultAsync(DoctorSetup doctor, LocalAppointment walkIn, string icd = "J06.9", string text = "Acute upper respiratory infection")
    {
        await using var d = await DeviceForAsync(doctor.UserName);
        var forms = new DynamicDataService(d.Db);
        var encounters = new EncounterService(d.Store, forms);
        var rx = new PrescriptionService(d.Store, forms, encounters);
        var e = await encounters.StartFromAppointmentAsync(walkIn.Id);
        var notes = new DynamicFormModel((await encounters.FormAsync(e))!, [], EncounterService.ContextFor(e, ["Doctor"]));
        notes.SetValue("chief_complaint", "Fever and sore throat");
        notes.SetValue("plan", "Paracetamol, fluids");
        await encounters.SaveNotesAsync(e.Id, notes.ValuesToSave(), null);
        await encounters.AddDiagnosisAsync(e.Id, icd, text, primary: true);
        var paracetamol = (await rx.SearchDrugsAsync("Paracetamol")).First(x => x.Strength == "500 mg");
        await rx.AddDrugAsync(e.Id, paracetamol);
        await encounters.SignAsync(e.Id);
        Assert.Equal(0, (await d.Engine.SyncAsync()).Outstanding);
        return e;
    }

    private static async Task SyncCleanAsync(ClientDevice d)
    {
        var r = await d.Engine.SyncAsync();
        Assert.True(r.Outstanding == 0, string.Join(" | ", (await d.Center.ListAsync()).Select(x => $"{x.Summary}: {x.Status} {x.LastError}")));
    }

    private static async Task<string> RejectionAsync(ClientDevice d)
    {
        var r = await d.Engine.SyncAsync();
        Assert.True(r.Rejected >= 1, "Expected the server to refuse the change.");
        var entries = await d.Center.ListAsync();
        var error = string.Join(" | ", entries.Where(e => e.LastError is not null).Select(e => e.LastError));
        foreach (var e in entries) await d.Center.DiscardAsync(e.Seq);
        return error;
    }

    private static async Task<LocalInventoryItem> ParacetamolAsync(ClientDevice d) =>
        (await new InventoryService(d.Store).ItemsAsync("Paracetamol 500")).Single();

    // ---------------------------------------------------------------------
    // §4.7 pharmacy
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Pharmacy_dispenses_a_prescription_first_expiry_first_and_bills_it()
    {
        var doctor = await DoctorAsync();
        var (patient, walkIn) = await WalkInAsync(doctor);
        var e = await ConsultAsync(doctor, walkIn);

        await using var pharmacy = await DeviceForAsync(await Factory.CreateUserAsync("Pharmacist"));
        var stock = new InventoryService(pharmacy.Store);
        var branch = pharmacy.Session.BranchId;
        var item = await ParacetamolAsync(pharmacy);
        pharmacy.Transport.Offline = true;

        // FR-INV-01: two batches; the earlier one expires within the alert window.
        await stock.ReceiveAsync(item.Id, branch, "P-EARLY", 10, DateTime.Today.AddDays(30), 2, "Getz Pharma", "INV-551");
        await stock.ReceiveAsync(item.Id, branch, "P-LATE", 100, DateTime.Today.AddDays(400), 2.2m);
        await Assert.ThrowsAsync<InventoryException>(() => stock.ReceiveAsync(item.Id, branch, "OLD", 5, DateTime.Today.AddDays(-1), 2));
        Assert.Contains(await stock.AlertsAsync(branch), a => a.ItemId == item.Id && a.Kind == StockAlertKind.NearExpiry && a.BatchNo == "P-EARLY");

        // The prescription at the pharmacy: 1 tablet three times a day for 3 days = 9.
        var line = Assert.Single(await stock.PrescriptionAsync(e.Id, branch));
        Assert.Equal(item.Id, line.Item!.Id);
        Assert.Equal(9, line.Suggested);
        Assert.Equal(110, line.OnHand);

        // FEFO: 10 from the batch expiring first, 5 from the next.
        var d = await stock.DispenseAsync(patient.Id, branch, item.Id, 15, line.RxItem.Id);
        var batches = await stock.BatchesAsync(item.Id, branch);
        Assert.Equal(0, batches.Single(b => b.BatchNo == "P-EARLY").OnHand);
        Assert.Equal(95, batches.Single(b => b.BatchNo == "P-LATE").OnHand);
        await Assert.ThrowsAsync<InventoryException>(() => stock.DispenseAsync(patient.Id, branch, item.Id, 1, line.RxItem.Id));
        await Assert.ThrowsAsync<InventoryException>(() => stock.DispenseAsync(patient.Id, branch, item.Id, 500));

        // FR-BIL-02: the dispensation is a charge at the pharmacy counter.
        var billing = new BillingService(pharmacy.Db, pharmacy.Store, pharmacy.Session.DeviceCode);
        var charge = Assert.Single(await billing.UnbilledAsync(patient.Id), c => c.Source == ChargeSource.Dispense);
        Assert.Equal(150m, charge.UnitPrice * charge.Quantity);
        var invoice = await billing.CreateInvoiceAsync(patient.Id, branch, [charge]);
        await billing.FinaliseAsync(invoice.Id);
        await billing.PayAsync(invoice.Id, 150);
        await stock.ReturnAsync(d.Id, 2, "Patient had some at home");
        Assert.Equal(97, (await stock.BatchesAsync(item.Id, branch)).Sum(b => b.OnHand));

        pharmacy.Transport.Offline = false;
        await SyncCleanAsync(pharmacy);

        // The ledger cannot be edited, and only pharmacy staff dispense.
        var movement = (await stock.LedgerAsync(item.Id, branch)).First();
        movement.Quantity = -1;
        await pharmacy.Store.UpdateAsync(movement);
        Assert.Contains("cannot be changed", await RejectionAsync(pharmacy));

        await using var nurse = await DeviceForAsync(await Factory.CreateUserAsync("Nurse / Assistant"));
        await new InventoryService(nurse.Store).DispenseAsync(patient.Id, branch, item.Id, 1);
        Assert.Contains("Prescriptions.Dispense", await RejectionAsync(nurse));

        // Server stock agrees with the device.
        using var admin = await Factory.DemoAdminAsync();
        var server = await admin.GetFromJsonAsync<JsonObject>($"{ReportRoutes.Stock}?branchId={branch}");
        var row = server!["items"]!.AsArray().Single(i => i!["itemId"]!.GetValue<Guid>() == item.Id)!;
        Assert.Equal(97m, row["onHand"]!.GetValue<decimal>());
    }

    // ---------------------------------------------------------------------
    // Phase 7 acceptance
    // ---------------------------------------------------------------------

    /// <summary>"Reports match server totals after sync."</summary>
    [Fact]
    public async Task Offline_counters_merge_stock_and_reports_match_server_totals()
    {
        var doctor = await DoctorAsync();
        var (p1, w1) = await WalkInAsync(doctor, "KHI");
        var (p2, w2) = await WalkInAsync(doctor, "LHE");
        var (p3, _) = await WalkInAsync(doctor, "KHI");
        await ConsultAsync(doctor, w1);
        await ConsultAsync(doctor, w2, "E11.9", "Type 2 diabetes mellitus without complications");

        await using var a = await DeviceForAsync(await Factory.CreateUserAsync("Pharmacist"));
        await using var b = await DeviceForAsync(await Factory.CreateUserAsync("Pharmacist"));
        var branch = a.Session.BranchId;
        // An item no other test stocks, so its stock is exactly what this test records.
        var item = (await new InventoryService(a.Store).ItemsAsync(category: ItemCategory.Medicine)).First(i => !i.Name.StartsWith("Paracetamol"));
        await new InventoryService(a.Store).ReceiveAsync(item.Id, branch, "LOT-7", 5, DateTime.Today.AddYears(1), 2);
        await SyncCleanAsync(a);
        await b.Engine.SyncAsync();

        // Both counters offline; each sees 5 in stock and dispenses within it.
        a.Transport.Offline = b.Transport.Offline = true;
        var da = await new InventoryService(a.Store).DispenseAsync(p1.Id, branch, item.Id, 4);
        var db = await new InventoryService(b.Store).DispenseAsync(p3.Id, branch, item.Id, 3);
        var billA = new BillingService(a.Db, a.Store, a.Session.DeviceCode);
        var billB = new BillingService(b.Db, b.Store, b.Session.DeviceCode);
        var invA = await billA.CreateInvoiceAsync(p1.Id, branch, (await billA.UnbilledAsync(p1.Id)).Where(c => c.SourceId == da.Id));
        await billA.FinaliseAsync(invA.Id);
        await billA.PayAsync(invA.Id, 40);
        var invB = await billB.CreateInvoiceAsync(p3.Id, branch, (await billB.UnbilledAsync(p3.Id)).Where(c => c.SourceId == db.Id));
        await billB.FinaliseAsync(invB.Id);
        await billB.PayAsync(invB.Id, 10, PaymentMethods.Card); // Rs 20 left pending

        a.Transport.Offline = b.Transport.Offline = false;
        await SyncCleanAsync(a);
        await SyncCleanAsync(b);
        await a.Engine.SyncAsync();

        // FR-INV-02: both dispensings kept; the stock is −2 everywhere and flagged for a count.
        foreach (var device in new[] { a, b })
        {
            Assert.Equal(-2, (await new InventoryService(device.Store).BatchesAsync(item.Id, branch)).Sum(x => x.OnHand));
            Assert.Contains(await new InventoryService(device.Store).AlertsAsync(branch), x => x.ItemId == item.Id && x.Kind == StockAlertKind.Negative);
        }

        // FR-REP-01/02: the device's offline report equals the server's.
        using var admin = await Factory.DemoAdminAsync();
        var from = DateTime.Today.AddDays(-7);
        var to = DateTime.Today;
        var server = (await admin.GetFromJsonAsync<ReportSummary>($"{ReportRoutes.Summary}?branchId={branch}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"))!;
        var reports = new ReportService(a.Store, new DynamicDataService(a.Db));
        var local = await reports.SummaryAsync(branch, from, to);

        Assert.Equal(server.NewPatients, local.NewPatients);
        Assert.True(local.NewPatients >= 3);
        Assert.Equal(server.Visits, local.Visits);
        Assert.Equal(server.VisitsByDoctor.Select(r => (r.Key, r.Count)), local.VisitsByDoctor.Select(r => (r.Key, r.Count)));
        Assert.Equal(server.TopDiagnoses.Select(r => (r.Key, r.Count)), local.TopDiagnoses.Select(r => (r.Key, r.Count)));
        Assert.Contains(local.TopDiagnoses, r => r.Key == "E11.9");
        Assert.Equal(server.RevenueByMethod.Select(r => (r.Key, r.Net)), local.RevenueByMethod.Select(r => (r.Key, r.Net)));
        Assert.Equal(server.RevenueByDay.Select(r => (r.Key, r.Net)), local.RevenueByDay.Select(r => (r.Key, r.Net)));
        Assert.Equal(server.PendingInvoices, local.PendingInvoices);
        Assert.Equal(server.PendingAmount, local.PendingAmount);
        Assert.True(local.PendingAmount >= 20);
        Assert.Equal(server.StockAlerts.Select(r => (r.Key, r.Count)), local.StockAlerts.Select(r => (r.Key, r.Count)));

        // FR-REP-03: group by a reportable dynamic field (patient city), same on both sides.
        var serverCity = (await admin.GetFromJsonAsync<FieldBreakdown>(
            $"{ReportRoutes.Breakdown}?entityType=Patient&fieldKey=city&branchId={branch}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"))!;
        var localCity = await reports.BreakdownAsync(SyncEntityNames.Patient, "city", branch, from, to);
        Assert.Equal(serverCity.Rows.Select(r => (r.Key, r.Count)), localCity.Rows.Select(r => (r.Key, r.Count)));
        Assert.Equal("Karachi", localCity.Rows.First(r => r.Key == "KHI").Label);
        Assert.True(localCity.Rows.Single(r => r.Key == "KHI").Count >= 2);
        var notReportable = await admin.GetAsync($"{ReportRoutes.Breakdown}?entityType=Patient&fieldKey=address&branchId={branch}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
        Assert.False(notReportable.IsSuccessStatusCode);

        // Export: a real workbook and a PDF.
        var xlsx = ReportService.ToExcel(local, "Demo Clinic", localCity);
        using (var zip = new ZipArchive(new MemoryStream(xlsx)))
        {
            Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
            Assert.Equal(8, zip.Entries.Count(x => x.FullName.StartsWith("xl/worksheets/")));
            using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            Assert.Contains("New patients", await sheet.ReadToEndAsync());
        }
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(ClinicLetterPdf.Render(ReportService.ToLetter(local, "Demo Clinic", "Main", true, localCity)), 0, 4));
        _ = p2;
    }

    // ---------------------------------------------------------------------
    // Pure rules
    // ---------------------------------------------------------------------

    [Fact]
    public void Stock_rules()
    {
        var today = new DateTime(2026, 11, 26);
        var early = new BatchStock(Guid.NewGuid(), "A", today.AddDays(10), 4);
        var expired = new BatchStock(Guid.NewGuid(), "X", today.AddDays(-1), 50);
        var late = new BatchStock(Guid.NewGuid(), "B", today.AddDays(300), 20);
        var (picks, shortBy) = Stock.PickFefo([late, expired, early], 10, today);
        Assert.Equal(["A", "B"], picks.Select(p => p.BatchNo));
        Assert.Equal([4m, 6m], picks.Select(p => p.Quantity));
        Assert.Equal(0, shortBy);
        Assert.Equal(26, Stock.PickFefo([early, late], 50, today).Short);

        var alerts = Stock.Alerts(Guid.NewGuid(), "Amoxicillin", 30, [early, expired, late], today);
        Assert.Contains(alerts, a => a.Kind == StockAlertKind.NearExpiry && a.BatchNo == "A");
        Assert.Contains(alerts, a => a.Kind == StockAlertKind.Expired && a.BatchNo == "X");
        Assert.DoesNotContain(alerts, a => a.Kind == StockAlertKind.Low); // 74 on hand

        Assert.Equal(9, Stock.QuantityFor("1 tablet", "tds", 3));
        Assert.Equal(14, Stock.QuantityFor("2 tablets", "od", 7));
        Assert.Equal(3, Stock.QuantityFor("1/2 tablet", "bd", 3));
        Assert.Equal(1, Stock.QuantityFor("5 ml", "tds", 5));
        Assert.Equal(1, Stock.QuantityFor(null, "sos", null));

        Assert.NotNull(MovementKind.CheckSign(MovementKind.Dispense, 3));
        Assert.NotNull(MovementKind.CheckSign(MovementKind.Purchase, -3));
        Assert.Null(MovementKind.CheckSign(MovementKind.Adjust, -3));
    }
}
