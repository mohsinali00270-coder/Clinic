using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Clinic.App.Services;
using Clinic.Client.Core.Forms;
using Clinic.Shared.Contracts;
using Clinic.Shared.Forms;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clinic.App.ViewModels;

public sealed record FormRow(FormSummaryDto Form, string EntityName)
{
    public string Status => !Form.IsActive ? "Inactive"
        : Form.CurrentVersionNo is null ? "Draft – not published"
        : Form.HasUnpublishedChanges ? $"v{Form.CurrentVersionNo} published · unpublished changes"
        : $"v{Form.CurrentVersionNo} published";
}

/// <summary>FR-DYN-10…13: list of forms, create from template, import.</summary>
public sealed partial class FormsViewModel(AdminApi api, FormDesignerSession designer) : ViewModelBase
{
    private List<EntityTypeDto> _types = [];

    public ObservableCollection<FormRow> Forms { get; } = [];

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        _types = await api.GetEntityTypesAsync();
        var forms = await api.GetFormsAsync();
        Forms.Clear();
        foreach (var f in forms.OrderBy(f => f.EntityType).ThenBy(f => f.Name))
            Forms.Add(new FormRow(f, _types.FirstOrDefault(t => t.Name == f.EntityType)?.DisplayName ?? f.EntityType));
    });

    [RelayCommand]
    private async Task NewAsync()
    {
        if (_types.Count == 0) await LoadAsync();
        var names = _types.Select(t => $"{t.DisplayName} ({t.AvailableFrom})").ToArray();
        var picked = await Shell.Current.DisplayActionSheetAsync("Form for…", "Cancel", null, names);
        var type = _types.FirstOrDefault(t => picked == $"{t.DisplayName} ({t.AvailableFrom})");
        if (type is null) return;

        string? templateKey = null;
        var templates = await api.GetTemplatesAsync(type.Name);
        if (templates.Count > 0)
        {
            var choice = await Shell.Current.DisplayActionSheetAsync("Start from", "Cancel", null,
                ["Blank form", .. templates.Select(t => t.Name)]);
            if (choice is null or "Cancel") return;
            templateKey = templates.FirstOrDefault(t => t.Name == choice)?.Key;
        }

        var name = await Ui.PromptAsync("New form", "Form name", templates.FirstOrDefault(t => t.Key == templateKey)?.Name ?? type.DisplayName);
        if (string.IsNullOrWhiteSpace(name)) return;

        FormDto? created = null;
        if (await RunAsync(async () => created = await api.CreateFormAsync(new CreateFormRequest(name, type.Name, TemplateKey: templateKey))))
            await OpenAsync(created!.Id);
    }

    /// <summary>FR-DYN-13: import a form definition (JSON) from the clipboard.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            await Ui.AlertAsync("Import", "Copy a form's JSON (e.g. from Export on another clinic) to the clipboard first.");
            return;
        }
        FormDto? created = null;
        var ok = await RunAsync(async () =>
        {
            FormDefinition definition;
            try { definition = FormJson.Deserialize(text); }
            catch (System.Text.Json.JsonException ex) { throw new InvalidOperationException($"The clipboard does not contain a form definition: {ex.Message}"); }
            created = await api.ImportFormAsync(definition);
        });
        if (ok) await OpenAsync(created!.Id);
    }

    [RelayCommand]
    private Task EditAsync(FormRow row) => OpenAsync(row.Form.Id);

    [RelayCommand]
    private Task LookupsAsync() => Ui.GoToAsync(Routes.Lookups);

    private Task OpenAsync(Guid id)
    {
        designer.FormId = id;
        return Ui.GoToAsync(Routes.FormDesigner);
    }
}

public sealed record FieldRow(FieldDefinition Field, SectionDefinition Section)
{
    public string Title => Field.Label.En is { Length: > 0 } l ? l : Field.Key;
    public string Subtitle => $"{Field.Key} · {Field.Type}" +
                              (Field.Required ? " · required" : "") +
                              (Field.VisibleWhen is not null ? " · conditional" : "") +
                              (Field.Retired ? " · retired" : "");
}

public sealed partial class SectionRow(SectionDefinition section) : ObservableObject
{
    public SectionDefinition Section { get; } = section;
    public string Title => $"{Section.Title.En}  ({Section.Key})";
    public ObservableCollection<FieldRow> Fields { get; } = new(section.Fields.Select(f => new FieldRow(f, section)));
}

/// <summary>Form designer: sections and fields, ordering, draft save, preview, publish (FR-DYN-10/11).</summary>
public sealed partial class FormDesignerViewModel : ViewModelBase, IDisposable
{
    private readonly AdminApi _api;
    private readonly FormDesignerSession _session;
    private FormDto? _form;

    public FormDesignerViewModel(AdminApi api, FormDesignerSession session)
    {
        _api = api;
        _session = session;
        _session.Changed += OnSessionChanged;
    }

    public ObservableCollection<SectionRow> Sections { get; } = [];
    public ObservableCollection<string> Problems { get; } = [];
    public ObservableCollection<BranchDto> Branches { get; } = [];

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string EntityType { get; set; } = "";
    [ObservableProperty] public partial string? Specialty { get; set; }
    [ObservableProperty] public partial bool IsActive { get; set; } = true;
    [ObservableProperty] public partial BranchDto? ContextBranch { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool HasProblems { get; set; }
    [ObservableProperty] public partial bool IsUnsaved { get; set; }

    private static readonly BranchDto AllBranches = new(Guid.Empty, "*", "All branches", null, null, true);

    partial void OnNameChanged(string value) => IsUnsaved = _form is not null;
    partial void OnSpecialtyChanged(string? value) => IsUnsaved = _form is not null;
    partial void OnIsActiveChanged(bool value) => IsUnsaved = _form is not null;
    partial void OnContextBranchChanged(BranchDto? value) => IsUnsaved = _form is not null;

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        if (_session.FormId is not { } id) return;
        if (_form?.Id == id && _session.Definition is not null) { Refresh(); return; } // returning from field editor

        var formTask = _api.GetFormAsync(id);
        var branchesTask = _api.GetBranchesAsync();
        var rolesTask = _api.GetRolesAsync();
        var listsTask = _api.GetLookupListsAsync();
        await Task.WhenAll(formTask, branchesTask, rolesTask, listsTask);

        _form = formTask.Result;
        _session.Definition = _form.Draft;
        _session.RoleNames = rolesTask.Result.Select(r => r.Name).ToList();
        _session.LookupKeys = listsTask.Result.Select(l => l.Key).OrderBy(k => k).ToList();

        Branches.Clear();
        Branches.Add(AllBranches);
        foreach (var b in branchesTask.Result) Branches.Add(b);

        Name = _form.Name;
        EntityType = _form.EntityType;
        Specialty = _form.ContextSpecialty;
        IsActive = _form.IsActive;
        ContextBranch = Branches.FirstOrDefault(b => b.Id == _form.ContextBranchId) ?? AllBranches;
        SetProblems(_form.Problems);
        UpdateStatus();
        IsUnsaved = false;
        Refresh();
    });

    private void Refresh()
    {
        Sections.Clear();
        foreach (var s in _session.Definition?.Sections ?? []) Sections.Add(new SectionRow(s));

        // Instant offline feedback while editing; the server repeats these checks (plus version rules) on save.
        if (_session.Definition is { } def)
        {
            var local = FormDefinitionValidator.Validate(def, k => _session.LookupKeys.Contains(k));
            SetProblems(local);
        }
    }

    private void SetProblems(IEnumerable<DefinitionError> problems)
    {
        Problems.Clear();
        foreach (var p in problems) Problems.Add(string.IsNullOrEmpty(p.Path) ? p.Message : $"{p.Path}: {p.Message}");
        HasProblems = Problems.Count > 0;
    }

    private void UpdateStatus() => StatusText = _form is null ? "" : _form.CurrentVersionNo is null
        ? "Not published yet"
        : $"Published v{_form.CurrentVersionNo}" + (_form.HasUnpublishedChanges ? " · draft has unpublished changes" : "");

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        IsUnsaved = true;
        MainThread.BeginInvokeOnMainThread(Refresh);
    }

    private static string Slug(string text)
    {
        var chars = text.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        var slug = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0 || !char.IsAsciiLetter(slug[0])) slug = "s_" + slug;
        return slug.Length > 50 ? slug[..50] : slug;
    }

    // ---- sections -------------------------------------------------------

    [RelayCommand]
    private async Task AddSectionAsync()
    {
        if (_session.Definition is not { } def) return;
        var title = await Ui.PromptAsync("New section", "Section title");
        if (string.IsNullOrWhiteSpace(title)) return;
        var key = Slug(title);
        while (def.Sections.Any(s => s.Key == key)) key += "_2";
        def.Sections.Add(new SectionDefinition { Key = key, Title = new LocalizedText(title.Trim()) });
        _session.NotifyChanged();
    }

    [RelayCommand]
    private async Task RenameSectionAsync(SectionRow row)
    {
        var title = await Ui.PromptAsync("Rename section", "Section title", row.Section.Title.En);
        if (string.IsNullOrWhiteSpace(title)) return;
        row.Section.Title.En = title.Trim();
        var ur = await Ui.PromptAsync("Urdu title (optional)", "Section title in Urdu", row.Section.Title.Ur);
        if (ur is not null) row.Section.Title.Ur = string.IsNullOrWhiteSpace(ur) ? null : ur.Trim();
        _session.NotifyChanged();
    }

    [RelayCommand]
    private void MoveSectionUp(SectionRow row) => Move(_session.Definition!.Sections, row.Section, -1);

    [RelayCommand]
    private void MoveSectionDown(SectionRow row) => Move(_session.Definition!.Sections, row.Section, +1);

    [RelayCommand]
    private async Task DeleteSectionAsync(SectionRow row)
    {
        if (!await Ui.ConfirmAsync("Delete section", $"Delete \"{row.Section.Title.En}\" and its {row.Section.Fields.Count} field(s)?", "Delete")) return;
        _session.Definition!.Sections.Remove(row.Section);
        _session.NotifyChanged();
    }

    // ---- fields ---------------------------------------------------------

    [RelayCommand]
    private Task AddFieldAsync(SectionRow row)
    {
        var field = new FieldDefinition { Type = FieldType.Text, Columns = 1 };
        _session.PushTarget(new FormDesignerSession.EditTarget(field, row.Section.Fields, IsNew: true, ParentGroup: null));
        return Ui.GoToAsync(Routes.FieldEditor);
    }

    [RelayCommand]
    private Task EditFieldAsync(FieldRow row)
    {
        _session.PushTarget(new FormDesignerSession.EditTarget(row.Field, row.Section.Fields, IsNew: false, ParentGroup: null));
        return Ui.GoToAsync(Routes.FieldEditor);
    }

    [RelayCommand]
    private void MoveFieldUp(FieldRow row) => Move(row.Section.Fields, row.Field, -1);

    [RelayCommand]
    private void MoveFieldDown(FieldRow row) => Move(row.Section.Fields, row.Field, +1);

    private void Move<T>(List<T> list, T item, int delta)
    {
        var i = list.IndexOf(item);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        _session.NotifyChanged();
    }

    // ---- save / publish / preview ---------------------------------------

    private async Task<bool> SaveCoreAsync()
    {
        if (_form is null || _session.Definition is null) return false;
        var branch = ContextBranch is { Id: var id } && id != Guid.Empty ? id : (Guid?)null;
        _form = await _api.SaveFormAsync(_form.Id, new SaveFormDraftRequest(Name, branch, Specialty, IsActive, _session.Definition));
        _session.Definition = _form.Draft;
        SetProblems(_form.Problems);
        UpdateStatus();
        IsUnsaved = false;
        Sections.Clear();
        foreach (var s in _session.Definition.Sections) Sections.Add(new SectionRow(s));
        return true;
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(SaveCoreAsync);

    [RelayCommand]
    private async Task PublishAsync()
    {
        if (!await RunAsync(SaveCoreAsync)) return;
        if (HasProblems)
        {
            await Ui.AlertAsync("Cannot publish", "Fix the problems listed at the top first.");
            return;
        }
        if (!await Ui.ConfirmAsync("Publish", "Publish a new version? Devices receive it on their next sync; existing records keep their version.", "Publish"))
            return;

        FormVersionDto? version = null;
        if (await RunAsync(async () => version = await _api.PublishFormAsync(_form!.Id)))
        {
            _form = await _api.GetFormAsync(_form!.Id);
            UpdateStatus();
            await Ui.AlertAsync("Published", $"Version {version!.VersionNo} is live.");
        }
    }

    [RelayCommand]
    private Task PreviewAsync() => Ui.GoToAsync(Routes.FormPreview);

    [RelayCommand]
    private async Task MoreAsync()
    {
        if (_form is null) return;
        var choice = await Shell.Current.DisplayActionSheetAsync("Form", "Cancel", null,
            "Export JSON to clipboard", "Clone as new draft", "Version history");
        switch (choice)
        {
            case "Export JSON to clipboard":
                await Clipboard.Default.SetTextAsync(FormJson.Serialize(_session.Definition!, indented: true));
                await Ui.AlertAsync("Exported", "The form definition is on the clipboard.");
                break;
            case "Clone as new draft":
                FormDto? clone = null;
                if (await RunAsync(async () => clone = await _api.CloneFormAsync(_form.Id)))
                {
                    _session.FormId = clone!.Id;
                    _form = null;
                    await LoadAsync();
                }
                break;
            case "Version history":
                List<FormVersionDto>? versions = null;
                if (await RunAsync(async () => versions = await _api.GetFormVersionsAsync(_form.Id)))
                {
                    var lines = versions!.Select(v =>
                        $"v{v.VersionNo} · {v.PublishedAt.ToLocalTime():g} · {v.PublishedBy ?? "?"}{(v.IsCurrent ? " (current)" : "")}");
                    await Ui.AlertAsync("Versions", versions!.Count == 0 ? "Not published yet." : string.Join("\n", lines));
                }
                break;
        }
    }

    public void Dispose() => _session.Changed -= OnSessionChanged;
}

/// <summary>Edits one field (or a repeating-group column). Changes apply to the shared draft on "Apply".</summary>
public sealed partial class FieldEditorViewModel(FormDesignerSession session) : ViewModelBase
{
    private FormDesignerSession.EditTarget? _target;

    public IReadOnlyList<FieldType> Types { get; } = Enum.GetValues<FieldType>();
    public IReadOnlyList<int> ColumnChoices { get; } = [1, 2, 3, 4];
    public IReadOnlyList<string> SeverityHelp => ["warning", "critical"];
    public ObservableCollection<string> LookupKeys { get; } = [];
    public ObservableCollection<string> ParentFieldKeys { get; } = [];
    public ObservableCollection<SelectableRole> ViewRoles { get; } = [];
    public ObservableCollection<SelectableRole> EditRoles { get; } = [];
    public ObservableCollection<FieldDefinition> Children { get; } = [];

    [ObservableProperty] public partial string PageTitle { get; set; } = "Field";
    [ObservableProperty] public partial bool IsNew { get; set; }
    [ObservableProperty] public partial string Key { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsNumeric), nameof(IsSelect), nameof(IsLookup), nameof(IsCalculated),
        nameof(IsGroup), nameof(IsDate), nameof(HasOptions), nameof(HoldsValue), nameof(DisplayChoices))]
    public partial FieldType Type { get; set; }

    [ObservableProperty] public partial string LabelEn { get; set; } = "";
    [ObservableProperty] public partial string? LabelUr { get; set; }
    [ObservableProperty] public partial string? Placeholder { get; set; }
    [ObservableProperty] public partial string? Help { get; set; }
    [ObservableProperty] public partial int Columns { get; set; } = 1;
    [ObservableProperty] public partial string? DefaultValue { get; set; }
    [ObservableProperty] public partial bool Required { get; set; }
    [ObservableProperty] public partial string? RequiredWhen { get; set; }
    [ObservableProperty] public partial string? VisibleWhen { get; set; }
    [ObservableProperty] public partial string? EnabledWhen { get; set; }
    [ObservableProperty] public partial string? Formula { get; set; }
    [ObservableProperty] public partial string? Min { get; set; }
    [ObservableProperty] public partial string? Max { get; set; }
    [ObservableProperty] public partial string? MinLength { get; set; }
    [ObservableProperty] public partial string? MaxLength { get; set; }
    [ObservableProperty] public partial string? Pattern { get; set; }
    [ObservableProperty] public partial string? PatternMessage { get; set; }
    [ObservableProperty] public partial string? Mask { get; set; }
    [ObservableProperty] public partial bool NotInFuture { get; set; }
    [ObservableProperty] public partial bool Unique { get; set; }
    [ObservableProperty] public partial string? Unit { get; set; }
    [ObservableProperty] public partial string? Decimals { get; set; }
    [ObservableProperty] public partial string? Display { get; set; }
    [ObservableProperty] public partial string? OptionsText { get; set; }
    [ObservableProperty] public partial string? LookupList { get; set; }
    [ObservableProperty] public partial string? LookupParentField { get; set; }
    [ObservableProperty] public partial string? AbnormalText { get; set; }
    [ObservableProperty] public partial string? MinRows { get; set; }
    [ObservableProperty] public partial string? MaxRows { get; set; }
    [ObservableProperty] public partial bool Sensitive { get; set; }
    [ObservableProperty] public partial bool Searchable { get; set; }
    [ObservableProperty] public partial bool Reportable { get; set; }
    [ObservableProperty] public partial bool ShowInList { get; set; }
    [ObservableProperty] public partial bool PrintOnPrescription { get; set; }
    [ObservableProperty] public partial bool CarryForward { get; set; }
    [ObservableProperty] public partial bool Retired { get; set; }
    [ObservableProperty] public partial string? CheckResult { get; set; }

    public bool IsText => Type is FieldType.Text or FieldType.MultilineText or FieldType.RichNote or FieldType.Phone or FieldType.Email or FieldType.IdNumber;
    public bool IsNumeric => Type is FieldType.Integer or FieldType.Decimal or FieldType.Calculated;
    public bool IsSelect => Type is FieldType.SingleSelect or FieldType.MultiSelect;
    public bool IsLookup => Type == FieldType.Lookup;
    public bool IsCalculated => Type == FieldType.Calculated;
    public bool IsGroup => Type == FieldType.RepeatingGroup && session.CurrentTarget?.ParentGroup is null;
    public bool IsDate => Type is FieldType.Date or FieldType.DateTime;
    public bool HasOptions => IsSelect;
    public bool HoldsValue => Type is not (FieldType.Label or FieldType.Calculated or FieldType.RepeatingGroup);
    public IReadOnlyList<string> DisplayChoices => Type == FieldType.MultiSelect ? ["checkboxes", "chips"] : ["dropdown", "radio"];

    public void Prepare()
    {
        _target = session.CurrentTarget;
        if (_target is null || session.Definition is not { } def) return;
        var f = _target.Field;

        IsNew = _target.IsNew;
        PageTitle = _target.IsNew ? (_target.ParentGroup is null ? "New field" : $"New column in {_target.ParentGroup.Label.En}") : $"Field · {f.Key}";
        Key = f.Key;
        Type = f.Type;
        LabelEn = f.Label.En;
        LabelUr = f.Label.Ur;
        Placeholder = f.Placeholder?.En;
        Help = f.Help?.En;
        Columns = Math.Clamp(f.Columns, 1, 4);
        DefaultValue = f.Default is null ? null : FormValues.AsString(f.Default);
        Required = f.Required;
        RequiredWhen = f.RequiredWhen;
        VisibleWhen = f.VisibleWhen;
        EnabledWhen = f.EnabledWhen;
        Formula = f.Formula;
        Min = f.Min?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Max = f.Max?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MinLength = f.MinLength?.ToString();
        MaxLength = f.MaxLength?.ToString();
        Pattern = f.Pattern;
        PatternMessage = f.PatternMessage?.En;
        Mask = f.Mask;
        NotInFuture = f.NotInFuture;
        Unique = f.Unique;
        Unit = f.Unit;
        Decimals = f.Decimals?.ToString();
        Display = f.Display;
        OptionsText = string.Join("\n", (f.Options ?? []).Select(o => o.Label.Ur is { } ur ? $"{o.Code} | {o.Label.En} | {ur}" : $"{o.Code} | {o.Label.En}"));
        LookupList = f.LookupList;
        LookupParentField = f.LookupParentField;
        AbnormalText = string.Join("\n", (f.Abnormal ?? []).Select(a => a.Message is { } m ? $"{a.When} | {a.Severity} | {m.En}" : $"{a.When} | {a.Severity}"));
        MinRows = f.MinRows?.ToString();
        MaxRows = f.MaxRows?.ToString();
        Sensitive = f.Sensitive;
        Searchable = f.Searchable;
        Reportable = f.Reportable;
        ShowInList = f.ShowInList;
        PrintOnPrescription = f.PrintOnPrescription;
        CarryForward = f.CarryForward;
        Retired = f.Retired;
        CheckResult = null;
        ErrorMessage = null;

        LookupKeys.Clear();
        foreach (var k in session.LookupKeys) LookupKeys.Add(k);
        ParentFieldKeys.Clear();
        foreach (var k in _target.Container.Where(x => x != f && x.Type is FieldType.Lookup or FieldType.SingleSelect).Select(x => x.Key)) ParentFieldKeys.Add(k);

        FillRoles(ViewRoles, f.Permissions?.ViewRoles);
        FillRoles(EditRoles, f.Permissions?.EditRoles);

        Children.Clear();
        foreach (var c in f.Fields ?? []) Children.Add(c);
        OnPropertyChanged(nameof(IsGroup));
    }

    private void FillRoles(ObservableCollection<SelectableRole> target, List<string>? selected)
    {
        target.Clear();
        foreach (var r in session.RoleNames)
            target.Add(new SelectableRole(r) { IsSelected = selected?.Contains(r) == true });
    }

    /// <summary>Builds the field from the editor, or returns an error message.</summary>
    private (FieldDefinition? Field, string? Error) Build()
    {
        static decimal? Dec(string? s) => decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        static int? Int(string? s) => int.TryParse(s, out var i) ? i : null;
        static string? Opt(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        var f = _target!.Field;
        var built = FormJson.Deserialize(FormJson.Serialize(new FormDefinition { Sections = [new() { Fields = [f] }] })).Sections[0].Fields[0];

        built.Key = Key.Trim();
        built.Type = Type;
        built.Label = new LocalizedText(LabelEn.Trim(), Opt(LabelUr));
        built.Placeholder = Opt(Placeholder) is { } ph ? new LocalizedText(ph) : null;
        built.Help = Opt(Help) is { } help ? new LocalizedText(help) : null;
        built.Columns = Columns;
        built.Default = Opt(DefaultValue) is { } dv ? JsonValue.Create(dv) : null;
        built.Required = Required;
        built.RequiredWhen = Opt(RequiredWhen);
        built.VisibleWhen = Opt(VisibleWhen);
        built.EnabledWhen = Opt(EnabledWhen);
        built.Formula = IsCalculated ? Opt(Formula) : null;
        built.Min = Dec(Min);
        built.Max = Dec(Max);
        built.MinLength = Int(MinLength);
        built.MaxLength = Int(MaxLength);
        built.Pattern = Opt(Pattern);
        built.PatternMessage = Opt(PatternMessage) is { } pm ? new LocalizedText(pm) : null;
        built.Mask = Opt(Mask);
        built.NotInFuture = NotInFuture;
        built.Unique = Unique;
        built.Unit = Opt(Unit);
        built.Decimals = Int(Decimals);
        built.Display = IsSelect ? Opt(Display) : null;
        built.LookupList = IsLookup ? Opt(LookupList) : null;
        built.LookupParentField = IsLookup ? Opt(LookupParentField) : null;
        built.MinRows = Int(MinRows);
        built.MaxRows = Int(MaxRows);
        built.Sensitive = Sensitive;
        built.Searchable = Searchable;
        built.Reportable = Reportable;
        built.ShowInList = ShowInList;
        built.PrintOnPrescription = PrintOnPrescription;
        built.CarryForward = CarryForward;
        built.Retired = Retired;
        built.Fields = Type == FieldType.RepeatingGroup ? Children.ToList() : null;

        if (IsSelect)
        {
            built.Options = [];
            foreach (var line in (OptionsText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('|', StringSplitOptions.TrimEntries);
                var label = parts.Length > 1 ? parts[1] : parts[0];
                built.Options.Add(new FieldOption(parts[0], label, parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null));
            }
        }
        else built.Options = null;

        built.Abnormal = null;
        foreach (var line in (AbnormalText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            (built.Abnormal ??= []).Add(new AbnormalRule
            {
                When = parts[0],
                Severity = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "warning",
                Message = parts.Length > 2 && parts[2].Length > 0 ? new LocalizedText(parts[2]) : null,
            });
        }

        var view = ViewRoles.Where(r => r.IsSelected).Select(r => r.Name).ToList();
        var edit = EditRoles.Where(r => r.IsSelected).Select(r => r.Name).ToList();
        built.Permissions = view.Count + edit.Count == 0 ? null : new FieldPermissions { ViewRoles = view, EditRoles = edit };

        // Key must be unique among siblings.
        if (_target.Container.Any(x => x != f && x.Key == built.Key))
            return (null, $"Another field already uses the key '{built.Key}'.");
        return (built, null);
    }

    /// <summary>Validates this field inside a copy of the whole draft, offline, and lists only its problems.</summary>
    private List<string> CheckInDraft(FieldDefinition built)
    {
        var def = session.Definition!.Clone();
        var (container, index) = Locate(def);
        if (index >= 0) container[index] = built; else container.Add(built);

        var all = FormDefinitionValidator.Validate(def, k => session.LookupKeys.Contains(k));
        var prefix = PathOf(def, built);
        return all.Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal) || e.Path.Length == 0)
            .Select(e => e.Message).Distinct().ToList();
    }

    /// <summary>The list in the copied draft that corresponds to the field's list in the live draft.</summary>
    private (List<FieldDefinition> Container, int Index) Locate(FormDefinition copy)
    {
        var t = _target!;
        var def = session.Definition!;
        for (var s = 0; s < def.Sections.Count; s++)
        {
            if (ReferenceEquals(def.Sections[s].Fields, t.Container))
                return (copy.Sections[s].Fields, t.Container.IndexOf(t.Field));
            for (var i = 0; i < def.Sections[s].Fields.Count; i++)
            {
                if (def.Sections[s].Fields[i].Fields is { } columns && ReferenceEquals(columns, t.Container))
                    return (copy.Sections[s].Fields[i].Fields!, t.Container.IndexOf(t.Field));
            }
        }
        if (copy.Sections.Count == 0) copy.Sections.Add(new SectionDefinition { Key = "main", Title = new LocalizedText("Main") });
        return (copy.Sections[0].Fields, -1);
    }

    private static string PathOf(FormDefinition def, FieldDefinition field)
    {
        for (var s = 0; s < def.Sections.Count; s++)
        {
            for (var i = 0; i < def.Sections[s].Fields.Count; i++)
            {
                var f = def.Sections[s].Fields[i];
                if (ReferenceEquals(f, field)) return $"sections[{s}].fields[{i}]";
                for (var c = 0; c < (f.Fields?.Count ?? 0); c++)
                    if (ReferenceEquals(f.Fields![c], field)) return $"sections[{s}].fields[{i}].fields[{c}]";
            }
        }
        return "";
    }

    [RelayCommand]
    private void Check()
    {
        var (built, error) = Build();
        if (built is null) { CheckResult = error; return; }
        var problems = CheckInDraft(built);
        CheckResult = problems.Count == 0 ? "✓ No problems found." : string.Join("\n", problems);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var (built, error) = Build();
        if (built is null) { ErrorMessage = error; return; }
        var problems = CheckInDraft(built);
        if (problems.Count > 0 && !await Ui.ConfirmAsync("Problems", string.Join("\n", problems) + "\n\nKeep the field anyway? (Publishing will be blocked until fixed.)", "Keep"))
            return;

        var t = _target!;
        var index = t.Container.IndexOf(t.Field);
        if (index >= 0) t.Container[index] = built; else t.Container.Add(built);
        session.PopTarget();
        session.NotifyChanged();
        await Ui.GoBackAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        var t = _target!;
        if (!t.IsNew)
        {
            var choice = await Shell.Current.DisplayActionSheetAsync(
                "Fields that already hold data cannot be deleted; retire them instead.", "Cancel", null, "Retire (hide on new records)", "Delete");
            if (choice == "Retire (hide on new records)") { Retired = true; await ApplyAsync(); return; }
            if (choice != "Delete") return;
            t.Container.Remove(t.Field);
            session.NotifyChanged();
        }
        session.PopTarget();
        await Ui.GoBackAsync();
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        session.PopTarget();
        await Ui.GoBackAsync();
    }

    // ---- repeating group columns ---------------------------------------

    [RelayCommand]
    private Task AddChildAsync()
    {
        var (group, _) = Build();
        if (group is null) return Task.CompletedTask;
        group.Fields ??= [];
        _target = _target! with { Field = Replace(group) };
        var child = new FieldDefinition { Type = FieldType.Text };
        session.PushTarget(new FormDesignerSession.EditTarget(child, _target.Field.Fields!, IsNew: true, ParentGroup: _target.Field));
        return Ui.GoToAsync(Routes.FieldEditor);
    }

    [RelayCommand]
    private Task EditChildAsync(FieldDefinition child)
    {
        var (group, _) = Build();
        if (group is null) return Task.CompletedTask;
        _target = _target! with { Field = Replace(group) };
        var current = _target.Field.Fields!.First(c => c.Key == child.Key);
        session.PushTarget(new FormDesignerSession.EditTarget(current, _target.Field.Fields!, IsNew: false, ParentGroup: _target.Field));
        return Ui.GoToAsync(Routes.FieldEditor);
    }

    /// <summary>Writes the in-progress group into the draft so child edits have a stable parent.</summary>
    private FieldDefinition Replace(FieldDefinition group)
    {
        var t = _target!;
        var index = t.Container.IndexOf(t.Field);
        if (index >= 0) t.Container[index] = group; else t.Container.Add(group);
        session.PopTarget();
        var updated = t with { Field = group, IsNew = false };
        session.PushTarget(updated);
        return group;
    }
}

public sealed partial class SelectableRole(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>FR-DYN-10 live preview: phone/tablet width, English/Urdu, role and context variables.</summary>
public sealed partial class FormPreviewViewModel(FormDesignerSession session, SyncCoordinator sync) : ViewModelBase
{
    public ObservableCollection<string> Roles { get; } = [];
    public IReadOnlyList<string> Genders { get; } = ["F", "M", "O"];

    [ObservableProperty] public partial DynamicFormModel? Model { get; set; }
    [ObservableProperty] public partial double PreviewWidth { get; set; } = 420;
    [ObservableProperty] public partial string WidthLabel { get; set; } = "Tablet width";
    [ObservableProperty] public partial string Language { get; set; } = "en";
    [ObservableProperty] public partial string? Role { get; set; }
    [ObservableProperty] public partial string Gender { get; set; } = "F";
    [ObservableProperty] public partial string Age { get; set; } = "30";
    [ObservableProperty] public partial bool HasVariables { get; set; }
    [ObservableProperty] public partial string? Result { get; set; }

    partial void OnRoleChanged(string? value) => _ = BuildAsync();
    partial void OnGenderChanged(string value) => _ = BuildAsync();
    partial void OnAgeChanged(string value) => _ = BuildAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        Roles.Clear();
        foreach (var r in session.RoleNames) Roles.Add(r);
        Role ??= Roles.FirstOrDefault();
        await BuildAsync();
    }

    private async Task BuildAsync()
    {
        if (session.Definition is not { } def) return;
        ErrorMessage = null;
        Result = null;
        try
        {
            var evaluator = new FormEvaluator(def);
            var version = new FormVersionInfo(Guid.Empty, 0, true, DateTime.UtcNow, def, evaluator);
            var entity = DynamicEntityTypes.Find(def.EntityType);
            HasVariables = entity?.Variables.Count > 0;

            var variables = new Dictionary<string, object?>
            {
                ["gender"] = Gender,
                ["ageYears"] = double.TryParse(Age, out var a) ? a : null,
                ["ageMonths"] = double.TryParse(Age, out var am) ? am * 12 : null,
                ["specialty"] = null,
            };
            // Lookup options come from the lists already synced to this device.
            var lookups = sync.IsStarted ? await sync.Forms.LoadLookupsAsync(def) : new();
            Model = new DynamicFormModel(version, new JsonObject(),
                new EvaluationContext { Roles = Role is null ? [] : [Role], Variables = variables, Language = Language }, lookups);
        }
        catch (FormDefinitionException ex)
        {
            Model = null;
            ErrorMessage = $"The form cannot be previewed yet: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleWidth()
    {
        var phone = PreviewWidth > 500;
        PreviewWidth = phone ? 420 : 1000;
        WidthLabel = phone ? "Tablet width" : "Phone width";
    }

    [RelayCommand]
    private async Task ToggleLanguageAsync()
    {
        Language = Language == "ur" ? "en" : "ur";
        await BuildAsync();
    }

    [RelayCommand]
    private void Validate()
    {
        if (Model is null) return;
        Result = Model.Validate() ? "✓ The test entry is valid." : $"{Model.Evaluation.Errors.Count()} problem(s) – see the highlighted fields.";
    }
}
