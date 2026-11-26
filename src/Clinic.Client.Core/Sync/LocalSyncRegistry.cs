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
                "followUpDate", "formVersionId", "patientGender", "patientDateOfBirth",
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
