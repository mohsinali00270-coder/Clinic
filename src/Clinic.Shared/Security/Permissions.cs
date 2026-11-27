using System.Reflection;

namespace Clinic.Shared.Security;

/// <summary>
/// FR-AUTH-01: permissions are plain strings ("Area.Action"). Roles are editable bundles of these.
/// The catalogue lives in Clinic.Shared so the API (policies) and the app (hide/disable UI) agree.
/// Later phases add their areas here; the seeder picks them up automatically.
/// </summary>
public static class Permissions
{
    public static class Tenants
    {
        public const string View = "Tenants.View";
        public const string Manage = "Tenants.Manage";
    }

    public static class Branches
    {
        public const string View = "Branches.View";
        public const string Manage = "Branches.Manage";
    }

    public static class Users
    {
        public const string View = "Users.View";
        public const string Manage = "Users.Manage";
        public const string ResetPassword = "Users.ResetPassword";
    }

    public static class Roles
    {
        public const string View = "Roles.View";
        public const string Manage = "Roles.Manage";
    }

    public static class Devices
    {
        public const string View = "Devices.View";
        public const string Manage = "Devices.Manage";
    }

    public static class Audit
    {
        public const string View = "Audit.View";
    }

    /// <summary>Branch handover notes: the first offline-synced record type (Phase 2).</summary>
    public static class Notes
    {
        public const string View = "Notes.View";
        public const string Manage = "Notes.Manage";
    }

    public static class Sync
    {
        /// <summary>FR-OFF-32: sign out even though unsynced changes would be lost.</summary>
        public const string DiscardPending = "Sync.DiscardPending";
    }

    public static class DynamicFields
    {
        public const string Manage = "DynamicFields.Manage";
    }

    public static class Doctors
    {
        public const string Manage = "Doctors.Manage";
    }

    public static class Scheduling
    {
        /// <summary>Appointment types, branch holidays.</summary>
        public const string Manage = "Scheduling.Manage";
    }

    public static class Patients
    {
        public const string View = "Patients.View";
        public const string Create = "Patients.Create";
        public const string Edit = "Patients.Edit";
        public const string Merge = "Patients.Merge";
    }

    public static class Appointments
    {
        public const string View = "Appointments.View";
        public const string Book = "Appointments.Book";
        public const string CheckIn = "Appointments.CheckIn";
        public const string Overbook = "Appointments.Overbook";
    }

    public static class Queue
    {
        public const string Manage = "Queue.Manage";
    }

    public static class Encounters
    {
        public const string View = "Encounters.View";
        public const string Edit = "Encounters.Edit";
        public const string Sign = "Encounters.Sign";
    }

    public static class Vitals
    {
        public const string Record = "Vitals.Record";
    }

    public static class Prescriptions
    {
        public const string Create = "Prescriptions.Create";
        public const string Dispense = "Prescriptions.Dispense";
    }

    /// <summary>§4.10 patient cases (episodes of care): open, plan, team, notes, close.</summary>
    public static class Cases
    {
        public const string View = "Cases.View";
        public const string Manage = "Cases.Manage";
    }

    /// <summary>§4.11 internal, external and incoming referrals.</summary>
    public static class Referrals
    {
        public const string View = "Referrals.View";
        public const string Manage = "Referrals.Manage";
    }

    /// <summary>§4.12 procedures with consent.</summary>
    public static class Procedures
    {
        public const string View = "Procedures.View";
        public const string Perform = "Procedures.Perform";
    }

    /// <summary>§4.12 day-care / short-stay admissions.</summary>
    public static class Admissions
    {
        public const string View = "Admissions.View";
        public const string Manage = "Admissions.Manage";
    }

    public static class Nursing
    {
        /// <summary>Nursing notes and medication chart doses of admitted patients.</summary>
        public const string Record = "Nursing.Record";
    }

    public static class CareSetup
    {
        /// <summary>Case types, procedure master, beds and rooms.</summary>
        public const string Manage = "CareSetup.Manage";
    }

    public static class Expenses
    {
        public const string Manage = "Expenses.Manage";
    }

    public static class Messages
    {
        /// <summary>FR-COM-03: queue messages to patients.</summary>
        public const string Send = "Messages.Send";
    }

    public static class Feedback
    {
        public const string Record = "Feedback.Record";

        /// <summary>FR-FBK-02: assign, work and resolve complaints.</summary>
        public const string ManageComplaints = "Feedback.ManageComplaints";
    }

    public static class Labs
    {
        public const string View = "Labs.View";
        public const string Order = "Labs.Order";
        public const string EnterResults = "Labs.EnterResults";
        public const string Verify = "Labs.Verify";
    }

    public static class Attachments
    {
        public const string Upload = "Attachments.Upload";
    }

    public static class Billing
    {
        public const string View = "Billing.View";
        public const string Collect = "Billing.Collect";
        public const string Discount = "Billing.Discount";
        public const string Refund = "Billing.Refund";
        public const string CashClose = "Billing.CashClose";

        /// <summary>FR-BIL-14: approve credit notes and write-offs (someone other than the requester).</summary>
        public const string Approve = "Billing.Approve";

        /// <summary>FR-BIL-12 / FR-DOC-05: payers, claims, payer settlements and doctor payouts (online, accounts).</summary>
        public const string Accounts = "Billing.Accounts";
    }

    public static class Inventory
    {
        public const string View = "Inventory.View";
        public const string StockIn = "Inventory.StockIn";
        public const string StockOut = "Inventory.StockOut";
    }

    public static class Reports
    {
        public const string View = "Reports.View";
        public const string Financial = "Reports.Financial";
    }

    /// <summary>Permissions only a SaaS owner (no tenant) can hold.</summary>
    public static readonly IReadOnlySet<string> PlatformOnly = new HashSet<string>
    {
        Tenants.View, Tenants.Manage
    };

    /// <summary>Every permission in the catalogue, discovered from the nested constants.</summary>
    public static readonly IReadOnlyList<string> All = typeof(Permissions)
        .GetNestedTypes(BindingFlags.Public | BindingFlags.Static)
        .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .OrderBy(p => p)
        .ToList();

    /// <summary>Permissions assignable to roles inside a tenant.</summary>
    public static IEnumerable<string> TenantAssignable => All.Where(p => !PlatformOnly.Contains(p));

    public static bool IsKnown(string permission) => All.Contains(permission);
}
