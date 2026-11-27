using System.Text.Json.Nodes;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Clinical;
using Clinic.Shared.Forms;
using Clinic.Shared.Sync;

namespace Clinic.Client.Core.Clinical;

/// <summary>
/// §4.14 on the device: patient preferences (FR-COM-02) and messages rendered from templates (FR-COM-01), queued
/// offline and sent by the server after sync (FR-COM-03). The status comes back with the next pull.
/// </summary>
public sealed class MessagingService(LocalStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>One preference row per patient, with an id derived from the patient so two devices never create two.</summary>
    public static Guid PreferenceId(Guid patientId) => DeterministicGuid.Create(patientId, "contact-preference");

    public async Task<LocalContactPreference> PreferenceAsync(Guid patientId) =>
        await store.GetAsync<LocalContactPreference>(PreferenceId(patientId))
        ?? new LocalContactPreference { Id = PreferenceId(patientId), PatientId = patientId };

    public async Task<LocalContactPreference> SetPreferenceAsync(Guid patientId, string language, string channel, bool optOut)
    {
        if (language is not ("en" or "ur")) throw new CareException("Language must be English or Urdu.");
        if (!MessageChannel.All.Contains(channel)) throw new CareException($"Unknown channel '{channel}'.");
        var existing = await store.GetAsync<LocalContactPreference>(PreferenceId(patientId));
        var p = existing ?? new LocalContactPreference { Id = PreferenceId(patientId), PatientId = patientId };
        p.Language = language;
        p.Channel = channel;
        p.OptOut = optOut;
        if (existing is null) await store.InsertAsync(p); else await store.UpdateAsync(p);
        return p;
    }

    public async Task<List<LocalMessageTemplate>> TemplatesAsync() =>
        (await store.ListAsync<LocalMessageTemplate>(t => t.IsActive)).OrderBy(t => t.Name).ToList();

    /// <summary>
    /// Renders the template in the patient's language for their channel and queues it. Returns null (nothing queued)
    /// when the patient opted out or has no phone number.
    /// </summary>
    public async Task<LocalOutboundMessage?> QueueAsync(Guid patientId, Guid branchId, string templateKey, IReadOnlyDictionary<string, string?> values,
        Guid? relatedId = null, string? channel = null)
    {
        var patient = await store.GetAsync<LocalPatient>(patientId) ?? throw new CareException("Patient not found.");
        var pref = await PreferenceAsync(patientId);
        if (pref.OptOut) return null;
        var ch = channel ?? pref.Channel;
        var templates = await store.ListAsync<LocalMessageTemplate>(t => t.Key == templateKey && t.IsActive);
        var template = templates.FirstOrDefault(t => t.Channel == ch) ?? templates.FirstOrDefault()
                       ?? throw new CareException($"No '{templateKey}' message template is set up.");
        var to = ContactLinks.International(patient.Phone);
        if (to is null) return null;

        var all = new Dictionary<string, string?>(values) { ["patient"] = patient.FullName };
        var body = TemplateText.Render(pref.Language == "ur" && !string.IsNullOrWhiteSpace(template.BodyUr) ? template.BodyUr! : template.BodyEn, all);
        return await store.InsertAsync(new LocalOutboundMessage
        {
            Id = Guid.NewGuid(), PatientId = patientId, BranchId = branchId, Channel = template.Channel, TemplateKey = templateKey, To = to,
            Body = body, Language = pref.Language, QueuedAt = _clock.GetUtcNow().UtcDateTime, QueuedBy = store.Context.UserId, RelatedId = relatedId,
            Status = MessageStatus.Queued,
        });
    }

    public async Task<List<LocalOutboundMessage>> ForPatientAsync(Guid patientId) =>
        (await store.ListAsync<LocalOutboundMessage>(m => m.PatientId == patientId)).OrderByDescending(m => m.QueuedAt).ToList();
}

/// <summary>FR-BIL-17 expenses on the device (offline; they sync per branch).</summary>
public sealed class ExpenseService(LocalStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<LocalExpense> AddAsync(Guid branchId, string category, decimal amount, DateTime spentOn, string method = PaymentMethods.Cash,
        string? paidTo = null, string? notes = null)
    {
        if (amount <= 0) throw new BillingException("Enter an amount.");
        if (string.IsNullOrWhiteSpace(category)) throw new BillingException("Choose a category.");
        return await store.InsertAsync(new LocalExpense
        {
            Id = Guid.NewGuid(), BranchId = branchId, Category = category, Amount = LineAmounts.Round(amount), SpentOn = spentOn.Date, Method = method,
            PaidTo = paidTo, Notes = notes, RecordedBy = store.Context.UserId,
        });
    }

    public async Task<List<LocalExpense>> ListAsync(Guid branchId, DateTime from, DateTime to) =>
        (await store.ListAsync<LocalExpense>(e => e.BranchId == branchId))
        .Where(e => e.SpentOn.Date >= from.Date && e.SpentOn.Date <= to.Date).OrderByDescending(e => e.SpentOn).ToList();

    public DateTime Today => _clock.GetLocalNow().Date;
}

/// <summary>§4.15 on the device: post-visit feedback (FR-FBK-01) and the complaint register (FR-FBK-02).</summary>
public sealed class FeedbackService(LocalStore store, DynamicDataService forms, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public Task<FormVersionInfo?> FormAsync(Guid branchId) => forms.ResolveFormAsync(SyncEntityNames.Feedback, branchId);

    public async Task<LocalFeedback> RecordAsync(Guid patientId, Guid branchId, int overall, int? doctorRating = null, int? serviceRating = null,
        string? comment = null, Guid? encounterId = null, Guid? doctorId = null, JsonObject? values = null, string channel = "in_clinic")
    {
        if (overall is < 1 or > 5 || doctorRating is < 1 or > 5 || serviceRating is < 1 or > 5) throw new CareException("Ratings are 1–5 stars.");
        if (encounterId is { } eid && doctorId is null) doctorId = (await store.GetAsync<LocalEncounter>(eid))?.DoctorId;
        var f = new LocalFeedback
        {
            Id = Guid.NewGuid(), PatientId = patientId, BranchId = branchId, EncounterId = encounterId, DoctorId = doctorId, OverallRating = overall,
            DoctorRating = doctorRating, ServiceRating = serviceRating, Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            SubmittedAt = UtcNow, Channel = channel,
        };
        var version = await FormAsync(branchId);
        if (version is null || values is null) return await store.InsertAsync(f);
        await DynamicDataService.SaveRecordAsync(store, f, isNew: true, version, values, SyncEntityNames.Feedback, branchId);
        return f;
    }

    /// <summary>Average ratings per doctor (FR-FBK-01 "rating per doctor and service").</summary>
    public async Task<List<(Guid DoctorId, double Overall, double? Doctor, int Count)>> RatingsByDoctorAsync(Guid branchId)
    {
        var rows = await store.ListAsync<LocalFeedback>(f => f.BranchId == branchId && f.DoctorId != null);
        return rows.GroupBy(f => f.DoctorId!.Value)
            .Select(g => (g.Key, g.Average(f => f.OverallRating), g.Any(f => f.DoctorRating is not null) ? g.Where(f => f.DoctorRating is not null).Average(f => (double)f.DoctorRating!.Value) : (double?)null, g.Count()))
            .OrderByDescending(x => x.Item2).ToList();
    }

    public async Task<LocalComplaint> RaiseAsync(Guid branchId, string subject, string? details, string? category, Guid? patientId = null)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new CareException("Give the complaint a subject.");
        return await store.InsertAsync(new LocalComplaint
        {
            Id = Guid.NewGuid(), PatientId = patientId, BranchId = branchId, Subject = subject.Trim(), Details = details, Category = category,
            Status = ComplaintStatus.Open, RaisedAt = UtcNow, RaisedBy = store.Context.UserId,
        });
    }

    public Task<LocalComplaint> AssignAsync(Guid id, Guid? userId, string name) => ChangeAsync(id, c =>
    {
        c.AssignedTo = userId;
        c.AssignedName = name;
        if (c.Status == ComplaintStatus.Open) c.Status = ComplaintStatus.InProgress;
    });

    public Task<LocalComplaint> ResolveAsync(Guid id, string resolution) => ChangeAsync(id, c =>
    {
        if (string.IsNullOrWhiteSpace(resolution)) throw new CareException("Record how the complaint was resolved.");
        c.Status = ComplaintStatus.Resolved;
        c.Resolution = resolution.Trim();
        c.ResolvedAt = UtcNow;
        c.ResolvedBy = store.Context.UserId;
    });

    public Task<LocalComplaint> CloseAsync(Guid id) => ChangeAsync(id, c =>
    {
        if (string.IsNullOrWhiteSpace(c.Resolution)) throw new CareException("Resolve the complaint before closing it.");
        c.Status = ComplaintStatus.Closed;
    });

    public async Task<List<LocalComplaint>> ComplaintsAsync(Guid branchId, bool openOnly = true) =>
        (await store.ListAsync<LocalComplaint>(c => c.BranchId == branchId))
        .Where(c => !openOnly || c.Status is ComplaintStatus.Open or ComplaintStatus.InProgress)
        .OrderBy(c => c.Status == ComplaintStatus.Open ? 0 : 1).ThenBy(c => c.RaisedAt).ToList();

    private async Task<LocalComplaint> ChangeAsync(Guid id, Action<LocalComplaint> change)
    {
        var c = await store.GetAsync<LocalComplaint>(id) ?? throw new CareException("Complaint not found.");
        if (c.Status == ComplaintStatus.Closed) throw new CareException("This complaint is closed.");
        change(c);
        await store.UpdateAsync(c);
        return c;
    }
}
