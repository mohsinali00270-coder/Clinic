using Clinic.Client.Core.Local;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Sync;

/// <summary>Device-side mirror of the server's SyncRegistry: which local table holds which entity type.</summary>
public sealed record LocalSyncEntry(string Name, Type LocalType, int Order, IReadOnlySet<string> WritableFields)
{
    public bool ClientWritable => WritableFields.Count > 0;
}

public static class LocalSyncRegistry
{
    /// <summary>FR-SYN-22: applied in this order within each pulled page.</summary>
    public static IReadOnlyList<LocalSyncEntry> All { get; } =
    [
        new(SyncEntityNames.Branch, typeof(LocalBranch), 0, new HashSet<string>()),
        new(SyncEntityNames.LookupList, typeof(LocalLookupList), 10, new HashSet<string>()),
        new(SyncEntityNames.LookupItem, typeof(LocalLookupItem), 11, new HashSet<string>()),
        new(SyncEntityNames.FormVersion, typeof(LocalFormVersion), 20, new HashSet<string>()),
        new(SyncEntityNames.DoctorProfile, typeof(LocalDoctor), 30, new HashSet<string>()),
        new(SyncEntityNames.DoctorSchedule, typeof(LocalDoctorSchedule), 31, new HashSet<string>()),
        new(SyncEntityNames.DoctorLeave, typeof(LocalDoctorLeave), 32, new HashSet<string>()),
        new(SyncEntityNames.BranchClosure, typeof(LocalBranchClosure), 33, new HashSet<string>()),
        new(SyncEntityNames.AppointmentType, typeof(LocalAppointmentType), 34, new HashSet<string>()),
        new(SyncEntityNames.Patient, typeof(LocalPatient), 200,
            new HashSet<string> { "mrn", "fullName", "gender", "dateOfBirth", "dobEstimated", "phone", "branchId", "formVersionId" }),
        new(SyncEntityNames.Appointment, typeof(LocalAppointment), 300,
            new HashSet<string>
            {
                "patientId", "doctorId", "branchId", "typeId", "date", "startTime", "durationMinutes", "status", "channel",
                "priority", "tokenNo", "checkedInAt", "calledAt", "completedAt", "cancelReason", "cancelledBy",
                "rescheduleCount", "rescheduledFromId", "notes", "formVersionId",
            }),
        new(SyncEntityNames.AppointmentStatusHistory, typeof(LocalAppointmentStatusHistory), 310,
            new HashSet<string> { "appointmentId", "fromStatus", "toStatus", "changedAt", "changedBy", "reason" }),
        new(SyncEntityNames.Icd10Code, typeof(LocalIcd10), 40, new HashSet<string>()),
        new(SyncEntityNames.Drug, typeof(LocalDrug), 41, new HashSet<string>()),
        new(SyncEntityNames.Encounter, typeof(LocalEncounter), 320,
            new HashSet<string>
            {
                "patientId", "doctorId", "branchId", "appointmentId", "specialty", "startedAt", "status", "signedAt", "signedBy",
                "followUpDate", "formVersionId", "patientGender", "patientDateOfBirth", "caseId",
            }),
        new(SyncEntityNames.Vitals, typeof(LocalVitals), 321,
            new HashSet<string> { "patientId", "encounterId", "branchId", "recordedAt", "recordedBy", "patientGender", "patientDateOfBirth", "formVersionId" }),
        new(SyncEntityNames.EncounterAmendment, typeof(LocalAmendment), 322,
            new HashSet<string> { "encounterId", "fieldKey", "oldValue", "newValue", "reason", "amendedAt", "amendedBy" }),
        new(SyncEntityNames.Diagnosis, typeof(LocalDiagnosis), 323,
            new HashSet<string> { "encounterId", "code", "description", "isPrimary", "notes" }),
        new(SyncEntityNames.Prescription, typeof(LocalPrescription), 324,
            new HashSet<string> { "encounterId", "notes", "issuedAt" }),
        new(SyncEntityNames.PrescriptionItem, typeof(LocalPrescriptionItem), 325,
            new HashSet<string> { "prescriptionId", "drugId", "drugName", "strength", "form", "dose", "frequency", "durationDays", "route", "instructions", "sortOrder" }),
        new(SyncEntityNames.PrescriptionTemplate, typeof(LocalPrescriptionTemplate), 326,
            new HashSet<string> { "doctorId", "name", "itemsJson" }),
        // Phase 5b: masters (server-managed), cases, day care, procedures, referrals.
        new(SyncEntityNames.CaseType, typeof(LocalCaseType), 42, new HashSet<string>()),
        new(SyncEntityNames.ProcedureType, typeof(LocalProcedureType), 43, new HashSet<string>()),
        new(SyncEntityNames.Bed, typeof(LocalBed), 44, new HashSet<string>()),
        new(SyncEntityNames.Case, typeof(LocalCase), 315, CareSyncFields.Case),
        new(SyncEntityNames.CarePlanStep, typeof(LocalCarePlanStep), 316, CareSyncFields.CarePlanStep),
        new(SyncEntityNames.CaseMember, typeof(LocalCaseMember), 317, CareSyncFields.CaseMember),
        new(SyncEntityNames.CaseNote, typeof(LocalCaseNote), 318, CareSyncFields.CaseNote),
        new(SyncEntityNames.Admission, typeof(LocalAdmission), 330, CareSyncFields.Admission),
        new(SyncEntityNames.BedTransfer, typeof(LocalBedTransfer), 331, CareSyncFields.BedTransfer),
        new(SyncEntityNames.NursingNote, typeof(LocalNursingNote), 332, CareSyncFields.NursingNote),
        new(SyncEntityNames.MedicationOrder, typeof(LocalMedicationOrder), 333, CareSyncFields.MedicationOrder),
        new(SyncEntityNames.MedicationDose, typeof(LocalMedicationDose), 334, CareSyncFields.MedicationDose),
        new(SyncEntityNames.Procedure, typeof(LocalProcedure), 335, CareSyncFields.Procedure),
        new(SyncEntityNames.Referral, typeof(LocalReferral), 340, CareSyncFields.Referral),
        // Phase 6: labs and billing.
        new(SyncEntityNames.LabTest, typeof(LocalLabTest), 45, new HashSet<string>()),
        new(SyncEntityNames.ServiceItem, typeof(LocalServiceItem), 46, new HashSet<string>()),
        new(SyncEntityNames.LabOrder, typeof(LocalLabOrder), 350, CareSyncFields.LabOrder),
        new(SyncEntityNames.LabResult, typeof(LocalLabResult), 351, CareSyncFields.LabResult),
        new(SyncEntityNames.Invoice, typeof(LocalInvoice), 360, CareSyncFields.Invoice),
        new(SyncEntityNames.InvoiceLine, typeof(LocalInvoiceLine), 361, CareSyncFields.InvoiceLine),
        new(SyncEntityNames.Payment, typeof(LocalPayment), 362, CareSyncFields.Payment),
        new(SyncEntityNames.CashClosing, typeof(LocalCashClosing), 363, CareSyncFields.CashClosing),
        // Phase 6b: packages, panels, credit notes, expenses, messages, feedback.
        new(SyncEntityNames.PackagePlan, typeof(LocalPackagePlan), 47, new HashSet<string>()),
        new(SyncEntityNames.Payer, typeof(LocalPayer), 48, new HashSet<string>()),
        new(SyncEntityNames.MessageTemplate, typeof(LocalMessageTemplate), 49, new HashSet<string>()),
        new(SyncEntityNames.PatientPanel, typeof(LocalPatientPanel), 205, CareSyncFields.PatientPanel),
        new(SyncEntityNames.ContactPreference, typeof(LocalContactPreference), 206, CareSyncFields.ContactPreference),
        new(SyncEntityNames.PatientPackage, typeof(LocalPatientPackage), 355, CareSyncFields.PatientPackage),
        new(SyncEntityNames.PackageUsage, typeof(LocalPackageUsage), 365, CareSyncFields.PackageUsage),
        new(SyncEntityNames.CreditNote, typeof(LocalCreditNote), 366, CareSyncFields.CreditNote),
        new(SyncEntityNames.Expense, typeof(LocalExpense), 367, CareSyncFields.Expense),
        new(SyncEntityNames.OutboundMessage, typeof(LocalOutboundMessage), 370, CareSyncFields.OutboundMessage),
        new(SyncEntityNames.Feedback, typeof(LocalFeedback), 371, CareSyncFields.Feedback),
        new(SyncEntityNames.Complaint, typeof(LocalComplaint), 372, CareSyncFields.Complaint),
        // Phase 7: inventory.
        new(SyncEntityNames.InventoryItem, typeof(LocalInventoryItem), 50, new HashSet<string>()),
        new(SyncEntityNames.StockBatch, typeof(LocalStockBatch), 379, CareSyncFields.StockBatch),
        new(SyncEntityNames.Dispensation, typeof(LocalDispensation), 380, CareSyncFields.Dispensation),
        new(SyncEntityNames.StockMovement, typeof(LocalStockMovement), 381, CareSyncFields.StockMovement),
        new(SyncEntityNames.AccessEvent, typeof(LocalAccessEvent), 900, CareSyncFields.AccessEvent),
        new(SyncEntityNames.Attachment, typeof(LocalAttachment), 400,
            new HashSet<string> { "recordId", "entityType", "fileName", "contentType", "sizeBytes", "sha256", "caption", "category" }),
        new(SyncEntityNames.BranchNote, typeof(LocalBranchNote), 100,
            new HashSet<string> { "branchId", "title", "body", "isPinned", "color", "formVersionId" }),
        new(SyncEntityNames.FieldValue, typeof(LocalFieldValue), 1000,
            new HashSet<string>
            {
                "recordId", "entityType", "fieldKey", "formVersionId",
                "valueKind", "valueText", "valueNumber", "valueDate", "valueBool", "valueJson", "rowIndex",
            }),
    ];

    private static readonly Dictionary<string, LocalSyncEntry> ByName = All.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Type, LocalSyncEntry> ByType = All.ToDictionary(e => e.LocalType);

    public static LocalSyncEntry? Find(string name) => ByName.GetValueOrDefault(name);

    public static LocalSyncEntry For(Type type) =>
        ByType.TryGetValue(type, out var e) ? e : throw new InvalidOperationException($"{type.Name} is not a synced local type.");
}
