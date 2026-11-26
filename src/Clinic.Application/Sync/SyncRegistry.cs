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
                "followUpDate", "formVersionId", "patientGender", "patientDateOfBirth",
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
