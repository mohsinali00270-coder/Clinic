namespace Clinic.Shared.Clinical;

/// <summary>FR-CASE-01: Open → On hold → Closed / Referred.</summary>
public static class CaseStatus
{
    public const string Open = "open";
    public const string OnHold = "on_hold";
    public const string Closed = "closed";
    public const string Referred = "referred";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Open, OnHold, Closed, Referred };

    /// <summary>A finished case: its plan, team and notes are read-only until it is reopened.</summary>
    public static bool IsFinished(string status) => status is Closed or Referred;

    public static string Display(string status) => status switch
    {
        OnHold => "On hold",
        _ => status.Length == 0 ? status : char.ToUpperInvariant(status[0]) + status[1..],
    };
}

/// <summary>FR-CASE-05: planned steps of a care plan.</summary>
public static class CarePlanStepKind
{
    public const string Visit = "visit";
    public const string Test = "test";
    public const string Procedure = "procedure";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Visit, Test, Procedure, Other };
}

public static class CarePlanStepStatus
{
    public const string Planned = "planned";
    public const string Done = "done";
    public const string Skipped = "skipped";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Planned, Done, Skipped };

    /// <summary>FR-CASE-05: a planned step past its due date is overdue.</summary>
    public static bool IsOverdue(string status, DateTime? dueDate, DateTime today) =>
        status == Planned && dueDate is { } due && due.Date < today.Date;
}

/// <summary>FR-CASE-06: roles of the case team.</summary>
public static class CaseMemberRole
{
    public const string Primary = "primary";
    public const string Consulting = "consulting";
    public const string Nurse = "nurse";
    public const string Therapist = "therapist";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Primary, Consulting, Nurse, Therapist };
}

/// <summary>A care-plan step a case type creates when a case opens (offset from the open date).</summary>
public sealed record CasePlanTemplateStep(string Kind, string Title, int OffsetDays);

// ---------------------------------------------------------------------------- referrals (§4.11)

public static class ReferralDirection
{
    /// <summary>FR-REF-01: to another doctor or specialty in this clinic.</summary>
    public const string Internal = "internal";

    /// <summary>FR-REF-02: to a hospital, specialist or imaging centre (printed letter).</summary>
    public const string External = "external";

    /// <summary>FR-REF-03: a patient sent to us by another doctor or clinic.</summary>
    public const string Incoming = "incoming";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Internal, External, Incoming };
}

public static class ReferralStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Completed = "completed";
    public const string Declined = "declined";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Pending, Accepted, Completed, Declined, Cancelled };
    public static bool IsOpen(string status) => status is Pending or Accepted;
}

public static class ReferralPriority
{
    public const string Routine = "routine";
    public const string Urgent = "urgent";
    public const string Emergency = "emergency";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Routine, Urgent, Emergency };
}

// ---------------------------------------------------------------------------- procedures and day care (§4.12)

public static class ProcedureStatus
{
    public const string Planned = "planned";
    public const string InProgress = "in_progress";
    public const string Done = "done";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Planned, InProgress, Done, Cancelled };

    /// <summary>FR-PRC-02: moving to these needs a signed consent when the procedure type requires one.</summary>
    public static bool NeedsConsent(string status) => status is InProgress or Done;

    public static string Display(string status) => status switch
    {
        InProgress => "In progress",
        _ => status.Length == 0 ? status : char.ToUpperInvariant(status[0]) + status[1..],
    };
}

public static class ProcedureCategory
{
    public const string MinorSurgery = "minor_surgery";
    public const string Dressing = "dressing";
    public const string Dental = "dental";
    public const string Physiotherapy = "physio";
    public const string Injection = "injection";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { MinorSurgery, Dressing, Dental, Physiotherapy, Injection, Other };
}

/// <summary>A consumable used by a procedure (snapshot on the record; stock comes with inventory in Phase 7).</summary>
public sealed record Consumable(string Name, decimal Quantity, string? Unit = null);

public static class ConsentRelation
{
    public const string Self = "self";
    public const string Parent = "parent";
    public const string Guardian = "guardian";
    public const string Spouse = "spouse";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Self, Parent, Guardian, Spouse };
}

public static class AdmissionStatus
{
    public const string Admitted = "admitted";
    public const string Discharged = "discharged";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Admitted, Discharged, Cancelled };
}

public static class BedKind
{
    public const string Bed = "bed";
    public const string Room = "room";
    public const string Chair = "chair";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Bed, Room, Chair };
}

public static class ChargeUnit
{
    public const string Day = "day";
    public const string Hour = "hour";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Day, Hour };
}

public static class MedicationOrderStatus
{
    public const string Active = "active";
    public const string Stopped = "stopped";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Active, Stopped };
}

public static class MedicationDoseStatus
{
    public const string Given = "given";
    public const string Held = "held";
    public const string Refused = "refused";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Given, Held, Refused };
}

public static class NursingNoteKind
{
    public const string Note = "note";
    public const string Observation = "observation";
    public const string Handover = "handover";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Note, Observation, Handover };
}

/// <summary>One stay in one bed: from admission or a transfer until the next transfer, discharge or now.</summary>
public sealed record BedStay(Guid BedId, DateTime From, DateTime? To);

public sealed record BedRate(decimal Amount, string Unit);

public sealed record RoomChargeLine(Guid BedId, DateTime From, DateTime To, int Units, string Unit, decimal Rate, decimal Amount);

/// <summary>
/// FR-PRC-04: room charges accrue per started day or hour of each bed stay, at least one unit per stay.
/// The same calculation runs on the device (running bill offline) and, with billing in Phase 6, on the server.
/// </summary>
public static class RoomCharges
{
    public static IReadOnlyList<RoomChargeLine> Calculate(IEnumerable<BedStay> stays, Func<Guid, BedRate?> rateOf, DateTime now)
    {
        var lines = new List<RoomChargeLine>();
        foreach (var stay in stays.OrderBy(s => s.From))
        {
            var rate = rateOf(stay.BedId);
            var to = stay.To ?? now;
            if (to < stay.From) to = stay.From;
            if (rate is null) continue;

            var span = to - stay.From;
            var units = rate.Unit == ChargeUnit.Hour
                ? (int)Math.Ceiling(span.TotalHours)
                : (int)Math.Ceiling(span.TotalDays);
            units = Math.Max(1, units);
            lines.Add(new RoomChargeLine(stay.BedId, stay.From, to, units, rate.Unit, rate.Amount, units * rate.Amount));
        }
        return lines;
    }

    /// <summary>Bed stays from the admission bed history: transfers in time order, current bed last.</summary>
    public static IReadOnlyList<BedStay> Stays(DateTime admittedAt, Guid currentBedId, DateTime? endedAt,
        IEnumerable<(Guid FromBedId, Guid ToBedId, DateTime MovedAt)> transfers)
    {
        var ordered = transfers.OrderBy(t => t.MovedAt).ToList();
        var stays = new List<BedStay>();
        var from = admittedAt;
        foreach (var t in ordered)
        {
            stays.Add(new BedStay(t.FromBedId, from, t.MovedAt));
            from = t.MovedAt;
        }
        stays.Add(new BedStay(currentBedId, from, endedAt));
        return stays;
    }
}

/// <summary>
/// FR-REF-04 contact actions: phone numbers as tel:, sms: and WhatsApp links. Pakistani numbers are normalised
/// to international form (03xx… → +923xx…).
/// </summary>
public static class ContactLinks
{
    public static string? International(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;
        if (phone.TrimStart().StartsWith('+')) return "+" + digits;
        if (digits.StartsWith("0092")) return "+" + digits[2..];
        if (digits.StartsWith("92") && digits.Length >= 12) return "+" + digits;
        if (digits.StartsWith('0')) return "+92" + digits[1..];
        return "+" + digits;
    }

    public static string? Tel(string? phone) => International(phone) is { } p ? $"tel:{p}" : null;
    public static string? Sms(string? phone) => International(phone) is { } p ? $"sms:{p}" : null;

    public static string? WhatsApp(string? phone, string? message = null) =>
        International(phone) is { } p
            ? $"https://wa.me/{p.TrimStart('+')}" + (string.IsNullOrWhiteSpace(message) ? "" : "?text=" + Uri.EscapeDataString(message))
            : null;
}

/// <summary>
/// FR-AI-22 / FR-AI-17 (on-device part): the prescription as plain sentences a patient understands, in English
/// or Urdu, for read-aloud or sharing. Built from structured data only — nothing is generated.
/// </summary>
public static class PatientInstructions
{
    public sealed record Line(string Medicine, string? Dose, string? Frequency, int? DurationDays, string? Instructions);

    public static IReadOnlyList<string> Build(IEnumerable<Line> lines, string language, DateTime? followUp = null, IEnumerable<string>? advice = null)
    {
        var ur = language == "ur";
        var result = new List<string>();
        foreach (var l in lines)
        {
            var f = Frequencies.Find(l.Frequency);
            if (ur)
            {
                var parts = new List<string> { $"{l.Medicine}:" };
                if (!string.IsNullOrWhiteSpace(l.Dose)) parts.Add(l.Dose!);
                parts.Add(f?.Urdu ?? l.Frequency ?? "");
                if (l.DurationDays is > 0) parts.Add($"{l.DurationDays} دن تک");
                result.Add(string.Join(" ", parts.Where(p => p.Length > 0)) + "۔");
            }
            else
            {
                var sentence = $"{l.Medicine}: take {(string.IsNullOrWhiteSpace(l.Dose) ? "as directed" : l.Dose)}, {(f?.English ?? l.Frequency ?? "as directed").ToLowerInvariant()}";
                if (l.DurationDays is > 0) sentence += $", for {l.DurationDays} days";
                result.Add(sentence + ".");
            }
            if (!string.IsNullOrWhiteSpace(l.Instructions)) result.Add(l.Instructions!.Trim());
        }
        foreach (var a in advice ?? []) if (!string.IsNullOrWhiteSpace(a)) result.Add(a.Trim());
        if (followUp is { } d)
            result.Add(ur ? $"براہِ کرم {d:dd MMM yyyy} کو دوبارہ تشریف لائیں۔" : $"Please come back on {d:dddd, d MMMM yyyy}.");
        return result;
    }
}
