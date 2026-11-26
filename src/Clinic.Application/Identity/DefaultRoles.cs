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
    public const int CatalogVersion = 2;

    private static readonly Dictionary<string, int> IntroducedIn = new()
    {
        [P.Notes.View] = 1,
        [P.Notes.Manage] = 1,
        [P.Sync.DiscardPending] = 1,
        [P.Doctors.Manage] = 2,
        [P.Scheduling.Manage] = 2,
    };

    public static int VersionOf(string permission) => IntroducedIn.GetValueOrDefault(permission, 0);

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
        ]),

        new("Nurse / Assistant", "Vitals, triage, queue updates, attach files",
        [
            G(P.Patients.View, Branch), G(P.Appointments.View, Branch), G(P.Queue.Manage, Branch),
            G(P.Encounters.View, Branch), G(P.Vitals.Record, Branch), G(P.Attachments.Upload, Branch), .. Notes,
        ]),

        new("Receptionist", "Register patients, book/reschedule, check-in, collect payments",
        [
            G(P.Patients.View, Branch), G(P.Patients.Create, Branch), G(P.Patients.Edit, Branch),
            G(P.Appointments.View, Branch), G(P.Appointments.Book, Branch), G(P.Appointments.CheckIn, Branch),
            G(P.Queue.Manage, Branch), G(P.Billing.View, Branch), G(P.Billing.Collect, Branch),
            G(P.Attachments.Upload, Branch), .. Notes,
        ]),

        new("Lab Technician", "Receive orders, enter results, upload reports",
        [
            G(P.Patients.View, Branch), G(P.Labs.EnterResults, Branch), G(P.Labs.Verify, Branch), .. Notes,
            G(P.Attachments.Upload, Branch),
        ]),

        new("Pharmacist", "Dispense against prescriptions, stock in/out",
        [
            G(P.Patients.View, Branch), G(P.Prescriptions.Dispense, Branch),
            G(P.Inventory.View, Branch), G(P.Inventory.StockIn, Branch), G(P.Inventory.StockOut, Branch), .. Notes,
        ]),

        new("Accountant", "Invoices, payments, refunds, financial reports",
        [
            G(P.Patients.View, Branch), G(P.Billing.View, Branch), G(P.Billing.Collect, Branch),
            G(P.Billing.Discount, Branch), G(P.Billing.Refund, Branch), G(P.Billing.CashClose, Branch),
            G(P.Reports.View, Branch), G(P.Reports.Financial, Branch), .. Notes,
        ]),
    ];

    /// <summary>What the platform Super Admin holds.</summary>
    public static IReadOnlyList<GrantedPermission> SuperAdminPermissions { get; } =
        P.PlatformOnly.Select(p => G(p)).ToList();
}
