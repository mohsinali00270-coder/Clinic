using Clinic.Shared.Contracts;
using Clinic.Shared.Security;
using P = Clinic.Shared.Security.Permissions;

namespace Clinic.Application.Identity;

/// <summary>Seed roles from §2 of the spec. Admins can edit them per tenant afterwards.</summary>
public static class DefaultRoles
{
    public const string TenantAdmin = "Tenant Admin";

    public sealed record Definition(string Name, string Description, IReadOnlyList<GrantedPermission> Permissions);

    private static GrantedPermission G(string name, PermissionScope scope = PermissionScope.All) => new(name, scope);

    private const PermissionScope Own = PermissionScope.OwnRecords;
    private const PermissionScope Branch = PermissionScope.OwnBranch;

    // Declared before All: static initializers run in source order.
    private static readonly GrantedPermission[] Notes =
        [G(P.Notes.View, Branch), G(P.Notes.Manage, Branch)];

    /// <summary>
    /// Bump when the catalogue gains permissions that built-in roles should receive. Existing tenants are
    /// upgraded at startup: each built-in role gets the newly introduced permissions from its definition,
    /// and nothing an admin removed earlier is re-added.
    /// </summary>
    public const int CatalogVersion = 6;

    private static readonly Dictionary<string, int> IntroducedIn = new()
    {
        [P.Notes.View] = 1,
        [P.Notes.Manage] = 1,
        [P.Sync.DiscardPending] = 1,
        [P.Doctors.Manage] = 2,
        [P.Scheduling.Manage] = 2,
        [P.Cases.View] = 3,
        [P.Cases.Manage] = 3,
        [P.Referrals.View] = 3,
        [P.Referrals.Manage] = 3,
        [P.Procedures.View] = 3,
        [P.Procedures.Perform] = 3,
        [P.Admissions.View] = 3,
        [P.Admissions.Manage] = 3,
        [P.Nursing.Record] = 3,
        [P.CareSetup.Manage] = 3,
        [P.Labs.View] = 4,
        [P.Billing.Approve] = 5,
        [P.Billing.Accounts] = 5,
        [P.Expenses.Manage] = 5,
        [P.Messages.Send] = 5,
        [P.Feedback.Record] = 5,
        [P.Feedback.ManageComplaints] = 5,
    };

    public static int VersionOf(string permission) => IntroducedIn.GetValueOrDefault(permission, 0);

    /// <summary>
    /// Existing permissions granted to a built-in role in a later catalogue version. Without this, an upgrade would
    /// only hand out permissions that are new to the catalogue, never an older permission newly given to a role.
    /// </summary>
    private static readonly Dictionary<(string Role, string Permission), int> GrantedIn = new()
    {
        [("Receptionist", P.Procedures.View)] = 4,
        [("Receptionist", P.Billing.CashClose)] = 4,
        [("Accountant", P.Procedures.View)] = 4,
        [("Accountant", P.Admissions.View)] = 4,
        [("Accountant", P.Appointments.View)] = 4,
        [("Pharmacist", P.Encounters.View)] = 6,
        [("Pharmacist", P.Billing.View)] = 6,
        [("Pharmacist", P.Billing.Collect)] = 6,
        [("Pharmacist", P.Billing.CashClose)] = 6,
        [("Pharmacist", P.Reports.View)] = 6,
        [("Receptionist", P.Reports.View)] = 6,
        [("Nurse / Assistant", P.Inventory.View)] = 6,
        [("Nurse / Assistant", P.Inventory.StockOut)] = 6,
        [("Accountant", P.Inventory.View)] = 6,
        [("Accountant", P.Encounters.View)] = 6,
    };

    /// <summary>The catalogue version in which this role received this permission.</summary>
    public static int VersionOf(string role, string permission) =>
        Math.Max(VersionOf(permission), GrantedIn.GetValueOrDefault((role, permission), 0));

    public static IReadOnlyList<Definition> All { get; } =
    [
        new(TenantAdmin, "Branches, users, roles, dynamic fields/forms, lookups, print templates, fees",
            P.TenantAssignable.Select(p => G(p)).ToList()),

        new("Doctor", "Own patients' encounters, diagnose, prescribe, order labs, sign notes",
        [
            G(P.Patients.View, Own), G(P.Patients.Edit, Own),
            G(P.Appointments.View, Own), G(P.Appointments.Book, Own), G(P.Queue.Manage, Own),
            G(P.Encounters.View, Own), G(P.Encounters.Edit, Own), G(P.Encounters.Sign, Own),
            G(P.Vitals.Record, Own), G(P.Prescriptions.Create, Own), G(P.Labs.Order, Own),
            G(P.Attachments.Upload, Own), G(P.Reports.View, Own), .. Notes,
            // Phase 5b: cases are team care, so doctors work on their branch's cases, referrals and procedures.
            G(P.Cases.View, Branch), G(P.Cases.Manage, Branch), G(P.Referrals.View, Branch), G(P.Referrals.Manage, Branch),
            G(P.Procedures.View, Branch), G(P.Procedures.Perform, Branch), G(P.Admissions.View, Branch), G(P.Admissions.Manage, Branch),
            G(P.Labs.View, Branch), G(P.Messages.Send, Branch),
        ]),

        new("Nurse / Assistant", "Vitals, triage, queue updates, attach files",
        [
            G(P.Patients.View, Branch), G(P.Appointments.View, Branch), G(P.Queue.Manage, Branch),
            G(P.Encounters.View, Branch), G(P.Vitals.Record, Branch), G(P.Attachments.Upload, Branch), .. Notes,
            G(P.Cases.View, Branch), G(P.Procedures.View, Branch), G(P.Procedures.Perform, Branch),
            G(P.Admissions.View, Branch), G(P.Admissions.Manage, Branch), G(P.Nursing.Record, Branch), G(P.Labs.View, Branch),
            G(P.Feedback.Record, Branch), G(P.Inventory.View, Branch), G(P.Inventory.StockOut, Branch),
        ]),

        new("Receptionist", "Register patients, book/reschedule, check-in, collect payments",
        [
            G(P.Patients.View, Branch), G(P.Patients.Create, Branch), G(P.Patients.Edit, Branch),
            G(P.Appointments.View, Branch), G(P.Appointments.Book, Branch), G(P.Appointments.CheckIn, Branch),
            G(P.Queue.Manage, Branch), G(P.Billing.View, Branch), G(P.Billing.Collect, Branch),
            G(P.Attachments.Upload, Branch), .. Notes,
            G(P.Cases.View, Branch), G(P.Referrals.View, Branch), G(P.Referrals.Manage, Branch), G(P.Admissions.View, Branch),
            // Phase 6: reception bills visits, procedures, labs and day care, and closes its own cash.
            G(P.Labs.View, Branch), G(P.Procedures.View, Branch), G(P.Billing.CashClose, Own),
            // Phase 6b: reminders to patients, feedback and complaints at the desk.
            G(P.Messages.Send, Branch), G(P.Feedback.Record, Branch), G(P.Reports.View, Branch),
        ]),

        new("Lab Technician", "Receive orders, enter results, upload reports",
        [
            G(P.Patients.View, Branch), G(P.Labs.View, Branch), G(P.Labs.EnterResults, Branch), G(P.Labs.Verify, Branch), .. Notes,
            G(P.Attachments.Upload, Branch),
        ]),

        new("Pharmacist", "Dispense against prescriptions, stock in/out",
        [
            G(P.Patients.View, Branch), G(P.Prescriptions.Dispense, Branch),
            G(P.Inventory.View, Branch), G(P.Inventory.StockIn, Branch), G(P.Inventory.StockOut, Branch), .. Notes,
            // Phase 7: reads prescriptions to dispense them, and bills and collects at the pharmacy counter.
            G(P.Encounters.View, Branch), G(P.Billing.View, Branch), G(P.Billing.Collect, Branch), G(P.Billing.CashClose, Own),
            G(P.Reports.View, Branch),
        ]),

        new("Accountant", "Invoices, payments, refunds, financial reports",
        [
            G(P.Patients.View, Branch), G(P.Billing.View, Branch), G(P.Billing.Collect, Branch),
            G(P.Billing.Discount, Branch), G(P.Billing.Refund, Branch), G(P.Billing.CashClose, Branch),
            G(P.Reports.View, Branch), G(P.Reports.Financial, Branch), .. Notes,
            G(P.Labs.View, Branch), G(P.Procedures.View, Branch), G(P.Admissions.View, Branch), G(P.Appointments.View, Branch),
            // Phase 6b: approvals, payer accounts, doctor payouts, expenses.
            G(P.Billing.Approve, Branch), G(P.Billing.Accounts), G(P.Expenses.Manage, Branch), G(P.Messages.Send, Branch),
            G(P.Inventory.View, Branch), G(P.Encounters.View, Branch),
        ]),
    ];

    /// <summary>What the platform Super Admin holds.</summary>
    public static IReadOnlyList<GrantedPermission> SuperAdminPermissions { get; } =
        P.PlatformOnly.Select(p => G(p)).ToList();
}
