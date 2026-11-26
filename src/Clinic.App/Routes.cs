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

    // Form builder (Tenant Admin)
    public const string Forms = "forms";
    public const string FormDesigner = "form-designer";
    public const string FieldEditor = "field-editor";
    public const string FormPreview = "form-preview";
    public const string Lookups = "lookups";
    public const string LookupEdit = "lookup-edit";
}
