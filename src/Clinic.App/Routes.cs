namespace Clinic.App;

public static class Routes
{
    public const string Login = "login";
    public const string Home = "home";
    public const string Branches = "branches";
    public const string Users = "users";
    public const string Devices = "devices";
    public const string Notes = "notes";
    public const string SyncCenter = "sync-center";
    public const string Unlock = "unlock";

    // Pushed pages (not in the flyout)
    public const string BranchEdit = "branch-edit";
    public const string UserEdit = "user-edit";
    public const string NoteEdit = "note-edit";

    // Patients and scheduling
    public const string Patients = "patients";
    public const string Appointments = "appointments";
    public const string Queue = "queue";
    public const string Doctors = "doctors";
    public const string Patient = "patient";
    public const string PatientEdit = "patient-edit";
    public const string Booking = "booking";
    public const string DoctorEdit = "doctor-edit";
    public const string SchedulingSetup = "scheduling-setup";
    public const string Encounter = "encounter";

    // Phase 5b: cases, referrals, procedures, day care
    public const string FollowUps = "follow-ups";
    public const string DayCare = "day-care";
    public const string CareSetup = "care-setup";
    public const string Case = "case";
    public const string Referral = "referral";
    public const string Procedure = "procedure";
    public const string Admission = "admission";

    // Phase 6: labs and billing
    public const string LabWork = "lab";
    public const string CashClosing = "cash-closing";
    public const string LabResult = "lab-result";
    public const string PatientBilling = "patient-billing";
    public const string Invoice = "invoice";

    // Phase 6b
    public const string Approvals = "approvals";
    public const string Expenses = "expenses";
    public const string Complaints = "complaints";

    // Phase 7
    public const string Stock = "stock";
    public const string Reports = "reports";
    public const string Dispense = "dispense";

    // Phase 8
    public const string Conflict = "conflict";
    public const string Audit = "audit";
    public const string Duplicates = "duplicates";

    // Form builder (Tenant Admin)
    public const string Forms = "forms";
    public const string FormDesigner = "form-designer";
    public const string FieldEditor = "field-editor";
    public const string FormPreview = "form-preview";
    public const string Lookups = "lookups";
    public const string LookupEdit = "lookup-edit";
}
