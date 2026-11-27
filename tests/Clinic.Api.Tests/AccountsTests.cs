using System.Net.Http.Json;
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

public sealed class AccountsFixture : IAsyncLifetime
{
    public ClinicApiFactory Factory { get; } = new();

    public async Task InitializeAsync()
    {
        using var admin = await Factory.DemoAdminAsync();
        foreach (var (template, context) in new[] { ("patient_standard", (string?)null), ("vitals_standard", null), ("opd_general", "general"), ("lab_generic", null) })
        {
            var t = (await admin.GetFromJsonAsync<List<FormTemplateDto>>(FormRoutes.FormTemplates))!.Single(x => x.Key == template);
            var form = await (await admin.PostAsJsonAsync(FormRoutes.Forms, new CreateFormRequest(t.Name, t.EntityType, null, context, template))).ReadAsync<FormDto>();
            await (await admin.PostAsync($"{FormRoutes.Forms}/{form.Id}/publish", null)).EnsureSuccessAsync();
        }
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>Phase 6b: packages, panels, deposits applied, credit notes, doctor share, expenses, messages, feedback.</summary>
public sealed class AccountsTests(AccountsFixture fixture) : IClassFixture<AccountsFixture>
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
            $"Dr {userName}", "general", "F", "PMDC-11", DateTime.Today.AddYears(1), DoctorTypes.Permanent, 1000, 10, true, user.Id))).ReadAsync<DoctorDto>();
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

    private async Task<(LocalPatient Patient, LocalAppointment WalkIn)> WalkInAsync(DoctorSetup doctor, string? phone = "0333-1112223")
    {
        await using var reception = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var forms = new DynamicDataService(reception.Db);
        var version = (await forms.ResolveFormAsync(SyncEntityNames.Patient, reception.Session.BranchId))!;
        var model = new DynamicFormModel(version, [], new EvaluationContext { Variables = new Dictionary<string, object?> { ["gender"] = "F", ["ageYears"] = 40d } });
        model.SetValue("allergies", new JsonArray(JsonValue.Create("none")));
        var patient = await reception.Patients.RegisterAsync(
            new PatientInput($"Patient {Guid.NewGuid():N}"[..16], "F", new DateTime(1985, 1, 1), false, phone, reception.Session.BranchId), version, model.ValuesToSave());
        var walkIn = await reception.Scheduling.WalkInAsync(patient.Id, doctor.Profile.Id, reception.Session.BranchId);
        Assert.Equal(0, (await reception.Engine.SyncAsync()).Outstanding);
        return (patient, walkIn);
    }

    private static async Task SyncCleanAsync(ClientDevice d)
    {
        var r = await d.Engine.SyncAsync();
        Assert.True(r.Outstanding == 0, string.Join(" | ", (await d.Center.ListAsync()).Select(x => $"{x.Summary}: {x.Status} {x.LastError}")));
        Assert.True(r.Phase == Clinic.Client.Core.Sync.SyncPhase.Idle, $"Sync ended in {r.Phase}: {r.Message}");
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

    private static BillingService Billing(ClientDevice d) => new(d.Db, d.Store, d.Session.DeviceCode);

    // ---------------------------------------------------------------------
    // FR-BIL-10 packages, FR-BIL-11 running bill
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Package_sessions_are_used_one_by_one_and_billed_at_zero()
    {
        var doctor = await DoctorAsync();
        var (patient, _) = await WalkInAsync(doctor);
        await using var desk = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var billing = Billing(desk);
        var branch = desk.Session.BranchId;

        // Sell "10 physiotherapy sessions" (Rs 12,000).
        var plan = (await billing.PlansAsync()).Single(p => p.Code == "physio_10");
        var (package, saleInvoice) = await billing.SellPackageAsync(patient.Id, branch, plan.Id);
        Assert.Equal(12000m, saleInvoice.Total);
        await billing.FinaliseAsync(saleInvoice.Id);
        await billing.PayAsync(saleInvoice.Id, 12000, PaymentMethods.Card);
        await SyncCleanAsync(desk);

        // Two physio sessions done by the doctor, inside a case; and an ordinary procedure.
        await using var doc = await DeviceForAsync(doctor.UserName);
        var procedures = new ProcedureService(doc.Store, new DynamicDataService(doc.Db), doc.Attachments);
        var cases = new CaseService(doc.Store, new DynamicDataService(doc.Db));
        var physioCase = await cases.OpenAsync(patient.Id, (await cases.TypesAsync()).Single(t => t.Code == "physio_course").Id, branch, doctor.Profile.Id);
        var types = await procedures.TypesAsync();
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var p = await procedures.PlanAsync(patient.Id, branch, types.Single(t => t.Code == "physio_session").Id, new ProcedureLinks(CaseId: physioCase.Id, DoctorId: doctor.Profile.Id));
            await procedures.CompleteAsync(p.Id);
        }
        var dressing = await procedures.PlanAsync(patient.Id, branch, types.Single(t => t.Code == "dressing").Id, new ProcedureLinks(CaseId: physioCase.Id));
        await procedures.CompleteAsync(dressing.Id);
        await SyncCleanAsync(doc);
        await desk.Engine.SyncAsync();

        // FR-BIL-11: the case's running bill before invoicing.
        var running = await billing.RunningBillAsync(patient.Id, caseId: physioCase.Id);
        Assert.Equal(3, running.Pending.Count);
        Assert.Equal(500m, running.Unbilled); // the two physio sessions are covered; the dressing is not

        var unbilled = await billing.UnbilledAsync(patient.Id);
        Assert.Equal(2, unbilled.Count(c => c.PackageId == package.Id));
        var invoice = await billing.CreateInvoiceAsync(patient.Id, branch, unbilled.Where(c => c.Source == ChargeSource.Procedure), caseId: physioCase.Id);
        var summary = await billing.SummaryAsync(invoice.Id);
        Assert.Equal(500m, summary.Invoice.Total);
        Assert.Equal(2, summary.Lines.Count(l => l.UnitPrice == 0 && l.Description.Contains("package")));
        await billing.FinaliseAsync(invoice.Id);
        await SyncCleanAsync(desk);
        var status = (await billing.PackagesAsync(patient.Id)).Single();
        Assert.Equal(2, status.Items.Single().Used);

        // The server counts sessions: a forged use beyond what the package holds is refused.
        var antenatal = (await billing.PlansAsync()).Single(p => p.Code == "antenatal");
        var (pkg2, inv2) = await billing.SellPackageAsync(patient.Id, branch, antenatal.Id);
        await billing.FinaliseAsync(inv2.Id);
        await SyncCleanAsync(desk);
        var labs = new LabService(doc.Store, new DynamicDataService(doc.Db));
        var fbs = (await labs.TestsAsync()).Single(t => t.Code == "fbs");
        var o1 = await labs.OrderAsync(patient.Id, branch, [fbs.Id]);
        var o2 = await labs.OrderAsync(patient.Id, branch, [fbs.Id]);
        await SyncCleanAsync(doc);
        await desk.Engine.SyncAsync();
        var labCharges = (await billing.UnbilledAsync(patient.Id)).Where(c => c.Source == ChargeSource.LabResult).ToList();
        Assert.Single(labCharges, c => c.PackageId == pkg2.Id); // antenatal includes one FBS
        var second = labCharges.Single(c => c.PackageId is null);
        await desk.Store.InsertAsync(new LocalPackageUsage
        {
            PatientPackageId = pkg2.Id, BranchId = branch, Kind = ServiceKind.Lab, Code = "fbs", Source = ChargeSource.LabResult,
            SourceId = second.SourceId!.Value, UsedAt = DateTime.UtcNow,
        });
        var first = labCharges.Single(c => c.PackageId is not null);
        await desk.Store.InsertAsync(new LocalPackageUsage
        {
            PatientPackageId = pkg2.Id, BranchId = branch, Kind = ServiceKind.Lab, Code = "fbs", Source = ChargeSource.LabResult,
            SourceId = first.SourceId!.Value, UsedAt = DateTime.UtcNow,
        });
        Assert.Contains("No lab sessions are left", await RejectionAsync(desk));
        _ = (o1, o2);
    }

    // ---------------------------------------------------------------------
    // FR-BIL-12 panels
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Panel_invoices_split_by_copay_and_the_payer_statement_ages_claims()
    {
        using var admin = await Factory.DemoAdminAsync();
        var payer = await (await admin.PostAsJsonAsync(AccountsRoutes.Payers, new SavePayerRequest(
            HttpExtensions.Unique("acme"), "Acme Textiles", PayerKind.Corporate, 20, 30, [new PayerPrice(ServiceKind.Consultation, null, 800)], null))).ReadAsync<PayerDto>();

        var doctor = await DoctorAsync();
        var (patient, _) = await WalkInAsync(doctor);
        await using var desk = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var billing = Billing(desk);
        var panel = await billing.AddPanelAsync(patient.Id, payer.Id, "ACME-4471", employer: "Acme Textiles");

        var invoice = await billing.CreateInvoiceAsync(patient.Id, desk.Session.BranchId, await billing.UnbilledAsync(patient.Id), panelId: panel.Id);
        invoice = await billing.FinaliseAsync(invoice.Id);
        Assert.Equal(800m, invoice.Total);   // payer price, not the doctor's Rs 1,000
        Assert.Equal(640m, invoice.PayerAmount);
        Assert.Equal(160m, invoice.PatientAmount);
        Assert.Equal(160m, (await billing.SummaryAsync(invoice.Id)).Due);
        await billing.PayAsync(invoice.Id, 160);
        Assert.True((await billing.SummaryAsync(invoice.Id)).IsPaid);
        await SyncCleanAsync(desk);

        // Statement, ageing and claim batch on the server; a settlement reduces what is owed.
        var from = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var to = DateTime.Today.ToString("yyyy-MM-dd");
        var statement = (await admin.GetFromJsonAsync<PayerStatementDto>($"{AccountsRoutes.Payers}/{payer.Id}/statement?from={from}&to={to}"))!;
        Assert.Equal(640m, statement.Claimed);
        Assert.Equal(640m, statement.Ageing.Current);
        var csv = await admin.GetStringAsync($"{AccountsRoutes.Payers}/{payer.Id}/claims.csv?from={from}&to={to}");
        Assert.Contains(invoice.InvoiceNo, csv);
        Assert.Contains("ACME-4471", csv);
        await (await admin.PostAsJsonAsync(AccountsRoutes.PayerSettlements, new SavePayerSettlementRequest(payer.Id, 400, DateTime.Today, "CHQ 0091", null))).EnsureSuccessAsync();
        statement = (await admin.GetFromJsonAsync<PayerStatementDto>($"{AccountsRoutes.Payers}/{payer.Id}/statement?from={from}&to={to}"))!;
        Assert.Equal(240m, statement.Outstanding);

        // The server checks the co-pay arithmetic.
        var forged = await billing.CreateInvoiceAsync(patient.Id, desk.Session.BranchId, panelId: panel.Id);
        await billing.AddManualAsync(forged.Id, "Medical certificate", 500);
        await SyncCleanAsync(desk);
        var header = (await desk.Store.GetAsync<LocalInvoice>(forged.Id))!;
        header.PayerAmount = 500;
        header.PatientAmount = 0;
        header.Status = InvoiceStatus.Final;
        await desk.Store.UpdateAsync(header);
        Assert.Contains("panel split should be", await RejectionAsync(desk));
    }

    // ---------------------------------------------------------------------
    // FR-BIL-13/14 deposits applied, credit notes and write-offs
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Credit_needs_another_users_approval_and_deposits_settle_invoices_without_cash()
    {
        var doctor = await DoctorAsync();
        var (patient, _) = await WalkInAsync(doctor);
        await using var desk = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        await using var accounts = await DeviceForAsync(await Factory.CreateUserAsync("Accountant"));
        var billing = Billing(desk);
        var branch = desk.Session.BranchId;

        await billing.DepositAsync(patient.Id, branch, 600);
        var invoice = await billing.CreateInvoiceAsync(patient.Id, branch, await billing.UnbilledAsync(patient.Id));
        await billing.FinaliseAsync(invoice.Id);
        var applied = await billing.ApplyCreditAsync(invoice.Id);
        Assert.Equal(600m, applied.Amount);
        Assert.Equal(400m, (await billing.SummaryAsync(invoice.Id)).Due);
        Assert.Equal(0m, (await billing.BalanceAsync(patient.Id)).Credit);
        await Assert.ThrowsAsync<BillingException>(() => billing.ApplyCreditAsync(invoice.Id));

        // Applying credit moves no cash: the drawer only holds the deposit.
        var mine = await billing.DaySummaryAsync(branch, DateTime.Today, desk.Session.Token.Me.UserId);
        Assert.Equal(600m, mine.Cash);

        var writeOff = await billing.RequestCreditAsync(invoice.Id, CreditKind.WriteOff, 400, "Deserving patient");
        await Assert.ThrowsAsync<BillingException>(() => billing.DecideCreditAsync(writeOff.Id, true)); // not your own
        await SyncCleanAsync(desk);

        // A desk user cannot approve on the server either (no Billing.Approve).
        var forged = (await desk.Store.GetAsync<LocalCreditNote>(writeOff.Id))!;
        forged.Status = ApprovalStatus.Approved;
        forged.DecidedBy = desk.Session.Token.Me.UserId;
        forged.DecidedAt = DateTime.UtcNow;
        await desk.Store.UpdateAsync(forged);
        Assert.Contains("Billing.Approve", await RejectionAsync(desk));

        await accounts.Engine.SyncAsync();
        var pending = await Billing(accounts).PendingCreditsAsync(branch);
        Assert.Contains(pending, c => c.Id == writeOff.Id);
        await Billing(accounts).DecideCreditAsync(writeOff.Id, true, "OK per policy");
        await SyncCleanAsync(accounts);
        await desk.Engine.SyncAsync();
        Assert.Equal(0m, (await billing.SummaryAsync(invoice.Id)).Due);
        Assert.Equal(0m, (await billing.BalanceAsync(patient.Id)).Balance);

        using var admin = await Factory.DemoAdminAsync();
        var server = (await admin.GetFromJsonAsync<PatientBalanceDto>($"{LabBillingRoutes.PatientBalance}/{patient.Id}"))!;
        Assert.Equal(0m, server.Balance);
    }

    // ---------------------------------------------------------------------
    // FR-DOC-04/05, FR-BIL-15/17/18 doctor share, tax, expenses, profit
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Doctor_share_follows_collections_and_refunds_and_reports_add_up()
    {
        using var admin = await Factory.DemoAdminAsync();
        var doctor = await DoctorAsync();
        await (await admin.PostAsJsonAsync(AccountsRoutes.ShareRules, new SaveShareRuleRequest(doctor.Profile.Id, ServiceKind.Consultation, 50, null))).EnsureSuccessAsync();
        var gst = await (await admin.PostAsJsonAsync(LabBillingRoutes.Services, new SaveServiceItemRequest(HttpExtensions.Unique("cosmetic"), "Cosmetic consult", ServiceKind.Other, null, 1000, 16)))
            .ReadAsync<ServiceItemDto>();

        var (patient, _) = await WalkInAsync(doctor);
        await using var desk = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        await using var accounts = await DeviceForAsync(await Factory.CreateUserAsync("Accountant"));
        var billing = Billing(desk);
        var invoice = await billing.CreateInvoiceAsync(patient.Id, desk.Session.BranchId, await billing.UnbilledAsync(patient.Id));
        await billing.AddServiceAsync(invoice.Id, gst.Id);
        invoice = await billing.FinaliseAsync(invoice.Id);
        Assert.Equal(2160m, invoice.Total); // 1000 consultation + 1000 + 16% tax
        await billing.PayAsync(invoice.Id, 2160);
        await SyncCleanAsync(desk);

        await accounts.Engine.SyncAsync();
        await Billing(accounts).RefundAsync(patient.Id, desk.Session.BranchId, invoice.Id, 540, "Partial refund");
        await new ExpenseService(accounts.Store).AddAsync(accounts.Session.BranchId, "utilities", 300, DateTime.Today, paidTo: "K-Electric");
        await SyncCleanAsync(accounts);

        // Collected 2160 − 540 = 1620 of 2160 (75%): the consultation line earned 75% of 1000, of which 50% is the share.
        var payout = await (await admin.PostAsJsonAsync(AccountsRoutes.Payouts, new CreatePayoutRequest(doctor.Profile.Id, DateTime.Today, DateTime.Today))).ReadAsync<PayoutDto>();
        Assert.Equal(PayoutStatus.Draft, payout.Status);
        Assert.Equal(375m, payout.Share);
        payout = await (await admin.PostAsync($"{AccountsRoutes.Payouts}/{payout.Id}/approve", null)).ReadAsync<PayoutDto>();
        payout = await (await admin.PostAsJsonAsync($"{AccountsRoutes.Payouts}/{payout.Id}/pay", new PayPayoutRequest("IBFT 7781"))).ReadAsync<PayoutDto>();
        Assert.Equal(PayoutStatus.Paid, payout.Status);
        var overlap = await admin.PostAsJsonAsync(AccountsRoutes.Payouts, new CreatePayoutRequest(doctor.Profile.Id, DateTime.Today, DateTime.Today));
        Assert.False(overlap.IsSuccessStatusCode);

        var day = DateTime.Today.ToString("yyyy-MM-dd");
        var tax = (await admin.GetFromJsonAsync<TaxSummaryDto>($"{AccountsRoutes.TaxSummary}?from={day}&to={day}"))!;
        Assert.Contains(tax.Lines, l => l.TaxPercent == 16 && l.Tax >= 160);
        var profit = (await admin.GetFromJsonAsync<ProfitDto>($"{AccountsRoutes.Profit}?from={day}&to={day}"))!;
        Assert.True(profit.Expenses >= 300);
        Assert.True(profit.ExpensesByCategory["utilities"] >= 300);
        Assert.True(profit.DoctorPayouts >= 375);
        Assert.Equal(profit.Collected - profit.Refunded + profit.PayerSettlements - profit.Expenses - profit.DoctorPayouts, profit.Profit);
    }

    // ---------------------------------------------------------------------
    // §4.14 messages
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Messages_queued_offline_are_sent_after_sync_and_respect_opt_out()
    {
        var doctor = await DoctorAsync();
        var (ok, _) = await WalkInAsync(doctor);
        var (unreachable, _) = await WalkInAsync(doctor, "+92 000 0000000");
        var (optedOut, _) = await WalkInAsync(doctor);
        await using var desk = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var messaging = new MessagingService(desk.Store);
        var branch = desk.Session.BranchId;
        desk.Transport.Offline = true;

        await messaging.SetPreferenceAsync(ok.Id, "ur", MessageChannel.WhatsApp, optOut: false);
        var values = new Dictionary<string, string?> { ["clinic"] = "Demo Clinic", ["date"] = DateTime.Today.ToString("dd MMM"), ["phone"] = "042-111" };
        var m1 = (await messaging.QueueAsync(ok.Id, branch, MessageTemplateKeys.FollowUp, values))!;
        Assert.Equal(MessageChannel.WhatsApp, m1.Channel);
        Assert.Contains("فالو اَپ", m1.Body);          // Urdu, the patient's language
        Assert.Contains(ok.FullName, m1.Body);
        var m2 = (await messaging.QueueAsync(unreachable.Id, branch, MessageTemplateKeys.FollowUp, values))!;

        // Queued, then the patient opts out before it is sent: the server skips it.
        var m3 = (await messaging.QueueAsync(optedOut.Id, branch, MessageTemplateKeys.LabReady, new Dictionary<string, string?> { ["test"] = "CBC", ["clinic"] = "Demo" }))!;
        await messaging.SetPreferenceAsync(optedOut.Id, "en", MessageChannel.Sms, optOut: true);
        Assert.Null(await messaging.QueueAsync(optedOut.Id, branch, MessageTemplateKeys.Birthday, values));

        desk.Transport.Offline = false;
        await SyncCleanAsync(desk);

        // The server's sender runs every second in tests; statuses come back with a pull.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await desk.Engine.SyncAsync();
            var statuses = new[] { m1.Id, m2.Id, m3.Id }.Select(id => desk.Store.GetAsync<LocalOutboundMessage>(id).Result!.Status).ToList();
            if (statuses.All(s => s != MessageStatus.Queued)) break;
            await Task.Delay(500);
        }
        Assert.Equal(MessageStatus.Sent, (await desk.Store.GetAsync<LocalOutboundMessage>(m1.Id))!.Status);
        Assert.Equal(MessageStatus.Failed, (await desk.Store.GetAsync<LocalOutboundMessage>(m2.Id))!.Status);
        Assert.Equal(MessageStatus.Skipped, (await desk.Store.GetAsync<LocalOutboundMessage>(m3.Id))!.Status);
        Assert.Contains(await desk.Patients.TimelineAsync(ok.Id), t => t.Kind == "message" && t.Title.Contains("sent"));
    }

    // ---------------------------------------------------------------------
    // §4.15 feedback and complaints
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Feedback_is_rated_per_doctor_and_complaints_are_worked_by_managers()
    {
        var doctor = await DoctorAsync();
        var (patient, _) = await WalkInAsync(doctor);
        await using var desk = await DeviceForAsync(await Factory.CreateUserAsync("Receptionist"));
        var feedback = new FeedbackService(desk.Store, new DynamicDataService(desk.Db));
        var branch = desk.Session.BranchId;

        await Assert.ThrowsAsync<CareException>(() => feedback.RecordAsync(patient.Id, branch, 6));
        await feedback.RecordAsync(patient.Id, branch, 4, doctorRating: 5, serviceRating: 3, comment: "Long wait", doctorId: doctor.Profile.Id);
        await feedback.RecordAsync(patient.Id, branch, 2, doctorRating: 3, doctorId: doctor.Profile.Id);
        var ratings = (await feedback.RatingsByDoctorAsync(branch)).Single(r => r.DoctorId == doctor.Profile.Id);
        Assert.Equal(3.0, ratings.Overall);
        Assert.Equal(4.0, ratings.Doctor);

        var complaint = await feedback.RaiseAsync(branch, "Waited 90 minutes", "Token 14, no update given", "waiting", patient.Id);
        await SyncCleanAsync(desk);

        // The desk cannot resolve complaints; a manager (tenant admin) can.
        await feedback.ResolveAsync(complaint.Id, "Apologised");
        Assert.Contains("Feedback.ManageComplaints", await RejectionAsync(desk));

        await using var manager = await ClientDevice.CreateAsync(await Factory.DemoAdminDeviceAsync());
        await manager.Engine.SyncAsync();
        var managerFeedback = new FeedbackService(manager.Store, new DynamicDataService(manager.Db));
        await managerFeedback.AssignAsync(complaint.Id, null, "Front desk lead");
        await managerFeedback.ResolveAsync(complaint.Id, "Called the patient; added a queue display.");
        await managerFeedback.CloseAsync(complaint.Id);
        await SyncCleanAsync(manager);
        await desk.Engine.SyncAsync();
        Assert.Equal(ComplaintStatus.Closed, (await desk.Store.GetAsync<LocalComplaint>(complaint.Id))!.Status);
    }

    // ---------------------------------------------------------------------
    // Pure rules
    // ---------------------------------------------------------------------

    [Fact]
    public void Shared_billing_extension_rules()
    {
        Assert.Equal((640m, 160m), PanelBilling.Split(800, 20));
        Assert.Equal((0m, 333m), PanelBilling.Split(333, 100));
        Assert.Equal((332m, 1m), PanelBilling.Split(333, 0.1m)); // the patient's share rounds up to the rupee

        var asOf = new DateTime(2026, 11, 30);
        var ageing = Ageing.Calculate([(new DateTime(2026, 8, 1), 500m), (new DateTime(2026, 10, 15), 300m), (new DateTime(2026, 11, 20), 200m)], 600, asOf);
        Assert.Equal(0m, ageing.Over90);       // the oldest 500 is paid first
        Assert.Equal(200m, ageing.Days31To60); // 300 − the remaining 100 of the settlement
        Assert.Equal(200m, ageing.Current);

        PackageItem[] items = [new(ServiceKind.Procedure, "physio_session", 10), new(ServiceKind.Consultation, null, 2)];
        Assert.Equal(9, Packages.Remaining(items, [(ServiceKind.Procedure, "physio_session")], ServiceKind.Procedure, "physio_session"));
        Assert.Equal(0, Packages.Remaining(items, [], ServiceKind.Procedure, "dressing"));
        Assert.Equal(1, Packages.Remaining(items, [(ServiceKind.Consultation, null)], ServiceKind.Consultation, null));

        var doctor = Guid.NewGuid();
        var share = DoctorShare.Calculate(
            [(Guid.NewGuid(), 2000m, 1000m, [("Consultation", ServiceKind.Consultation, 1000m, (Guid?)doctor), ("Lab", ServiceKind.Lab, 1000m, null)])],
            doctor, [new ShareRule(ServiceKind.Consultation, 40, null)]);
        Assert.Equal(200m, Assert.Single(share).Share); // half collected → 500 of the line → 40%

        Assert.Equal("Hi Ali, Rs 500 due {unknown}", TemplateText.Render("Hi {patient}, Rs {amount} due {unknown}", new Dictionary<string, string?> { ["patient"] = "Ali", ["amount"] = "500" }));
    }
}
