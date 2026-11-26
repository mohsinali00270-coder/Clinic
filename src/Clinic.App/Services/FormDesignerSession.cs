using Clinic.Shared.Forms;

namespace Clinic.App.Services;

/// <summary>
/// The form being designed, shared by the designer, field editor and preview pages, so they all edit the same
/// in-memory definition until the designer saves it.
/// </summary>
public sealed class FormDesignerSession
{
    public FormDefinition? Definition { get; set; }
    public Guid? FormId { get; set; }

    /// <summary>Role names offered for field permissions.</summary>
    public IReadOnlyList<string> RoleNames { get; set; } = [];

    /// <summary>Lookup list keys offered for lookup fields.</summary>
    public IReadOnlyList<string> LookupKeys { get; set; } = [];

    /// <summary>One open field editor: the field, the list it lives in, and whether it is new.</summary>
    public sealed record EditTarget(FieldDefinition Field, List<FieldDefinition> Container, bool IsNew, FieldDefinition? ParentGroup);

    private readonly Stack<EditTarget> _targets = new();

    public void PushTarget(EditTarget target) => _targets.Push(target);
    public EditTarget? CurrentTarget => _targets.Count > 0 ? _targets.Peek() : null;
    public void PopTarget() { if (_targets.Count > 0) _targets.Pop(); }

    /// <summary>Raised when a field editor applies changes, so the designer refreshes.</summary>
    public event EventHandler? Changed;
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
