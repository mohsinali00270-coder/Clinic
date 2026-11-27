using Clinic.Application.Abstractions;
using Clinic.Application.Forms;
using Clinic.Application.Sync;
using Clinic.Domain.Clinical;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Security;
using Clinic.Shared.Sync;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Application.Clinical;

/// <summary>Server-side checks for patients, appointments, status history and attachments (FR-SYN-13).</summary>
public static class ClinicalSyncRules
{
    // ---------------------------------------------------------------- patients

    public static string? ValidatePatient(object o)
    {
        var p = (Patient)o;
        if (string.IsNullOrWhiteSpace(p.FullName)) return "Patient name is required.";
        if (p.FullName.Length > 200) return "Name must be 200 characters or fewer.";
        if (!PatientRules.Genders.Contains(p.Gender)) return "Gender must be M, F or O.";
        if (string.IsNullOrWhiteSpace(p.Mrn) || p.Mrn.Length > 40) return "MRN is required (max 40 characters).";
        if (p.DateOfBirth is { } dob && (dob.Date > DateTime.Today || dob.Date < DateTime.Today.AddYears(-130)))
            return "Date of birth is not plausible.";
        if (p.Phone is { Length: > 0 } phone && PatientRules.NormalizePhone(phone).Length is < 7 or > 15)
            return "Phone number is not valid.";
        return null;
    }

    public static async Task<string?> AuthorizePatientAsync(SyncWriteContext ctx)
    {
        var p = (Patient)ctx.Entity;
        if (p.MergedIntoId is { } survivor)
            return $"This patient was merged into another record ({survivor}). Sync to get the merged patient and make the change there.";
        var permission = ctx.IsNew ? Permissions.Patients.Create : Permissions.Patients.Edit;
        if (ctx.User.GetScope(permission) is not { } scope) return $"You do not have the {permission} permission.";

        if (!await ctx.Db.Branches.AnyAsync(b => b.Id == p.BranchId)) return "Branch not found.";
        if (scope != PermissionScope.All && !ctx.User.BranchIds.Contains(p.BranchId))
            return "You can only register or edit patients of your own branches.";
        if (scope == PermissionScope.OwnRecords && !ctx.IsNew && !await IsOwnPatientAsync(ctx, p))
            return "You can only edit your own patients.";

        var mrnTaken = await ctx.Db.Patients.AnyAsync(x => x.Mrn == p.Mrn && x.Id != p.Id);
        if (mrnTaken) return $"MRN {p.Mrn} is already used by another patient.";

        return await FieldValueRules.CheckFormVersionAsync(ctx.Db, p.FormVersionId, SyncEntityNames.Patient);
    }

    /// <summary>FR-AUTH-02 "own records" for a doctor: patients they registered or have an appointment with.</summary>
    private static async Task<bool> IsOwnPatientAsync(SyncWriteContext ctx, Patient p)
    {
        if (p.CreatedBy == ctx.User.UserId) return true;
        var myDoctorIds = await ctx.Db.DoctorProfiles.Where(d => d.UserId == ctx.User.UserId).Select(d => d.Id).ToListAsync();
        return myDoctorIds.Count > 0 && await ctx.Db.Appointments.AnyAsync(a => a.PatientId == p.Id && myDoctorIds.Contains(a.DoctorId));
    }

    // ---------------------------------------------------------------- appointments

    public static string? ValidateAppointment(object o)
    {
        var a = (Appointment)o;
        if (!AppointmentStatus.All.Contains(a.Status)) return $"Unknown status '{a.Status}'.";
        if (a.Priority is not (QueuePriority.Normal or QueuePriority.Priority or QueuePriority.Emergency)) return "Unknown priority.";
        if (a.DurationMinutes is < 1 or > 480) return "Duration must be between 1 and 480 minutes.";
        if (a.Date == default) return "Date is required.";
        if (a.StartTime is { } t && (t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))) return "Start time is not valid.";
        if (a.CancelReason?.Length > 200 || a.Notes?.Length > 1000) return "Text is too long.";
        return null;
    }

    public static async Task<string?> AuthorizeAppointmentAsync(SyncWriteContext ctx)
    {
        var a = (Appointment)ctx.Entity;
        var permissions = ctx.IsNew
            ? new[] { Permissions.Appointments.Book }
            : [Permissions.Appointments.Book, Permissions.Appointments.CheckIn, Permissions.Queue.Manage];

        var granted = permissions.Select(p => ctx.User.GetScope(p)).Where(s => s is not null).Select(s => s!.Value).ToList();
        if (granted.Count == 0) return ctx.IsNew ? "You do not have the Appointments.Book permission." : "You may not change appointments.";
        var scope = granted.Max();

        if (scope != PermissionScope.All && !ctx.User.BranchIds.Contains(a.BranchId))
            return "You can only manage appointments of your own branches.";
        if (!await ctx.Db.Branches.AnyAsync(b => b.Id == a.BranchId)) return "Branch not found.";
        if (!await ctx.Db.Patients.AnyAsync(p => p.Id == a.PatientId)) return "Patient not found on the server.";
        var doctor = await ctx.Db.DoctorProfiles.FirstOrDefaultAsync(d => d.Id == a.DoctorId);
        if (doctor is null) return "Doctor not found.";
        if (!await ctx.Db.AppointmentTypes.AnyAsync(t => t.Id == a.TypeId)) return "Appointment type not found.";
        if (scope == PermissionScope.OwnRecords && doctor.UserId != ctx.User.UserId)
            return "You can only manage your own appointments.";

        return await FieldValueRules.CheckFormVersionAsync(ctx.Db, a.FormVersionId, SyncEntityNames.Appointment);
    }

    /// <summary>
    /// FR-APT-04: if two devices booked the same full slot offline, both are kept and the later one is marked
    /// SlotConflict for reception to resolve. Duplicate queue tokens for a doctor's day get the next free number.
    /// </summary>
    public static async Task AppointmentAfterApplyAsync(SyncWriteContext ctx)
    {
        var a = (Appointment)ctx.Entity;
        var db = ctx.Db;
        var changed = false;
        var date = a.Date.Date;

        var conflict = false;
        if (a.StartTime is { } start && AppointmentStatus.OccupiesSlot.Contains(a.Status))
        {
            var dow = (int)date.DayOfWeek;
            var sessions = await db.DoctorSchedules
                .Where(s => s.DoctorId == a.DoctorId && s.BranchId == a.BranchId && s.DayOfWeek == dow).ToListAsync();
            var capacity = sessions.FirstOrDefault(s => s.StartTime <= start && s.EndTime > start)?.Capacity ?? 1;

            var occupying = AppointmentStatus.OccupiesSlot.ToArray();
            var sameDay = await db.Appointments.Where(x =>
                x.Id != a.Id && x.DoctorId == a.DoctorId && x.Date == date && occupying.Contains(x.Status) && !x.SlotConflict).ToListAsync();
            var earlier = sameDay.Count(x => x.StartTime == start && x.CreatedAt <= a.CreatedAt);
            conflict = earlier >= capacity;
        }
        if (a.SlotConflict != conflict)
        {
            a.SlotConflict = conflict;
            changed = true;
        }

        if (a.TokenNo is { } token)
        {
            var taken = await db.Appointments.Where(x =>
                    x.Id != a.Id && x.DoctorId == a.DoctorId && x.Date == date && x.TokenNo != null)
                .Select(x => x.TokenNo!.Value).ToListAsync();
            if (taken.Contains(token))
            {
                a.TokenNo = taken.Max() + 1;
                changed = true;
            }
        }

        if (changed) await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- status history

    public static string? ValidateHistory(object o)
    {
        var h = (AppointmentStatusHistory)o;
        if (!AppointmentStatus.All.Contains(h.ToStatus)) return $"Unknown status '{h.ToStatus}'.";
        if (h.FromStatus is { } f && !AppointmentStatus.All.Contains(f)) return $"Unknown status '{f}'.";
        return null;
    }

    public static async Task<string?> AuthorizeHistoryAsync(SyncWriteContext ctx)
    {
        var h = (AppointmentStatusHistory)ctx.Entity;
        if (!ctx.IsNew) return "Status history cannot be edited.";
        var appointment = await ctx.Db.Appointments.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == h.AppointmentId && a.TenantId == ctx.Db.CurrentTenantId);
        if (appointment is null) return "Appointment not found on the server.";
        h.BranchId = appointment.BranchId;
        return await AuthorizeAppointmentAsync(new SyncWriteContext(appointment, false, ctx.User, ctx.Db));
    }

    // ---------------------------------------------------------------- attachments

    public const long MaxAttachmentBytes = 25 * 1024 * 1024;

    public static readonly IReadOnlySet<string> AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/heic", "application/pdf",
    };

    public static string? ValidateAttachment(object o)
    {
        var a = (Attachment)o;
        if (string.IsNullOrWhiteSpace(a.FileName) || a.FileName.Length > 200) return "File name is required (max 200 characters).";
        if (!AllowedContentTypes.Contains(a.ContentType)) return $"Files of type {a.ContentType} are not allowed (photos and PDFs only).";
        if (a.SizeBytes is <= 0 or > MaxAttachmentBytes) return "Files must be smaller than 25 MB.";
        if (a.Sha256.Length != 64) return "A SHA-256 hash is required.";
        return null;
    }

    public static async Task<string?> AuthorizeAttachmentAsync(SyncWriteContext ctx)
    {
        var a = (Attachment)ctx.Entity;
        var parent = await DynamicParents.FindAsync(ctx.Db, a.EntityType, a.RecordId);
        if (parent is null) return "The record this file belongs to was not found on the server.";
        a.BranchId = parent.GetSyncBranchId();

        var scope = ctx.User.GetScope(Permissions.Attachments.Upload);
        if (scope is null) return "You do not have the Attachments.Upload permission.";
        if (scope != PermissionScope.All && a.BranchId is { } b && !ctx.User.BranchIds.Contains(b))
            return "You can only attach files to records of your own branches.";
        return null;
    }
}
