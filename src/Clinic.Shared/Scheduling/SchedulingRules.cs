namespace Clinic.Shared.Scheduling;

/// <summary>FR-APT-19 status lifecycle (+ FR-APT-03 queue states). Stored as these lower_snake strings.</summary>
public static class AppointmentStatus
{
    public const string Requested = "requested";
    public const string Booked = "booked";
    public const string Confirmed = "confirmed";
    public const string CheckedIn = "checked_in";     // queue: Waiting
    public const string Vitals = "vitals";            // queue: with the nurse
    public const string WithDoctor = "with_doctor";   // queue: In consultation
    public const string OnHold = "on_hold";           // queue: skipped for now
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string NoShow = "no_show";
    public const string Rescheduled = "rescheduled";
    public const string LeftWithoutBeingSeen = "left";

    public static readonly IReadOnlyList<string> All =
        [Requested, Booked, Confirmed, CheckedIn, Vitals, WithDoctor, OnHold, Completed, Cancelled, NoShow, Rescheduled, LeftWithoutBeingSeen];

    /// <summary>Statuses that hold a slot.</summary>
    public static readonly IReadOnlySet<string> OccupiesSlot =
        new HashSet<string> { Requested, Booked, Confirmed, CheckedIn, Vitals, WithDoctor, OnHold, Completed };

    /// <summary>Statuses shown on the live queue board.</summary>
    public static readonly IReadOnlySet<string> InQueue = new HashSet<string> { CheckedIn, Vitals, WithDoctor, OnHold };

    public static readonly IReadOnlySet<string> Final = new HashSet<string> { Completed, Cancelled, NoShow, Rescheduled, LeftWithoutBeingSeen };

    private static readonly Dictionary<string, string[]> Next = new()
    {
        [Requested] = [Booked, Confirmed, Cancelled],
        [Booked] = [Confirmed, CheckedIn, Cancelled, NoShow, Rescheduled],
        [Confirmed] = [CheckedIn, Cancelled, NoShow, Rescheduled],
        [CheckedIn] = [Vitals, WithDoctor, OnHold, LeftWithoutBeingSeen, Cancelled],
        [Vitals] = [CheckedIn, WithDoctor, OnHold, LeftWithoutBeingSeen],
        [WithDoctor] = [Completed, Vitals, OnHold, CheckedIn],
        [OnHold] = [CheckedIn, Vitals, WithDoctor, LeftWithoutBeingSeen],
    };

    public static bool CanMove(string from, string to) => Next.TryGetValue(from, out var n) && n.Contains(to);

    public static string Display(string status) => status switch
    {
        CheckedIn => "Waiting",
        Vitals => "Vitals",
        WithDoctor => "With doctor",
        OnHold => "On hold",
        NoShow => "No-show",
        LeftWithoutBeingSeen => "Left without being seen",
        _ => char.ToUpperInvariant(status[0]) + status[1..].Replace('_', ' '),
    };
}

public static class QueuePriority
{
    public const string Normal = "normal";
    public const string Priority = "priority";     // elderly, pregnant… (FR-APT-21)
    public const string Emergency = "emergency";

    public static int Rank(string? p) => p switch { Emergency => 0, Priority => 1, _ => 2 };
}

public static class BookingChannel
{
    public const string FrontDesk = "front_desk";
    public const string Phone = "phone";
    public const string Doctor = "doctor";
    public const string WalkIn = "walk_in";
    public const string Online = "online";
}

// ---------------------------------------------------------------------------
// Slot calculation (FR-APT-01, 08, 09, 10) — identical on device (offline) and server.
// ---------------------------------------------------------------------------

public sealed record ScheduleBlock(Guid DoctorId, Guid BranchId, int DayOfWeek, TimeSpan Start, TimeSpan End,
    int SlotMinutes, int Capacity, int BufferMinutes, DateTime? ValidFrom = null, DateTime? ValidTo = null);

public sealed record DateRange(DateTime Start, DateTime End);

public sealed record BookedSlot(Guid DoctorId, DateTime Date, TimeSpan Start, Guid AppointmentId);

public sealed record Slot(Guid DoctorId, Guid BranchId, DateTime Date, TimeSpan Start, TimeSpan End, int Capacity, int Booked)
{
    public int Free => Math.Max(0, Capacity - Booked);
    public bool IsAvailable => Free > 0;
    public DateTime StartsAt => Date.Date + Start;
}

public sealed record BookingLimits(int MinNoticeMinutes = 0, int MaxAdvanceDays = 60);

public static class SlotCalculator
{
    /// <summary>
    /// The doctor's slots on a date at a branch: schedule blocks minus leave and branch closures, with how many
    /// are already booked. Past slots and slots outside the booking window are dropped.
    /// </summary>
    public static IReadOnlyList<Slot> SlotsFor(
        Guid doctorId, Guid branchId, DateTime date,
        IEnumerable<ScheduleBlock> schedules,
        IEnumerable<DateRange> doctorLeave,
        IEnumerable<DateTime> branchClosures,
        IEnumerable<BookedSlot> booked,
        DateTime now,
        BookingLimits? limits = null)
    {
        limits ??= new BookingLimits();
        var day = date.Date;
        if (day > now.Date.AddDays(limits.MaxAdvanceDays)) return [];
        if (branchClosures.Any(c => c.Date == day)) return [];
        if (doctorLeave.Any(l => day >= l.Start.Date && day <= l.End.Date)) return [];

        var bookedAt = booked.Where(b => b.DoctorId == doctorId && b.Date.Date == day)
            .GroupBy(b => b.Start).ToDictionary(g => g.Key, g => g.Count());

        var earliest = now.AddMinutes(limits.MinNoticeMinutes);
        var slots = new List<Slot>();

        foreach (var block in schedules.Where(s =>
                     s.DoctorId == doctorId && s.BranchId == branchId && s.DayOfWeek == (int)day.DayOfWeek &&
                     (s.ValidFrom is null || s.ValidFrom.Value.Date <= day) && (s.ValidTo is null || s.ValidTo.Value.Date >= day))
                 .OrderBy(s => s.Start))
        {
            var step = TimeSpan.FromMinutes(Math.Max(5, block.SlotMinutes) + Math.Max(0, block.BufferMinutes));
            var length = TimeSpan.FromMinutes(Math.Max(5, block.SlotMinutes));
            for (var t = block.Start; t + length <= block.End; t += step)
            {
                if (day + t < earliest) continue;
                slots.Add(new Slot(doctorId, branchId, day, t, t + length, Math.Max(1, block.Capacity), bookedAt.GetValueOrDefault(t)));
            }
        }
        return slots;
    }

    /// <summary>FR-APT-13: first free slot per doctor from <paramref name="from"/> onwards (searching up to N days).</summary>
    public static IReadOnlyList<Slot> NextAvailable(
        IEnumerable<Guid> doctorIds, Guid branchId, DateTime from, int days,
        Func<Guid, DateTime, IReadOnlyList<Slot>> slotsForDoctorDay)
    {
        var result = new List<Slot>();
        foreach (var doctor in doctorIds)
        {
            for (var d = 0; d < days; d++)
            {
                var free = slotsForDoctorDay(doctor, from.Date.AddDays(d)).FirstOrDefault(s => s.IsAvailable);
                if (free is null) continue;
                result.Add(free);
                break;
            }
        }
        return result.OrderBy(s => s.StartsAt).ToList();
    }
}

// ---------------------------------------------------------------------------
// Patients (FR-PAT-02, 04)
// ---------------------------------------------------------------------------

public static class PatientRules
{
    public static readonly IReadOnlySet<string> Genders = new HashSet<string> { "M", "F", "O" };

    /// <summary>FR-PAT-02: {BranchCode}-{DeviceCode}-{Sequence}, unique without the network.</summary>
    public static string Mrn(string branchCode, string deviceCode, long sequence) => $"{branchCode}-{deviceCode}-{sequence:D5}";

    public static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "";
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        // Pakistani numbers: +92 300… and 0300… are the same.
        if (digits.StartsWith("92") && digits.Length == 12) digits = "0" + digits[2..];
        return digits;
    }

    public static string NormalizeName(string? name) =>
        string.Join(' ', (name ?? "").ToLowerInvariant()
            .Split([' ', '.', '-', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p is not ("mr" or "mrs" or "ms" or "dr" or "miss")));

    /// <summary>
    /// FR-PAT-04: same phone + similar name (and DOB/age not contradicting). Similar = same tokens in any order,
    /// or edit distance ≤ 2 on the whole name (catches Muhammad/Mohammad, Ali/Aly).
    /// </summary>
    public static bool LooksLikeDuplicate(string nameA, string? phoneA, DateTime? dobA, string nameB, string? phoneB, DateTime? dobB)
    {
        var pa = NormalizePhone(phoneA);
        if (pa.Length < 7 || pa != NormalizePhone(phoneB)) return false;
        if (dobA is { } a && dobB is { } b && Math.Abs((a.Date - b.Date).TotalDays) > 366) return false;
        return SimilarNames(nameA, nameB);
    }

    public static bool SimilarNames(string a, string b)
    {
        var na = NormalizeName(a);
        var nb = NormalizeName(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        if (na == nb) return true;
        var ta = na.Split(' ').Order().ToArray();
        var tb = nb.Split(' ').Order().ToArray();
        if (ta.SequenceEqual(tb)) return true;
        return Levenshtein(na, nb) <= Math.Max(2, Math.Min(na.Length, nb.Length) / 6);
    }

    public static int Levenshtein(string s, string t)
    {
        var d = new int[t.Length + 1];
        for (var j = 0; j <= t.Length; j++) d[j] = j;
        for (var i = 1; i <= s.Length; i++)
        {
            var prev = d[0];
            d[0] = i;
            for (var j = 1; j <= t.Length; j++)
            {
                var tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (s[i - 1] == t[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return d[t.Length];
    }

    /// <summary>Context variables a patient record offers to dynamic-form expressions.</summary>
    public static IReadOnlyDictionary<string, object?> FormVariables(string? gender, DateTime? dob, DateTime today)
    {
        double? years = null, months = null;
        if (dob is { } d)
        {
            years = AgeYears(d, today);
            var m = (today.Year - d.Year) * 12 + today.Month - d.Month;
            if (today.Day < d.Day) m--;
            months = Math.Max(0, m);
        }
        return new Dictionary<string, object?> { ["gender"] = gender, ["ageYears"] = years, ["ageMonths"] = months };
    }

    public static int? AgeYears(DateTime? dob, DateTime today)
    {
        if (dob is not { } d) return null;
        var age = today.Year - d.Year;
        if (today.Date < d.Date.AddYears(age)) age--;
        return Math.Max(0, age);
    }
}

public static class DoctorTypes
{
    public const string Permanent = "permanent";
    public const string Visiting = "visiting";
    public const string OnCall = "on_call";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Permanent, Visiting, OnCall };
}
