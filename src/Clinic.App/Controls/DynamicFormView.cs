using System.Globalization;
using System.Text.Json.Nodes;
using Clinic.Client.Core.Forms;
using Clinic.Shared.Forms;
using Microsoft.Maui.Layouts;

namespace Clinic.App.Controls;

/// <summary>
/// FR-DYN-20…22: renders a <see cref="DynamicFormModel"/> at runtime. One control per field type, a responsive
/// flex grid (1 column on phones, up to 4 on tablet/desktop, honouring each field's width), and live refresh of
/// visibility, enabled state, calculations, abnormal flags and errors after every change — all offline.
/// </summary>
public sealed class DynamicFormView : ContentView
{
    public static readonly BindableProperty ModelProperty = BindableProperty.Create(
        nameof(Model), typeof(DynamicFormModel), typeof(DynamicFormView),
        propertyChanged: (b, o, n) => ((DynamicFormView)b).OnModelChanged((DynamicFormModel?)o, (DynamicFormModel?)n));

    public static readonly BindableProperty IsReadOnlyProperty = BindableProperty.Create(
        nameof(IsReadOnly), typeof(bool), typeof(DynamicFormView), false,
        propertyChanged: (b, _, _) => ((DynamicFormView)b).Refresh());

    private readonly VerticalStackLayout _root = new() { Spacing = 14 };
    private readonly List<FieldEditor> _editors = [];
    private readonly Dictionary<string, View> _sections = new();
    private int _columns = 1;

    public DynamicFormView()
    {
        Content = _root;
        SizeChanged += (_, _) =>
        {
            var columns = Width switch { < 600 => 1, < 900 => 2, _ => 4 };
            if (columns == _columns) return;
            _columns = columns;
            Rebuild();
        };
    }

    public DynamicFormModel? Model
    {
        get => (DynamicFormModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    internal int Columns => _columns;

    private void OnModelChanged(DynamicFormModel? old, DynamicFormModel? model)
    {
        if (old is not null) old.Evaluated -= OnEvaluated;
        if (model is not null) model.Evaluated += OnEvaluated;
        Rebuild();
    }

    private void OnEvaluated(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(Refresh);

    private void Rebuild()
    {
        _root.Children.Clear();
        _editors.Clear();
        _sections.Clear();
        if (Model is not { } model) return;

        _root.FlowDirection = model.Language == "ur" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        foreach (var section in model.Definition.Sections)
        {
            var grid = new FlexLayout { Wrap = FlexWrap.Wrap, AlignItems = FlexAlignItems.Start, AlignContent = FlexAlignContent.Start };
            foreach (var field in section.Fields)
            {
                var editor = FieldEditor.Create(this, model, field, field.Key, null, null);
                _editors.Add(editor);
                var span = Math.Clamp(field.Columns, 1, 4);
                FlexLayout.SetBasis(editor.Container, new FlexBasis(Math.Min(span, _columns) / (float)_columns, isRelative: true));
                grid.Children.Add(editor.Container);
            }

            var header = new Label
            {
                Text = section.Title.Get(model.Language),
                FontSize = 17,
                FontFamily = "OpenSansSemibold",
                Margin = new Thickness(0, 4, 0, 0),
            };
            var body = new VerticalStackLayout { Spacing = 6, Children = { grid } };
            body.IsVisible = !section.StartCollapsed;

            View sectionView;
            if (section.Collapsible)
            {
                var toggle = new Button
                {
                    Text = body.IsVisible ? "Hide" : "Show",
                    FontSize = 12,
                    Padding = new Thickness(10, 2),
                    MinimumHeightRequest = 32,
                    BackgroundColor = Colors.Transparent,
                    TextColor = (Color)Application.Current!.Resources["Primary"],
                };
                toggle.Clicked += (_, _) => { body.IsVisible = !body.IsVisible; toggle.Text = body.IsVisible ? "Hide" : "Show"; };
                sectionView = new VerticalStackLayout
                {
                    Children = { new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], Children = { header, Col(toggle, 1) } }, body },
                };
            }
            else
            {
                sectionView = new VerticalStackLayout { Spacing = 6, Children = { header, body } };
            }

            _sections[section.Key] = sectionView;
            _root.Children.Add(sectionView);
        }
        Refresh();
    }

    private static View Col(View v, int col) { Grid.SetColumn(v, col); return v; }

    private void Refresh()
    {
        if (Model is not { } model) return;
        foreach (var (key, view) in _sections)
            view.IsVisible = model.Evaluation.SectionVisible.GetValueOrDefault(key, true);
        foreach (var editor in _editors) editor.Refresh(IsReadOnly);
    }
}

/// <summary>Label + input + help + error for one field (or one repeating-group cell).</summary>
internal abstract class FieldEditor
{
    private static readonly Color CriticalColor = Color.FromArgb("#B91C1C");
    private static readonly Color WarningColor = Color.FromArgb("#B45309");

    protected FieldEditor(DynamicFormView owner, DynamicFormModel model, FieldDefinition field, string path, string? group, int? row)
    {
        Owner = owner;
        Model = model;
        Field = field;
        Path = path;
        Group = group;
        Row = row;
    }

    protected DynamicFormView Owner { get; }
    protected DynamicFormModel Model { get; }
    public FieldDefinition Field { get; }
    protected string Path { get; }
    protected string? Group { get; }
    protected int? Row { get; }
    protected bool Updating { get; set; }

    public View Container { get; private set; } = null!;
    private Label _label = null!;
    private Label _error = null!;
    private Label _abnormal = null!;
    private View _input = null!;

    public static FieldEditor Create(DynamicFormView owner, DynamicFormModel model, FieldDefinition field, string path, string? group, int? row)
    {
        FieldEditor editor = field.Type switch
        {
            FieldType.Text or FieldType.Phone or FieldType.Email or FieldType.IdNumber => new TextEditor(owner, model, field, path, group, row, multiline: false),
            FieldType.MultilineText or FieldType.RichNote => new TextEditor(owner, model, field, path, group, row, multiline: true),
            FieldType.Integer or FieldType.Decimal => new NumberEditor(owner, model, field, path, group, row),
            FieldType.Date => new DateEditor(owner, model, field, path, group, row),
            FieldType.Time => new TimeEditor(owner, model, field, path, group, row),
            FieldType.DateTime => new DateEditor(owner, model, field, path, group, row, withTime: true),
            FieldType.YesNo => new YesNoEditor(owner, model, field, path, group, row),
            FieldType.SingleSelect when field.Display == "radio" => new RadioEditor(owner, model, field, path, group, row),
            FieldType.SingleSelect or FieldType.Lookup => new PickerEditor(owner, model, field, path, group, row),
            FieldType.MultiSelect => new MultiSelectEditor(owner, model, field, path, group, row),
            FieldType.Calculated => new CalculatedEditor(owner, model, field, path, group, row),
            FieldType.RepeatingGroup when row is null => new GroupEditor(owner, model, field, path),
            FieldType.Label => new StaticEditor(owner, model, field, path, group, row, null),
            _ => new StaticEditor(owner, model, field, path, group, row, "This field type is captured from Phase 4 (attachments)."),
        };
        editor.Build();
        return editor;
    }

    private void Build()
    {
        var lang = Model.Language;
        _label = new Label { FontSize = 13, FontFamily = "OpenSansSemibold", Text = Field.Label.Get(lang) };
        _input = CreateInput();
        _error = new Label { FontSize = 12, TextColor = CriticalColor, IsVisible = false };
        _abnormal = new Label { FontSize = 12, IsVisible = false };

        var stack = new VerticalStackLayout { Spacing = 3, Padding = new Thickness(0, 0, 12, 10) };
        if (Field.Type != FieldType.Label) stack.Children.Add(_label);
        stack.Children.Add(_input);
        if (Field.Help is { } help && Field.Type != FieldType.Label)
            stack.Children.Add(new Label { Text = help.Get(lang), FontSize = 12, TextColor = Colors.Gray });
        stack.Children.Add(_abnormal);
        stack.Children.Add(_error);
        SemanticProperties.SetDescription(_input, Field.Label.Get(lang));
        Container = stack;
    }

    protected abstract View CreateInput();

    /// <summary>Shows the model's current value in the control (skipped while the user is typing in it).</summary>
    protected abstract void ShowValue(JsonNode? value);

    protected virtual void SetEnabled(bool enabled) => _input.IsEnabled = enabled;

    protected JsonNode? CurrentValue => Row is { } r && Group is { } g ? Model.GetRowValue(g, r, Field.Key) : Model.GetValue(Field.Key);

    protected void Commit(JsonNode? value)
    {
        if (Updating) return;
        if (Row is { } r && Group is { } g) Model.SetRowValue(g, r, Field.Key, value);
        else Model.SetValue(Field.Key, value);
    }

    protected string L(LocalizedText t) => t.Get(Model.Language);

    public virtual void Refresh(bool readOnly)
    {
        var st = Model.State(Path);
        Container.IsVisible = st.Visible;
        if (!st.Visible) return;

        _label.Text = L(Field.Label) + (st.Required ? " *" : "") + (Field.Unit is { } u && Field.Type is not (FieldType.Integer or FieldType.Decimal) ? $" ({u})" : "");
        SetEnabled(!readOnly && st.Enabled && st.CanEdit);

        Updating = true;
        try { ShowValue(CurrentValue); }
        finally { Updating = false; }

        var errors = Model.VisibleErrors(Path);
        _error.Text = string.Join("\n", errors);
        _error.IsVisible = errors.Count > 0;

        _abnormal.IsVisible = st.Abnormal != AbnormalSeverity.None;
        if (_abnormal.IsVisible)
        {
            _abnormal.TextColor = st.Abnormal == AbnormalSeverity.Critical ? CriticalColor : WarningColor;
            _abnormal.Text = (st.Abnormal == AbnormalSeverity.Critical ? "● Critical" : "● Abnormal") +
                             (st.AbnormalMessage is { } m ? $": {m}" : "");
        }
        OnAbnormal(st.Abnormal == AbnormalSeverity.None ? null : _abnormal.TextColor);
    }

    protected virtual void OnAbnormal(Color? color) { }

    protected static string Format(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "Yes" : "No",
        JsonValue v when FormValues.AsDecimal(v) is { } d && v.GetValueKind() == System.Text.Json.JsonValueKind.Number => d.ToString("0.######", CultureInfo.InvariantCulture),
        JsonArray a => string.Join(", ", a.Select(FormValues.AsString)),
        _ => FormValues.AsString(node) ?? "",
    };
}

internal sealed class TextEditor(DynamicFormView o, DynamicFormModel m, FieldDefinition f, string p, string? g, int? r, bool multiline)
    : FieldEditor(o, m, f, p, g, r)
{
    private InputView _box = null!;

    protected override View CreateInput()
    {
        _box = multiline
            ? new Editor { AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 80 }
            : new Entry
            {
                Keyboard = Field.Type switch
                {
                    FieldType.Phone => Keyboard.Telephone,
                    FieldType.Email => Keyboard.Email,
                    FieldType.IdNumber when Field.Mask?.All(c => c is '#' or '-') == true => Keyboard.Numeric,
                    _ => Keyboard.Default,
                },
            };
        _box.Placeholder = Field.Placeholder is { } ph ? L(ph) : Field.Mask?.Replace('#', '0');
        if (Field.MaxLength is { } max) _box.MaxLength = max;
        _box.TextChanged += (_, e) => Commit(string.IsNullOrEmpty(e.NewTextValue) ? null : JsonValue.Create(e.NewTextValue));
        return _box;
    }

    protected override void ShowValue(JsonNode? value)
    {
        if (_box.IsFocused) return;
        var text = FormValues.AsString(value) ?? "";
        if (_box.Text != text) _box.Text = text;
    }
}

internal sealed class NumberEditor(DynamicFormView o, DynamicFormModel m, FieldDefinition f, string p, string? g, int? r)
    : FieldEditor(o, m, f, p, g, r)
{
    private Entry _entry = null!;

    protected override View CreateInput()
    {
        _entry = new Entry { Keyboard = Keyboard.Numeric, Placeholder = Field.Placeholder is { } ph ? L(ph) : null };
        _entry.TextChanged += (_, e) =>
        {
            var text = e.NewTextValue?.Trim();
            if (string.IsNullOrEmpty(text)) Commit(null);
            else if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) Commit(JsonValue.Create(n));
            else Commit(JsonValue.Create(text)); // validation reports "must be a number"
        };
        if (Field.Unit is not { } unit) return _entry;

        var grid = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 6 };
        grid.Children.Add(_entry);
        var unitLabel = new Label { Text = unit, VerticalOptions = LayoutOptions.Center, TextColor = Colors.Gray };
        Grid.SetColumn(unitLabel, 1);
        grid.Children.Add(unitLabel);
        return grid;
    }

    protected override void SetEnabled(bool enabled) => _entry.IsEnabled = enabled;

    protected override void ShowValue(JsonNode? value)
    {
        if (_entry.IsFocused) return;
        var text = Format(value);
        if (_entry.Text != text) _entry.Text = text;
    }

    protected override void OnAbnormal(Color? color) => _entry.TextColor = color ?? (Color)Application.Current!.Resources[
        Application.Current.RequestedTheme == AppTheme.Dark ? "White" : "Black"];
}

internal sealed class DateEditor(DynamicFormView o, DynamicFormModel m, FieldDefinition f, string p, string? g, int? r, bool withTime = false)
    : FieldEditor(o, m, f, p, g, r)
{
    private DatePicker _date = null!;
    private TimePicker _time = null!;
    private Button _set = null!;
    private Button _clear = null!;

    protected override View CreateInput()
    {
        _date = new DatePicker { Format = "dd MMM yyyy" };
        _time = new TimePicker { Format = "HH:mm", IsVisible = withTime };
        _set = new Button { Text = withTime ? "Set date & time" : "Set date", FontSize = 13, Padding = new Thickness(12, 4) };
        _clear = new Button { Text = "✕", FontSize = 13, Padding = new Thickness(10, 4), BackgroundColor = Colors.Transparent, TextColor = Colors.Gray };
        SemanticProperties.SetDescription(_clear, "Clear");

        _set.Clicked += (_, _) => Commit(Stamp(DateTime.Today, withTime ? DateTime.Now.TimeOfDay : TimeSpan.Zero));
        _clear.Clicked += (_, _) => Commit(null);
        _date.DateSelected += (_, e) => Commit(Stamp(e.NewDate ?? DateTime.Today, _time.Time ?? TimeSpan.Zero));
        _time.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TimePicker.Time) && withTime) Commit(Stamp(_date.Date ?? DateTime.Today, _time.Time ?? TimeSpan.Zero));
        };

        return new HorizontalStackLayout { Spacing = 6, Children = { _date, _time, _set, _clear } };
    }

    private JsonNode Stamp(DateTime date, TimeSpan time) => withTime
        ? JsonValue.Create((date.Date + time).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture))
        : JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    protected override void SetEnabled(bool enabled) => _date.IsEnabled = _time.IsEnabled = _set.IsEnabled = _clear.IsEnabled = enabled;

    protected override void ShowValue(JsonNode? value)
    {
        var d = FormValues.AsDate(value);
        _set.IsVisible = d is null;
        _date.IsVisible = _clear.IsVisible = d is not null;
        _time.IsVisible = withTime && d is not null;
        if (d is { } dt)
        {
            if (_date.Date?.Date != dt.Date) _date.Date = dt.Date;
            if (withTime && _time.Time != dt.TimeOfDay) _time.Time = dt.TimeOfDay;
        }
    }
}

internal sealed class TimeEditor(DynamicFormView o, DynamicFormModel m, FieldDefinition f, string p, string? g, int? r)
    : FieldEditor(o, m, f, p, g, r)
{
    private TimePicker _time = null!;
    private Button _set = null!;
    private Button _clear = null!;

    protected override View CreateInput()
    {
        _time = new TimePicker { Format = "HH:mm" };
        _set = new Button { Text = "Set time", FontSize = 13, Padding = new Thickness(12, 4) };
        _clear = new Button { Text = "✕", FontSize = 13, Padding = new Thickness(10, 4), BackgroundColor = Colors.Transparent, TextColor = Colors.Gray };
        _set.Clicked += (_, _) => Commit(JsonValue.Create(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture)));
        _clear.Clicked += (_, _) => Commit(null);
        _time.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TimePicker.Time) && _time.Time is { } t)
                Commit(JsonValue.Create($"{t.Hours:00}:{t.Minutes:00}"));
        };
        return new HorizontalStackLayout { Spacing = 6, Children = { _time, _set, _clear } };
    }

    protected override void SetEnabled(bool enabled) => _time.IsEnabled = _set.IsEnabled = _clear.IsEnabled = enabled;

    protected override void ShowValue(JsonNode? value)
    {
        var s = FormValues.AsString(value);
        var has = TimeSpan.TryParseExact(s, @"hh\:mm", CultureInfo.InvariantCulture, out var t);
        _set.IsVisible = !has;
        _time.IsVisible = _clear.IsVisible = has;
        if (has && _time.Time != t) _time.Time = t;
    }
}

internal sealed class YesNoEditor(DynamicFormView o, DynamicFormModel m, FieldDefinition f, string p, string? g, int? r)
    : FieldEditor(o, m, f, p, g, r)
{
    private RadioButton _yes = null!;
    private RadioButton _no = null!;

    protected override View CreateInput()
    {
        var group = $"{Path}-{Guid.NewGuid():N}";
        _yes = new RadioButton { Content = "Yes", GroupName = group };
        _no = new RadioButton { Content = "No", GroupName = group };
        _yes.CheckedChanged += (_, e) => { if (e.Value) Commit(JsonValue.Create(true)); };
        _no.CheckedChanged += (_, e) => { if (e.Value) Commit(JsonValue.Create(false)); };
        return new HorizontalStackLayout { Spacing = 16, Children = { _yes, _no } };
    }

    protected override void SetEnabled(bool enabled) => _yes.IsEnabled = _no.IsEnabled = enabled;

    protected override void ShowValue(JsonNode? value)
    {
        var b = FormValues.AsBool(value);
        if (_yes.IsChecked != (b == true)) _yes.IsChecked = b == true;
        if (_no.IsChecked != (b == false)) _no.IsChecked = b == false;
    }
}

internal sealed class RadioEditor(DynamicFormView o, DynamicFormModel m, FieldDefinition f, string p, string? g, int? r)
    : FieldEditor(o, m, f, p, g, r)
{
    private readonly List<(string Code, RadioButton Button)> _buttons = [];

    protected override View CreateInput()
    {
        var group = $"{Path}-{Guid.NewGuid():N}";
        var layout = new FlexLayout { Wrap = FlexWrap.Wrap };
        foreach (var option in Model.OptionsFor(Field))
        {
            var rb = new RadioButton { Content = L(option.Label), GroupName = group, Margin = new Thickness(0, 0, 14, 0) };
            var code = option.Code;
            rb.CheckedChanged += (_, e) => { if (e.Value) Commit(JsonValue.Create(code)); };
            _buttons.Add((code, rb));
            layout.Children.Add(rb);
        }
        return layout;
    }

    protected override void SetEnabled(bool enabled) { foreach (var (_, b) in _buttons) b.IsEnabled = enabled; }

    protected override void ShowValue(JsonNode? value)
    {
        var code = FormValues.AsString(value);
        foreach (var (c, b) in _buttons)
            if (b.IsChecked != (c == code)) b.IsChecked = c == code;
    }
}

/// <summary>Dropdown for single select and lookups; lookup options follow their parent field (cascading).</summary>
internal sealed class PickerEditor(DynamicFormView o, DynamicFormModel m, FieldDefinition f, string p, string? g, int? r)
    : FieldEditor(o, m, f, p, g, r)
{
    private Picker _picker = null!;
    private List<FieldOption> _options = [];

    protected override View CreateInput()
    {
        _picker = new Picker { Title = Field.Placeholder is { } ph ? L(ph) : "Select…" };
        _picker.SelectedIndexChanged += (_, _) =>
        {
            if (Updating) return;
            var i = _picker.SelectedIndex;
            Commit(i <= 0 || i > _options.Count ? null : JsonValue.Create(_options[i - 1].Code));
        };
        return _picker;
    }

    protected override void ShowValue(JsonNode? value)
    {
        var options = Model.OptionsFor(Field, Row, Group).ToList();
        if (!options.Select(o => o.Code).SequenceEqual(_options.Select(o => o.Code)))
        {
            _options = options;
            _picker.ItemsSource = new[] { "—" }.Concat(options.Select(o => L(o.Label))).ToList();
        }
        var code = FormValues.AsString(value);
        var index = code is null ? 0 : _options.FindIndex(o => o.Code == code) + 1;
        if (_picker.SelectedIndex != index) _picker.SelectedIndex = index;
    }
}

internal sealed class MultiSelectEditor(DynamicFormView o, DynamicFormModel m, FieldDefinition f, string p, string? g, int? r)
    : FieldEditor(o, m, f, p, g, r)
{
    private readonly List<(string Code, CheckBox Box)> _boxes = [];

    protected override View CreateInput()
    {
        var layout = new FlexLayout { Wrap = FlexWrap.Wrap };
        foreach (var option in Model.OptionsFor(Field))
        {
            var box = new CheckBox();
            var code = option.Code;
            box.CheckedChanged += (_, _) =>
            {
                if (Updating) return;
                var selected = _boxes.Where(x => x.Box.IsChecked).Select(x => (JsonNode?)JsonValue.Create(x.Code)).ToArray();
                Commit(new JsonArray(selected));
            };
            _boxes.Add((code, box));
            var label = new Label { Text = L(option.Label), VerticalOptions = LayoutOptions.Center };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => { if (box.IsEnabled) box.IsChecked = !box.IsChecked; };
            label.GestureRecognizers.Add(tap);
            SemanticProperties.SetDescription(box, L(option.Label));
            layout.Children.Add(new HorizontalStackLayout { Margin = new Thickness(0, 0, 14, 0), Children = { box, label } });
        }
        return layout;
    }

    protected override void SetEnabled(bool enabled) { foreach (var (_, b) in _boxes) b.IsEnabled = enabled; }

    protected override void ShowValue(JsonNode? value)
    {
        var selected = FormValues.AsStringList(value).ToHashSet();
        foreach (var (c, b) in _boxes)
            if (b.IsChecked != selected.Contains(c)) b.IsChecked = selected.Contains(c);
    }
}

internal sealed class CalculatedEditor(DynamicFormView o, DynamicFormModel m, FieldDefinition f, string p, string? g, int? r)
    : FieldEditor(o, m, f, p, g, r)
{
    private Label _value = null!;

    protected override View CreateInput()
    {
        _value = new Label { FontSize = 16, FontFamily = "OpenSansSemibold", MinimumHeightRequest = 32, VerticalTextAlignment = TextAlignment.Center };
        return _value;
    }

    protected override void ShowValue(JsonNode? value)
    {
        var text = Format(value);
        _value.Text = text.Length == 0 ? "—" : Field.Unit is { } u ? $"{text} {u}" : text;
    }

    protected override void OnAbnormal(Color? color) => _value.TextColor = color ?? _value.TextColor;
}

internal sealed class StaticEditor(DynamicFormView o, DynamicFormModel m, FieldDefinition f, string p, string? g, int? r, string? note)
    : FieldEditor(o, m, f, p, g, r)
{
    protected override View CreateInput() => new Label
    {
        Text = note ?? (Field.Help is { } h ? L(h) : L(Field.Label)),
        FontSize = note is null ? 14 : 12,
        TextColor = note is null ? null : Colors.Gray,
    };

    protected override void ShowValue(JsonNode? value) { }
}

/// <summary>Repeating group: a card per row with the child fields, plus add/remove row.</summary>
internal sealed class GroupEditor(DynamicFormView o, DynamicFormModel m, FieldDefinition f, string p)
    : FieldEditor(o, m, f, p, null, null)
{
    private VerticalStackLayout _rows = null!;
    private Button _add = null!;
    private readonly List<FieldEditor> _cells = [];
    private int _renderedRows = -1;
    private bool _enabled = true;

    protected override View CreateInput()
    {
        _rows = new VerticalStackLayout { Spacing = 8 };
        _add = new Button { Text = $"+ Add {L(Field.Label).ToLowerInvariant()} row", FontSize = 13, HorizontalOptions = LayoutOptions.Start };
        _add.Clicked += (_, _) => Model.AddRow(Field.Key);
        return new VerticalStackLayout { Spacing = 8, Children = { _rows, _add } };
    }

    protected override void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _add.IsEnabled = enabled && (Field.MaxRows is not { } max || Model.RowCount(Field.Key) < max);
    }

    protected override void ShowValue(JsonNode? value)
    {
        var count = Model.RowCount(Field.Key);
        if (count != _renderedRows) RebuildRows(count);
        foreach (var cell in _cells) cell.Refresh(!_enabled);
    }

    private void RebuildRows(int count)
    {
        _renderedRows = count;
        _rows.Children.Clear();
        _cells.Clear();
        for (var i = 0; i < count; i++)
        {
            var row = i;
            var grid = new FlexLayout { Wrap = FlexWrap.Wrap };
            foreach (var child in Field.Fields ?? [])
            {
                var cell = Create(Owner, Model, child, $"{Path}[{row}].{child.Key}", Field.Key, row);
                FlexLayout.SetBasis(cell.Container, new FlexBasis(Owner.Columns == 1 ? 1f : 0.5f, isRelative: true));
                _cells.Add(cell);
                grid.Children.Add(cell.Container);
            }
            var remove = new Button
            {
                Text = "Remove row", FontSize = 12, Padding = new Thickness(10, 2), HorizontalOptions = LayoutOptions.Start,
                BackgroundColor = Colors.Transparent, TextColor = Color.FromArgb("#B91C1C"),
            };
            remove.Clicked += (_, _) => Model.RemoveRow(Field.Key, row);
            _rows.Children.Add(new Border
            {
                Padding = new Thickness(12, 8),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                Stroke = Colors.LightGray,
                Content = new VerticalStackLayout
                {
                    Children = { new Label { Text = $"Row {row + 1}", FontSize = 12, TextColor = Colors.Gray }, grid, remove },
                },
            });
        }
    }
}
