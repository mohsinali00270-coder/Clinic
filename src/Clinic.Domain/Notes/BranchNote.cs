using Clinic.Domain.Forms;
using Clinic.Domain.Sync;

namespace Clinic.Domain.Notes;

/// <summary>
/// Front-desk handover note on a branch notice board. Small, but a real offline-first record:
/// created and edited on devices, merged field by field on the server. Since Phase 3 it also carries
/// an admin-designed "details" form (dynamic fields).
/// </summary>
public class BranchNote : SyncEntity, IHasDynamicData
{
    public Guid BranchId { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public bool IsPinned { get; set; }

    /// <summary>Optional colour tag, e.g. "amber".</summary>
    public string? Color { get; set; }

    public Guid? FormVersionId { get; set; }
    public string? DynamicData { get; set; }

    public override Guid? GetSyncBranchId() => BranchId;

    public IReadOnlyDictionary<string, object?> GetFormVariables() => new Dictionary<string, object?>();
}
