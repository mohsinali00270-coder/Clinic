using Clinic.Application.Abstractions;
using Clinic.Application.Clinical;
using Clinic.Application.Forms;
using Clinic.Domain.Clinical;
using Clinic.Domain.Forms;
using Clinic.Domain.Notes;
using Clinic.Domain.Sync;
using Clinic.Domain.Tenancy;
using Clinic.Shared.Security;
using Clinic.Shared.Sync;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Sync;

/// <summary>§7.4: what happens when a pushed field was also changed by someone else since the device's base.</summary>
public enum ConflictPolicy
{
    /// <summary>Non-clinical data: the change received last wins; the old value stays in the audit log.</summary>
    LastWriterWins,

    /// <summary>Master/config data: devices cannot overwrite the server.</summary>
    ServerWins,

    /// <summary>Clinical/financial data: nothing is overwritten; a person chooses.</summary>
    Manual,
}

/// <summary>When a rule runs: before an update is applied (old values), on the final state, or for a delete.</summary>
public enum SyncStage
{
    Final,
    BeforeChange,
    Delete,
}

public sealed record SyncWriteContext(object Entity, bool IsNew, ICurrentUser User, IAppDbContext Db, SyncStage Stage = SyncStage.Final);

public sealed record SyncReadContext(ICurrentUser User, IAppDbContext Db);

/// <summary>How one entity type takes part in sync.</summary>
public sealed class SyncEntityDescriptor
{
    /// <summary>WritePermission marker: the descriptor's Authorize decides (e.g. different permissions for insert and edit).</summary>
    public const string CustomPermission = "*";

    public required string Name { get; init; }
    public required Type ClrType { get; init; }

    /// <summary>FR-SYN-22 pull order: config → masters → patients → … (lower first).</summary>
    public required int Order { get; init; }

    public string? ViewPermission { get; init; }
    public string? WritePermission { get; init; }
    public bool ClientWritable => WritePermission is not null;
    public ConflictPolicy Policy { get; init; } = ConflictPolicy.ServerWins;

    /// <summary>Optional per-record policy (e.g. clinical FieldValues are Manual, others last-writer-wins).</summary>
    public Func<object, ConflictPolicy>? PolicyFor { get; init; }

    public ConflictPolicy PolicyOf(object entity) => PolicyFor?.Invoke(entity) ?? Policy;

    /// <summary>camelCase properties a device may set. Id, tenant and bookkeeping columns are never writable.</summary>
    public IReadOnlySet<string> WritableFields { get; init; } = new HashSet<string>();

    /// <summary>
    /// camelCase properties never sent to devices. A save that changes only hidden properties does not create a
    /// ChangeLog entry (e.g. a server-maintained snapshot).
    /// </summary>
    public IReadOnlySet<string> HiddenFields { get; init; } = new HashSet<string>();

    /// <summary>An insert of an id that already exists is applied as an update (deterministic ids, FieldValue).</summary>
    public bool UpsertOnInsert { get; init; }

    /// <summary>FR-SYN-13: server-side re-validation. Returns an error message, or null when valid.</summary>
    public Func<object, string?> Validate { get; init; } = _ => null;

    /// <summary>Row-level permission/scope check (branch, ownership). Returns an error message, or null.</summary>
    public Func<SyncWriteContext, Task<string?>> Authorize { get; init; } = _ => Task.FromResult<string?>(null);

    /// <summary>Runs after an applied change is saved, inside the same transaction (derived data).</summary>
    public Func<SyncWriteContext, Task>? AfterApplyAsync { get; init; }

    /// <summary>Removes rows the caller may not see (e.g. field-level view permissions).</summary>
    public Func<IReadOnlyList<ISyncEntity>, SyncReadContext, Task<IReadOnlyList<ISyncEntity>>>? FilterPullAsync { get; init; }
}

public static class SyncRegistry
{
    public static IReadOnlyList<SyncEntityDescriptor> All { get; } =
    [
        new() { Name = SyncEntityNames.Branch, ClrType = typeof(Branch), Order = 0 },

        // Dynamic form configuration (server wins, FR-OFF-24): lookups before the forms that use them.
        new() { Name = SyncEntityNames.LookupList, ClrType = typeof(LookupList), Order = 10 },
        new() { Name = SyncEntityNames.LookupItem, ClrType = typeof(LookupItem), Order = 11 },
        new() { Name = SyncEntityNames.FormVersion, ClrType = typeof(FormVersion), Order = 20 },

        // Scheduling masters (server wins; edited through the admin API).
        new() { Name = SyncEntityNames.DoctorProfile, ClrType = typeof(DoctorProfile), Order = 30, HiddenFields = new HashSet<string> { "dynamicData" } },
        new() { Name = SyncEntityNames.DoctorSchedule, ClrType = typeof(DoctorSchedule), Order = 31 },
        new() { Name = SyncEntityNames.DoctorLeave, ClrType = typeof(DoctorLeave), Order = 32 },
        new() { Name = SyncEntityNames.BranchClosure, ClrType = typeof(BranchClosure), Order = 33 },
        new() { Name = SyncEntityNames.AppointmentType, ClrType = typeof(AppointmentType), Order = 34 },

        // Patients → appointments → their history → files (FR-SYN-22 order).
        new()
        {
            Name = SyncEntityNames.Patient,
            ClrType = typeof(Patient),
            Order = 200,
            ViewPermission = Permissions.Patients.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = new HashSet<string> { "mrn", "fullName", "gender", "dateOfBirth", "dobEstimated", "phone", "branchId", "formVersionId" },
            HiddenFields = new HashSet<string> { "dynamicData" },
            Validate = ClinicalSyncRules.ValidatePatient,
            Authorize = ClinicalSyncRules.AuthorizePatientAsync,
        },
        new()
        {
            Name = SyncEntityNames.Appointment,
            ClrType = typeof(Appointment),
            Order = 300,
            ViewPermission = Permissions.Appointments.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = new HashSet<string>
            {
                "patientId", "doctorId", "branchId", "typeId", "date", "startTime", "durationMinutes", "status", "channel",
                "priority", "tokenNo", "checkedInAt", "calledAt", "completedAt", "cancelReason", "cancelledBy",
                "rescheduleCount", "rescheduledFromId", "notes", "formVersionId",
            },
            HiddenFields = new HashSet<string> { "dynamicData" },
            Validate = ClinicalSyncRules.ValidateAppointment,
            Authorize = ClinicalSyncRules.AuthorizeAppointmentAsync,
            AfterApplyAsync = ClinicalSyncRules.AppointmentAfterApplyAsync,
        },
        new()
        {
            Name = SyncEntityNames.AppointmentStatusHistory,
            ClrType = typeof(AppointmentStatusHistory),
            Order = 310,
            ViewPermission = Permissions.Appointments.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.ServerWins,
            WritableFields = new HashSet<string> { "appointmentId", "fromStatus", "toStatus", "changedAt", "changedBy", "reason" },
            Validate = ClinicalSyncRules.ValidateHistory,
            Authorize = ClinicalSyncRules.AuthorizeHistoryAsync,
        },
        // Consultations (FR-SYN-22: after appointments, before files).
        new() { Name = SyncEntityNames.Icd10Code, ClrType = typeof(Icd10Code), Order = 40 },
        new() { Name = SyncEntityNames.Drug, ClrType = typeof(Drug), Order = 41 },
        new()
        {
            Name = SyncEntityNames.Encounter,
            ClrType = typeof(Encounter),
            Order = 320,
            ViewPermission = Permissions.Encounters.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = new HashSet<string>
            {
                "patientId", "doctorId", "branchId", "appointmentId", "specialty", "startedAt", "status", "signedAt", "signedBy",
                "followUpDate", "formVersionId", "patientGender", "patientDateOfBirth", "caseId",
            },
            HiddenFields = new HashSet<string> { "dynamicData" },
            Validate = EncounterSyncRules.ValidateEncounter,
            Authorize = EncounterSyncRules.AuthorizeEncounterAsync,
        },
        new()
        {
            Name = SyncEntityNames.Vitals,
            ClrType = typeof(VitalsRecord),
            Order = 321,
            ViewPermission = Permissions.Encounters.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = new HashSet<string> { "patientId", "encounterId", "branchId", "recordedAt", "recordedBy", "patientGender", "patientDateOfBirth", "formVersionId" },
            HiddenFields = new HashSet<string> { "dynamicData" },
            Authorize = EncounterSyncRules.AuthorizeVitalsAsync,
        },
        new()
        {
            Name = SyncEntityNames.EncounterAmendment,
            ClrType = typeof(EncounterAmendment),
            Order = 322,
            ViewPermission = Permissions.Encounters.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            WritableFields = new HashSet<string> { "encounterId", "fieldKey", "oldValue", "newValue", "reason", "amendedAt", "amendedBy" },
            Validate = EncounterSyncRules.ValidateAmendment,
            Authorize = EncounterSyncRules.AuthorizeAmendmentAsync,
        },
        new()
        {
            Name = SyncEntityNames.Diagnosis,
            ClrType = typeof(Diagnosis),
            Order = 323,
            ViewPermission = Permissions.Encounters.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.Manual,
            WritableFields = new HashSet<string> { "encounterId", "code", "description", "isPrimary", "notes" },
            Validate = EncounterSyncRules.ValidateDiagnosis,
            Authorize = EncounterSyncRules.AuthorizeDiagnosisAsync,
        },
        new()
        {
            Name = SyncEntityNames.Prescription,
            ClrType = typeof(Prescription),
            Order = 324,
            ViewPermission = Permissions.Encounters.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.Manual,
            WritableFields = new HashSet<string> { "encounterId", "notes", "issuedAt" },
            Authorize = EncounterSyncRules.AuthorizePrescriptionAsync,
        },
        new()
        {
            Name = SyncEntityNames.PrescriptionItem,
            ClrType = typeof(PrescriptionItem),
            Order = 325,
            ViewPermission = Permissions.Encounters.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.Manual,
            WritableFields = new HashSet<string>
            {
                "prescriptionId", "drugId", "drugName", "strength", "form", "dose", "frequency", "durationDays", "route", "instructions", "sortOrder",
            },
            Validate = EncounterSyncRules.ValidatePrescriptionItem,
            Authorize = EncounterSyncRules.AuthorizePrescriptionItemAsync,
        },
        new()
        {
            Name = SyncEntityNames.PrescriptionTemplate,
            ClrType = typeof(PrescriptionTemplate),
            Order = 326,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = new HashSet<string> { "doctorId", "name", "itemsJson" },
            Authorize = EncounterSyncRules.AuthorizeTemplateAsync,
        },
        // Phase 5b masters (server wins; edited through the care setup API).
        new() { Name = SyncEntityNames.CaseType, ClrType = typeof(CaseType), Order = 42 },
        new() { Name = SyncEntityNames.ProcedureType, ClrType = typeof(ProcedureType), Order = 43 },
        new() { Name = SyncEntityNames.Bed, ClrType = typeof(Bed), Order = 44 },

        // Section 4.10 cases: before encounters, which link to them.
        new()
        {
            Name = SyncEntityNames.Case,
            ClrType = typeof(PatientCase),
            Order = 315,
            ViewPermission = Permissions.Cases.View,
            WritePermission = Permissions.Cases.Manage,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.Case,
            HiddenFields = new HashSet<string> { "dynamicData" },
            Validate = CareSyncRules.ValidateCase,
            Authorize = CareSyncRules.AuthorizeCaseAsync,
        },
        new()
        {
            Name = SyncEntityNames.CarePlanStep,
            ClrType = typeof(CarePlanStep),
            Order = 316,
            ViewPermission = Permissions.Cases.View,
            WritePermission = Permissions.Cases.Manage,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.CarePlanStep,
            Validate = CareSyncRules.ValidateStep,
            Authorize = CareSyncRules.AuthorizeStepAsync,
        },
        new()
        {
            Name = SyncEntityNames.CaseMember,
            ClrType = typeof(CaseMember),
            Order = 317,
            ViewPermission = Permissions.Cases.View,
            WritePermission = Permissions.Cases.Manage,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.CaseMember,
            Validate = CareSyncRules.ValidateMember,
            Authorize = CareSyncRules.AuthorizeMemberAsync,
        },
        new()
        {
            Name = SyncEntityNames.CaseNote,
            ClrType = typeof(CaseNote),
            Order = 318,
            ViewPermission = Permissions.Cases.View,
            WritePermission = Permissions.Cases.Manage,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.CaseNote,
            Validate = CareSyncRules.ValidateCaseNote,
            Authorize = CareSyncRules.AuthorizeCaseNoteAsync,
        },

        // Section 4.12 day care, then procedures (which may belong to an admission), then referrals (4.11).
        new()
        {
            Name = SyncEntityNames.Admission,
            ClrType = typeof(Admission),
            Order = 330,
            ViewPermission = Permissions.Admissions.View,
            WritePermission = Permissions.Admissions.Manage,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.Admission,
            Validate = CareSyncRules.ValidateAdmission,
            Authorize = CareSyncRules.AuthorizeAdmissionAsync,
            AfterApplyAsync = CareSyncRules.AdmissionAfterApplyAsync,
        },
        new()
        {
            Name = SyncEntityNames.BedTransfer,
            ClrType = typeof(BedTransfer),
            Order = 331,
            ViewPermission = Permissions.Admissions.View,
            WritePermission = Permissions.Admissions.Manage,
            WritableFields = CareSyncFields.BedTransfer,
            Authorize = CareSyncRules.AuthorizeTransferAsync,
        },
        new()
        {
            Name = SyncEntityNames.NursingNote,
            ClrType = typeof(NursingNote),
            Order = 332,
            ViewPermission = Permissions.Admissions.View,
            WritePermission = Permissions.Nursing.Record,
            Policy = ConflictPolicy.Manual,
            WritableFields = CareSyncFields.NursingNote,
            Validate = CareSyncRules.ValidateNursingNote,
            Authorize = CareSyncRules.AuthorizeNursingNoteAsync,
        },
        new()
        {
            Name = SyncEntityNames.MedicationOrder,
            ClrType = typeof(MedicationOrder),
            Order = 333,
            ViewPermission = Permissions.Admissions.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.Manual,
            WritableFields = CareSyncFields.MedicationOrder,
            Validate = CareSyncRules.ValidateMedicationOrder,
            Authorize = CareSyncRules.AuthorizeMedicationOrderAsync,
        },
        new()
        {
            Name = SyncEntityNames.MedicationDose,
            ClrType = typeof(MedicationDose),
            Order = 334,
            ViewPermission = Permissions.Admissions.View,
            WritePermission = Permissions.Nursing.Record,
            Policy = ConflictPolicy.Manual,
            WritableFields = CareSyncFields.MedicationDose,
            Validate = CareSyncRules.ValidateDose,
            Authorize = CareSyncRules.AuthorizeDoseAsync,
        },
        new()
        {
            Name = SyncEntityNames.Procedure,
            ClrType = typeof(ProcedureRecord),
            Order = 335,
            ViewPermission = Permissions.Procedures.View,
            WritePermission = Permissions.Procedures.Perform,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.Procedure,
            HiddenFields = new HashSet<string> { "dynamicData" },
            Validate = CareSyncRules.ValidateProcedure,
            Authorize = CareSyncRules.AuthorizeProcedureAsync,
        },
        new()
        {
            Name = SyncEntityNames.Referral,
            ClrType = typeof(Referral),
            Order = 340,
            ViewPermission = Permissions.Referrals.View,
            WritePermission = Permissions.Referrals.Manage,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.Referral,
            Validate = CareSyncRules.ValidateReferral,
            Authorize = CareSyncRules.AuthorizeReferralAsync,
        },
        // Phase 6 masters (server wins).
        new() { Name = SyncEntityNames.LabTest, ClrType = typeof(LabTest), Order = 45 },
        new() { Name = SyncEntityNames.ServiceItem, ClrType = typeof(ServiceItem), Order = 46 },

        // Section 4.5 labs, after procedures; section 4.6 billing last, since invoices point at everything above.
        new()
        {
            Name = SyncEntityNames.LabOrder,
            ClrType = typeof(LabOrder),
            Order = 350,
            ViewPermission = Permissions.Labs.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.LabOrder,
            Validate = LabBillingSyncRules.ValidateLabOrder,
            Authorize = LabBillingSyncRules.AuthorizeLabOrderAsync,
        },
        new()
        {
            Name = SyncEntityNames.LabResult,
            ClrType = typeof(LabResult),
            Order = 351,
            ViewPermission = Permissions.Labs.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.LabResult,
            HiddenFields = new HashSet<string> { "dynamicData" },
            Validate = LabBillingSyncRules.ValidateLabResult,
            Authorize = LabBillingSyncRules.AuthorizeLabResultAsync,
        },
        new()
        {
            Name = SyncEntityNames.Invoice,
            ClrType = typeof(Invoice),
            Order = 360,
            ViewPermission = Permissions.Billing.View,
            WritePermission = Permissions.Billing.Collect,
            Policy = ConflictPolicy.Manual,
            WritableFields = CareSyncFields.Invoice,
            Validate = LabBillingSyncRules.ValidateInvoice,
            Authorize = LabBillingSyncRules.AuthorizeInvoiceAsync,
        },
        new()
        {
            Name = SyncEntityNames.InvoiceLine,
            ClrType = typeof(InvoiceLine),
            Order = 361,
            ViewPermission = Permissions.Billing.View,
            WritePermission = Permissions.Billing.Collect,
            Policy = ConflictPolicy.Manual,
            WritableFields = CareSyncFields.InvoiceLine,
            Validate = LabBillingSyncRules.ValidateLine,
            Authorize = LabBillingSyncRules.AuthorizeLineAsync,
        },
        new()
        {
            Name = SyncEntityNames.Payment,
            ClrType = typeof(Payment),
            Order = 362,
            ViewPermission = Permissions.Billing.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            WritableFields = CareSyncFields.Payment,
            Validate = LabBillingSyncRules.ValidatePayment,
            Authorize = LabBillingSyncRules.AuthorizePaymentAsync,
        },
        new()
        {
            Name = SyncEntityNames.CashClosing,
            ClrType = typeof(CashClosing),
            Order = 363,
            ViewPermission = Permissions.Billing.View,
            WritePermission = Permissions.Billing.CashClose,
            WritableFields = CareSyncFields.CashClosing,
            Authorize = LabBillingSyncRules.AuthorizeCashClosingAsync,
        },
        // Phase 6b masters (server wins).
        new() { Name = SyncEntityNames.PackagePlan, ClrType = typeof(PackagePlan), Order = 47 },
        new() { Name = SyncEntityNames.Payer, ClrType = typeof(Payer), Order = 48 },
        new() { Name = SyncEntityNames.MessageTemplate, ClrType = typeof(MessageTemplate), Order = 49 },

        // Patient-level settings follow the patient; packages precede the invoice lines that sell them.
        new()
        {
            Name = SyncEntityNames.PatientPanel,
            ClrType = typeof(PatientPanel),
            Order = 205,
            ViewPermission = Permissions.Billing.View,
            WritePermission = Permissions.Billing.Collect,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.PatientPanel,
            Authorize = AccountsSyncRules.AuthorizePanelAsync,
        },
        new()
        {
            Name = SyncEntityNames.ContactPreference,
            ClrType = typeof(ContactPreference),
            Order = 206,
            ViewPermission = Permissions.Patients.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.ContactPreference,
            UpsertOnInsert = true, // one row per patient, id derived from the patient
            Authorize = AccountsSyncRules.AuthorizePreferenceAsync,
        },
        new()
        {
            Name = SyncEntityNames.PatientPackage,
            ClrType = typeof(PatientPackage),
            Order = 355,
            ViewPermission = Permissions.Billing.View,
            WritePermission = Permissions.Billing.Collect,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.PatientPackage,
            Authorize = AccountsSyncRules.AuthorizePatientPackageAsync,
        },
        new()
        {
            Name = SyncEntityNames.PackageUsage,
            ClrType = typeof(PackageUsage),
            Order = 365,
            ViewPermission = Permissions.Billing.View,
            WritePermission = Permissions.Billing.Collect,
            WritableFields = CareSyncFields.PackageUsage,
            Authorize = AccountsSyncRules.AuthorizeUsageAsync,
        },
        new()
        {
            Name = SyncEntityNames.CreditNote,
            ClrType = typeof(CreditNote),
            Order = 366,
            ViewPermission = Permissions.Billing.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            WritableFields = CareSyncFields.CreditNote,
            Validate = AccountsSyncRules.ValidateCredit,
            Authorize = AccountsSyncRules.AuthorizeCreditAsync,
        },
        new()
        {
            Name = SyncEntityNames.Expense,
            ClrType = typeof(Expense),
            Order = 367,
            ViewPermission = Permissions.Expenses.Manage,
            WritePermission = Permissions.Expenses.Manage,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.Expense,
            Validate = AccountsSyncRules.ValidateExpense,
            Authorize = AccountsSyncRules.AuthorizeExpenseAsync,
        },
        new()
        {
            Name = SyncEntityNames.OutboundMessage,
            ClrType = typeof(OutboundMessage),
            Order = 370,
            ViewPermission = Permissions.Messages.Send,
            WritePermission = Permissions.Messages.Send,
            WritableFields = CareSyncFields.OutboundMessage,
            Validate = AccountsSyncRules.ValidateMessage,
            Authorize = AccountsSyncRules.AuthorizeMessageAsync,
        },
        new()
        {
            Name = SyncEntityNames.Feedback,
            ClrType = typeof(Feedback),
            Order = 371,
            ViewPermission = Permissions.Feedback.Record,
            WritePermission = Permissions.Feedback.Record,
            WritableFields = CareSyncFields.Feedback,
            HiddenFields = new HashSet<string> { "dynamicData" },
            Validate = AccountsSyncRules.ValidateFeedback,
            Authorize = AccountsSyncRules.AuthorizeFeedbackAsync,
        },
        new()
        {
            Name = SyncEntityNames.Complaint,
            ClrType = typeof(Complaint),
            Order = 372,
            ViewPermission = Permissions.Feedback.Record,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.Complaint,
            Validate = AccountsSyncRules.ValidateComplaint,
            Authorize = AccountsSyncRules.AuthorizeComplaintAsync,
        },
        // Phase 7 inventory: the item master (server wins), then batches, dispensations and the movement ledger.
        new() { Name = SyncEntityNames.InventoryItem, ClrType = typeof(InventoryItem), Order = 50 },
        new()
        {
            Name = SyncEntityNames.StockBatch,
            ClrType = typeof(StockBatch),
            Order = 379,
            ViewPermission = Permissions.Inventory.View,
            WritePermission = Permissions.Inventory.StockIn,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = CareSyncFields.StockBatch,
            Authorize = InventorySyncRules.AuthorizeBatchAsync,
        },
        new()
        {
            Name = SyncEntityNames.Dispensation,
            ClrType = typeof(Dispensation),
            Order = 380,
            ViewPermission = Permissions.Billing.View,
            WritePermission = Permissions.Prescriptions.Dispense,
            WritableFields = CareSyncFields.Dispensation,
            Validate = InventorySyncRules.ValidateDispensation,
            Authorize = InventorySyncRules.AuthorizeDispensationAsync,
        },
        new()
        {
            Name = SyncEntityNames.StockMovement,
            ClrType = typeof(StockMovement),
            Order = 381,
            ViewPermission = Permissions.Inventory.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            WritableFields = CareSyncFields.StockMovement,
            Validate = InventorySyncRules.ValidateMovement,
            Authorize = InventorySyncRules.AuthorizeMovementAsync,
        },
        // Phase 8: read-access log (FR-SEC-07), written on devices, read by auditors only.
        new()
        {
            Name = SyncEntityNames.AccessEvent,
            ClrType = typeof(AccessEvent),
            Order = 900,
            ViewPermission = Permissions.Audit.View,
            WritePermission = Permissions.Patients.View,
            WritableFields = CareSyncFields.AccessEvent,
            Authorize = AuditService.AuthorizeAccessAsync,
            AfterApplyAsync = AuditService.AccessAfterApplyAsync,
        },
        new()
        {
            Name = SyncEntityNames.Attachment,
            ClrType = typeof(Attachment),
            Order = 400,
            ViewPermission = Permissions.Patients.View,
            WritePermission = SyncEntityDescriptor.CustomPermission,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = new HashSet<string> { "recordId", "entityType", "fileName", "contentType", "sizeBytes", "sha256", "caption", "category" },
            Validate = ClinicalSyncRules.ValidateAttachment,
            Authorize = ClinicalSyncRules.AuthorizeAttachmentAsync,
        },

        new()
        {
            Name = SyncEntityNames.BranchNote,
            ClrType = typeof(BranchNote),
            Order = 100,
            ViewPermission = Permissions.Notes.View,
            WritePermission = Permissions.Notes.Manage,
            Policy = ConflictPolicy.LastWriterWins,
            WritableFields = new HashSet<string> { "branchId", "title", "body", "isPinned", "color", "formVersionId" },
            HiddenFields = new HashSet<string> { "dynamicData" },
            Validate = o =>
            {
                var n = (BranchNote)o;
                if (string.IsNullOrWhiteSpace(n.Title)) return "Title is required.";
                if (n.Title.Length > 200) return "Title must be 200 characters or fewer.";
                if (n.Body?.Length > 4000) return "Note must be 4000 characters or fewer.";
                if (n.Color?.Length > 20) return "Colour must be 20 characters or fewer.";
                return null;
            },
            Authorize = async ctx =>
            {
                var note = (BranchNote)ctx.Entity;
                return await AuthorizeBranchScopedAsync(ctx, note.BranchId, Permissions.Notes.Manage)
                       ?? await FieldValueRules.CheckFormVersionAsync(ctx.Db, note.FormVersionId, SyncEntityNames.BranchNote);
            },
        },

        // Dynamic values follow their parent record (pushed after it, same branch scope).
        new()
        {
            Name = SyncEntityNames.FieldValue,
            ClrType = typeof(FieldValue),
            Order = 1000,
            WritePermission = FieldValueRules.WritePermission,
            Policy = ConflictPolicy.LastWriterWins,
            UpsertOnInsert = true,
            WritableFields = new HashSet<string>
            {
                "recordId", "entityType", "fieldKey", "formVersionId",
                "valueKind", "valueText", "valueNumber", "valueDate", "valueBool", "valueJson", "rowIndex",
            },
            PolicyFor = EncounterSyncRules.FieldValuePolicy,
            Authorize = FieldValueRules.AuthorizeAsync,
            AfterApplyAsync = FieldValueRules.AfterApplyAsync,
            FilterPullAsync = FieldValueRules.FilterPullAsync,
        },
    ];

    private static readonly Dictionary<string, SyncEntityDescriptor> ByName =
        All.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<Type, SyncEntityDescriptor> ByType = All.ToDictionary(d => d.ClrType);

    public static SyncEntityDescriptor? Find(string name) => ByName.GetValueOrDefault(name);

    public static SyncEntityDescriptor ForType(Type type) =>
        ByType.TryGetValue(type, out var d) ? d
            : throw new InvalidOperationException($"{type.Name} implements ISyncEntity but is not registered in SyncRegistry.");

    /// <summary>FR-AUTH-02 for branch-owned rows: the branch must exist in the tenant and be within the caller's scope.</summary>
    public static async Task<string?> AuthorizeBranchScopedAsync(SyncWriteContext ctx, Guid branchId, string permission)
    {
        if (!await ctx.Db.Branches.AnyAsync(b => b.Id == branchId))
            return "Branch not found.";
        if (ctx.User.GetScope(permission) != PermissionScope.All && !ctx.User.BranchIds.Contains(branchId))
            return "You can only change records in your own branches.";
        if (ctx.User.GetScope(permission) == PermissionScope.OwnRecords && !ctx.IsNew &&
            ctx.Entity is Domain.Common.Entity e && e.CreatedBy != ctx.User.UserId)
            return "You can only change records you created.";
        return null;
    }
}
