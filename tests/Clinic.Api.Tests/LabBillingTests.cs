using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Clinic.Client.Core.Clinical;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Contracts;
using Clinic.Shared.Forms;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Sync;

namespace Clinic.Api.Tests;

/// <summary>A clinic with the Phase 5 forms plus the CBC (context 'cbc') and generic lab result forms.</summary>
public sealed class LabBillingFixture : IAsyncLifetime
{
    public ClinicApiFactory Factory { get; } = new();

    public async Task InitializeAsync()
    {
        using var admin = await Factory.DemoAdminAsync();
        foreach (var (template, context) in new[]
                 {
                     ("patient_standard", (string?)null), ("vitals_standard", null), ("opd_general", "general"),
                     ("lab_cbc", "cbc"), ("lab_generic", null),
                 })
        {
            var t = (await admin.GetFromJsonAsync<List<FormTemplateDto>>(FormRoutes.FormTemplates))!.Single(x => x.Key == template);
            var form = await (await admin.PostAsJsonAsync(FormRoutes.Forms, new CreateFormRequest(t.Name, t.EntityType, null, context, template))).ReadAsync<FormDto>();
            await (await admin.PostAsync($"{FormRoutes.Forms}/{form.Id}/publish", null)).EnsureSuccessAsync();
        }
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

public sealed class LabBillingTests(LabBillingFixture fixture) : IClassFixture<LabBillingFixture>
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
            $"Dr {userName}", "general", "M", "PMDC-99", DateTime.Today.AddYears(1), DoctorTypes.Permanent, 1000, 10, true, user.Id))).ReadAsync<DoctorDto>();
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

    private async Task<(LocalPatient Patient, LocalAppointment WalkIn)> WalkInAsync(DoctorSetup doctor, string gender = "F")
    {
        await using var reception = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var forms = new DynamicDataService(reception.Db);
        var version = (await forms.ResolveFormAsync(SyncEntityNames.Patient, reception.Session.BranchId))!;
        var model = new DynamicFormModel(version, [], new EvaluationContext { Variables = new Dictionary<string, object?> { ["gender"] = gender, ["ageYears"] = 30d } });
        model.SetValue("allergies", new JsonArray(JsonValue.Create("none")));
        var patient = await reception.Patients.RegisterAsync(
            new PatientInput($"Patient {Guid.NewGuid():N}"[..16], gender, new DateTime(1995, 6, 1), false, "0301-7654321", reception.Session.BranchId),
            version, model.ValuesToSave());
        var walkIn = await reception.Scheduling.WalkInAsync(patient.Id, doctor.Profile.Id, reception.Session.BranchId);
        Assert.Equal(0, (await reception.Engine.SyncAsync()).Outstanding);
        return (patient, walkIn);
    }

    private static async Task SyncCleanAsync(ClientDevice d)
    {
        var result = await d.Engine.SyncAsync();
        Assert.True(result.Outstanding == 0, string.Join(" | ", (await d.Center.ListAsync()).Select(x => $"{x.Summary}: {x.Status} {x.LastError}")));
    }

    private static async Task<string> SingleRejectionAsync(ClientDevice d)
    {
        var result = await d.Engine.SyncAsync();
        Assert.True(result.Rejected >= 1, "Expected the server to refuse the change.");
        var entries = await d.Center.ListAsync();
        var error = string.Join(" | ", entries.Where(e => e.LastError is not null).Select(e => e.LastError));
        foreach (var e in entries) await d.Center.DiscardAsync(e.Seq);
        return error;
    }

    private static BillingService Billing(ClientDevice d) => new(d.Db, d.Store, d.Session.DeviceCode);

    // ---------------------------------------------------------------------
    // §4.5 labs
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Doctor_orders_tests_offline_and_the_lab_collects_results_and_verifies_them()
    {
        var doctor = await DoctorAsync();
        var (patient, walkIn) = await WalkInAsync(doctor, "F");

        // Doctor, offline: order a CBC and a fasting sugar from the consultation.
        await using var doc = await DeviceForAsync(doctor.UserName);
        var docForms = new DynamicDataService(doc.Db);
        var docLabs = new LabService(doc.Store, docForms);
        doc.Transport.Offline = true;
        var e = await new EncounterService(doc.Store, docForms).StartFromAppointmentAsync(walkIn.Id);
        var menu = await docLabs.TestsAsync();
        var cbc = menu.Single(t => t.Code == "cbc");
        var fbs = menu.Single(t => t.Code == "fbs");
        var order = await docLabs.OrderAsync(patient.Id, e.BranchId, [cbc.Id, fbs.Id], e.Id, clinicalNotes: "Tired, pale");
        var lines = await docLabs.ResultsAsync(order.Id);
        Assert.Contains("complete blood count", (await docForms.GetVersionAsync(lines.Single(r => r.TestCode == "cbc").FormVersionId!.Value))!.Definition.Name.En, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("generic", (await docForms.GetVersionAsync(lines.Single(r => r.TestCode == "fbs").FormVersionId!.Value))!.Definition.Name.En);
        doc.Transport.Offline = false;
        await SyncCleanAsync(doc);

        // The lab, offline: work list → collect → results → verify.
        await using var lab = await DeviceForAsync(await Factory.CreateUserAsync("Lab Technician"));
        var labs = new LabService(lab.Store, new DynamicDataService(lab.Db));
        lab.Transport.Offline = true;
        var work = await labs.WorkListAsync(lab.Session.BranchId);
        var cbcLine = work.Single(r => r.OrderId == order.Id && r.TestCode == "cbc");
        var fbsLine = work.Single(r => r.OrderId == order.Id && r.TestCode == "fbs");

        await Assert.ThrowsAsync<LabException>(() => labs.EnterResultsAsync(cbcLine.Id, new JsonObject { ["hb"] = 11.2 }));
        await labs.CollectAsync(cbcLine.Id);
        await Assert.ThrowsAsync<LabException>(() => labs.EnterResultsAsync(cbcLine.Id, new JsonObject { ["hb"] = 11.2 })); // WBC, platelets required
        var cbcResult = await labs.EnterResultsAsync(cbcLine.Id, new JsonObject { ["hb"] = 11.2, ["wbc"] = 7.4, ["platelets"] = 250, ["mcv"] = 76 }, "Hypochromic picture");
        Assert.True(cbcResult.IsAbnormal); // 11.2 is low for a woman
        var values = await labs.ValuesAsync(cbcResult);
        Assert.Equal(AbnormalSeverity.Warning, values.Single(v => v.Label == "Haemoglobin").Flag);
        Assert.Equal("Microcytic", values.Single(v => v.Label == "MCV").FlagText);
        Assert.Equal(AbnormalSeverity.None, values.Single(v => v.Label == "Platelets").Flag);
        await labs.VerifyAsync(cbcLine.Id);

        await labs.CollectAsync(fbsLine.Id, "S-1001");
        var fbsResult = await labs.EnterResultsAsync(fbsLine.Id, new JsonObject { ["value"] = 95, ["unit"] = "mg/dL", ["ref_low"] = 70, ["ref_high"] = 110 });
        Assert.False(fbsResult.IsAbnormal);
        Assert.Equal("N", (await labs.ValuesAsync(fbsResult)).Single(v => v.Label == "Flag").Value);
        await labs.VerifyAsync(fbsLine.Id);
        Assert.Equal("complete", (await lab.Store.GetAsync<LocalLabOrder>(order.Id))!.Status);
        lab.Transport.Offline = false;
        await SyncCleanAsync(lab);

        // A verified result is locked on the server too.
        var hb = (await lab.Store.ListAsync<LocalFieldValue>(v => v.RecordId == cbcLine.Id && v.FieldKey == "hb")).Single();
        hb.ValueNumber = 13.5m;
        await lab.Store.UpdateAsync(hb);
        Assert.Contains("verified", await SingleRejectionAsync(lab));

        // A nurse can see results but not enter them.
        await using var nurse = await DeviceForAsync(await Factory.CreateUserAsync("Nurse / Assistant"));
        var other = await new LabService(nurse.Store, new DynamicDataService(nurse.Db))
            .OrderAsync(patient.Id, nurse.Session.BranchId, [cbc.Id]);
        Assert.Contains("Labs.Order", await SingleRejectionAsync(nurse)); // nurses do not order tests
        Assert.Equal(LabStatus.Verified, (await nurse.Store.GetAsync<LocalLabResult>(cbcLine.Id))!.Status);
        _ = other;

        // Back at the doctor: the verified, abnormal result and its report.
        await doc.Engine.SyncAsync();
        var seen = await doc.Store.GetAsync<LocalLabResult>(cbcLine.Id);
        Assert.Equal(LabStatus.Verified, seen!.Status);
        Assert.True(seen.IsAbnormal);
        var report = await docLabs.BuildReportAsync(order.Id, "Demo Clinic", "Main");
        Assert.Equal("Laboratory report", report.Title);
        Assert.Contains(report.Sections.SelectMany(s => s.Lines), l => l.StartsWith("Haemoglobin: 11.2 g/dL") && l.Contains("▲ Low"));
        Assert.Equal("%PDF", Encoding.ASCII.GetString(ClinicLetterPdf.Render(report), 0, 4));

        // FR-LAB-03: an outside report attached to the test.
        await using (var pdf = new MemoryStream(ClinicLetterPdf.Render(report)))
            await lab.Attachments.AddAsync(SyncEntityNames.LabResult, fbsLine.Id, lab.Session.BranchId, pdf, "outside-report.pdf", "application/pdf", "Outside lab", "lab_report");
        await SyncCleanAsync(lab);
        Assert.Equal(1, await lab.Attachments.UploadPendingAsync(lab.Files));
    }

    // ---------------------------------------------------------------------
    // §4.6 billing — Phase 6 acceptance
    // ---------------------------------------------------------------------

    /// <summary>"Day closing totals match across devices after sync."</summary>
    [Fact]
    public async Task Day_closing_totals_match_across_devices_after_sync()
    {
        var doctor = await DoctorAsync();
        var (p1, _) = await WalkInAsync(doctor);
        var (p2, _) = await WalkInAsync(doctor, "M");
        var (p3, _) = await WalkInAsync(doctor);

        await using var r1 = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        await using var r2 = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var b1 = Billing(r1);
        var b2 = Billing(r2);
        var branch = r1.Session.BranchId;
        r1.Transport.Offline = r2.Transport.Offline = true; // ---- both desks offline

        // Desk 1: consultation (doctor fee) + registration; paid in cash. A deposit for another patient by card.
        var unbilled = await b1.UnbilledAsync(p1.Id);
        var visit = Assert.Single(unbilled);
        Assert.Equal(1000m, visit.UnitPrice);
        var inv1 = await b1.CreateInvoiceAsync(p1.Id, branch, unbilled);
        var registration = (await b1.ServicesAsync(branch)).Single(s => s.Code == "registration");
        await b1.AddServiceAsync(inv1.Id, registration.Id);
        inv1 = await b1.FinaliseAsync(inv1.Id);
        Assert.Equal(1200m, inv1.Total);
        Assert.StartsWith($"MAIN-{r1.Session.DeviceCode}-", inv1.InvoiceNo);
        await Assert.ThrowsAsync<BillingException>(() => b1.PayAsync(inv1.Id, 1500));
        await b1.PayAsync(inv1.Id, 1200);
        Assert.True((await b1.SummaryAsync(inv1.Id)).IsPaid);
        Assert.Empty(await b1.UnbilledAsync(p1.Id));
        await b1.DepositAsync(p3.Id, branch, 500, PaymentMethods.Card);

        // Desk 2: consultation + a manual line; split cash / card.
        var inv2 = await b2.CreateInvoiceAsync(p2.Id, branch, await b2.UnbilledAsync(p2.Id));
        await b2.AddManualAsync(inv2.Id, "Dressing kit", 300);
        inv2 = await b2.FinaliseAsync(inv2.Id);
        Assert.Equal(1300m, inv2.Total);
        await b2.PayAsync(inv2.Id, 1000);
        await b2.PayAsync(inv2.Id, 300, PaymentMethods.Card, "POS 4411");
        Assert.NotEqual(inv1.InvoiceNo, inv2.InvoiceNo); // unique without the network

        // Back online.
        r1.Transport.Offline = r2.Transport.Offline = false;
        await SyncCleanAsync(r1);
        await SyncCleanAsync(r2);
        await r1.Engine.SyncAsync();

        // Each desk closes its own cash; the server agrees with both.
        var close1 = await b1.CloseCashAsync(branch, DateTime.Today, 1200);
        var close2 = await b2.CloseCashAsync(branch, DateTime.Today, 990, "Rs 10 short");
        Assert.Equal(1200m, close1.ExpectedCash);
        Assert.Equal(1000m, close2.ExpectedCash);
        await SyncCleanAsync(r1);
        await SyncCleanAsync(r2);
        await r1.Engine.SyncAsync();
        await r2.Engine.SyncAsync();
        foreach (var (device, id) in new[] { (r1, close1.Id), (r2, close2.Id) })
        {
            var closing = (await device.Store.GetAsync<LocalCashClosing>(id))!;
            Assert.False(closing.Mismatch);
            Assert.Equal(closing.ExpectedCash, closing.ServerExpectedCash);
        }

        // Branch totals on either device equal the server's.
        using var admin = await Factory.DemoAdminAsync();
        async Task<DaySummary> ServerAsync() =>
            (await admin.GetFromJsonAsync<DaySummary>($"{LabBillingRoutes.DaySummary}?branchId={branch}&date={DateTime.Today:yyyy-MM-dd}"))!;
        var server = await ServerAsync();
        foreach (var device in new[] { b1, b2 })
        {
            var local = await device.DaySummaryAsync(branch, DateTime.Today);
            Assert.Equal(server.Net, local.Net);
            Assert.Equal(server.Cash, local.Cash);
            Assert.Equal(server.Count, local.Count);
            Assert.Equal(server.Methods.Select(m => (m.Method, m.Net)), local.Methods.Select(m => (m.Method, m.Net)));
        }
        Assert.True(server.Net >= 3000m); // other tests in the class may add their own payments

        // An accountant refunds part of invoice 1; totals still match after sync.
        await using var acc = await DeviceForAsync(await Factory.CreateUserAsync("Accountant"));
        var ba = Billing(acc);
        await ba.RefundAsync(p1.Id, branch, inv1.Id, 200, "Registration waived");
        await Assert.ThrowsAsync<BillingException>(() => ba.RefundAsync(p1.Id, branch, inv1.Id, 1500, "too much"));
        await SyncCleanAsync(acc);
        await r1.Engine.SyncAsync();
        var after = await ServerAsync();
        Assert.Equal(server.Net - 200, after.Net);
        Assert.Equal(after.Net, (await b1.DaySummaryAsync(branch, DateTime.Today)).Net);

        // Patient balances agree too: p3 holds Rs 500 credit; p1 owes Rs 200 again after the refund.
        foreach (var (patient, expected) in new[] { (p3.Id, -500m), (p1.Id, 200m) })
        {
            var serverBalance = (await admin.GetFromJsonAsync<PatientBalanceDto>($"{LabBillingRoutes.PatientBalance}/{patient}"))!;
            Assert.Equal(expected, serverBalance.Balance);
            Assert.Equal(expected, (await b1.BalanceAsync(patient)).Balance);
        }
    }

    [Fact]
    public async Task The_server_enforces_billing_rules_devices_could_bypass()
    {
        var doctor = await DoctorAsync();
        var (p1, _) = await WalkInAsync(doctor);
        var (p2, _) = await WalkInAsync(doctor);
        await using var r1 = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        await using var r2 = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var b1 = Billing(r1);
        var b2 = Billing(r2);
        var branch = r1.Session.BranchId;

        // FR-BIL-03: a receptionist cannot give a discount.
        var discounted = await b1.CreateInvoiceAsync(p2.Id, branch);
        await b1.AddManualAsync(discounted.Id, "Certificate", 500);
        await b1.SetInvoiceDiscountAsync(discounted.Id, 100, "staff");
        await b1.FinaliseAsync(discounted.Id);
        Assert.Contains("Billing.Discount", await SingleRejectionAsync(r1));

        // FR-BIL-02: the same visit billed on two offline desks — the second is refused.
        r1.Transport.Offline = r2.Transport.Offline = true;
        var first = await b1.CreateInvoiceAsync(p1.Id, branch, await b1.UnbilledAsync(p1.Id));
        var second = await b2.CreateInvoiceAsync(p1.Id, branch, await b2.UnbilledAsync(p1.Id));
        r1.Transport.Offline = r2.Transport.Offline = false;
        await SyncCleanAsync(r1);
        Assert.Contains($"already billed on invoice {first.InvoiceNo}", await SingleRejectionAsync(r2));
        _ = second;

        // A final invoice's amounts and a payment cannot be changed afterwards.
        var fin = await b1.FinaliseAsync(first.Id);
        var pay = await b1.PayAsync(first.Id, 400);
        await SyncCleanAsync(r1);
        fin.Total += 1;
        await r1.Store.UpdateAsync(fin);
        Assert.Contains("final", await SingleRejectionAsync(r1));
        pay.Amount = 4000;
        await r1.Store.UpdateAsync(pay);
        Assert.Contains("cannot be edited", await SingleRejectionAsync(r1));

        // Refunds need Billing.Refund.
        await b1.RefundAsync(p1.Id, branch, first.Id, 100, "changed mind");
        Assert.Contains("Billing.Refund", await SingleRejectionAsync(r1));

        // Invoice totals are recomputed by the server: a draft whose header disagrees with its lines cannot be finalised.
        var bad = await b1.CreateInvoiceAsync(p2.Id, branch);
        await b1.AddManualAsync(bad.Id, "Nebulisation", 500);
        await SyncCleanAsync(r1);
        var header = (await r1.Store.GetAsync<LocalInvoice>(bad.Id))!;
        header.Status = InvoiceStatus.Final;
        header.Total = 50;
        header.Subtotal = 50;
        await r1.Store.UpdateAsync(header);
        Assert.Contains("do not match", await SingleRejectionAsync(r1));
    }

    // ---------------------------------------------------------------------
    // Pure rules
    // ---------------------------------------------------------------------

    [Fact]
    public void Invoice_arithmetic_and_cash_closing_are_shared_rules()
    {
        var totals = Clinic.Shared.Clinical.Billing.Totals([new LineAmounts(2, 500, 100, 0), new LineAmounts(1, 1000, 0, 16)], 50);
        Assert.Equal(2000m, totals.Subtotal);
        Assert.Equal(150m, totals.Discount);
        Assert.Equal(160m, totals.Tax);
        Assert.Equal(2010m, totals.Total);

        var user = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var today = new DateTime(2026, 11, 20);
        var zone = TimeZoneInfo.CreateCustomTimeZone("PKT", TimeSpan.FromHours(5), "PKT", "PKT");
        PaymentEntry P(DateTime utc, string kind, string method, decimal amount, Guid? by = null) =>
            new(Guid.NewGuid(), by ?? user, branch, utc, kind, method, amount);
        var rows = new[]
        {
            P(new DateTime(2026, 11, 19, 20, 0, 0), PaymentKind.Payment, "cash", 1000),   // 01:00 local on the 20th: counts
            P(new DateTime(2026, 11, 20, 10, 0, 0), PaymentKind.Payment, "card", 700),
            P(new DateTime(2026, 11, 20, 11, 0, 0), PaymentKind.Refund, "cash", 200),
            P(new DateTime(2026, 11, 20, 12, 0, 0), PaymentKind.Deposit, "cash", 300),
            P(new DateTime(2026, 11, 20, 19, 30, 0), PaymentKind.Payment, "cash", 999),   // 00:30 on the 21st: next day
            P(new DateTime(2026, 11, 20, 9, 0, 0), PaymentKind.Payment, "cash", 50, Guid.NewGuid()), // another user
        };
        var mine = CashClosingCalculator.Summarise(rows, today, branch, user, zone);
        Assert.Equal(1100m, mine.Cash);
        Assert.Equal(1800m, mine.Net);
        Assert.Equal(4, mine.Count);
        Assert.Equal(1850m, CashClosingCalculator.Summarise(rows, today, branch, null, zone).Net);
    }

    [Fact]
    public void Lab_status_moves_forward_only()
    {
        Assert.True(LabStatus.CanMove(LabStatus.Ordered, LabStatus.Collected));
        Assert.True(LabStatus.CanMove(LabStatus.Resulted, LabStatus.Resulted)); // corrections before verification
        Assert.False(LabStatus.CanMove(LabStatus.Ordered, LabStatus.Verified));
        Assert.False(LabStatus.CanMove(LabStatus.Verified, LabStatus.Resulted));
        Assert.True(LabStatus.IsLocked(LabStatus.Verified));
    }
}
