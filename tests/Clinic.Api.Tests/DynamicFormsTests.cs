using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Clinic.Application.Forms;
using Clinic.Client.Core.Forms;
using Clinic.Client.Core.Local;
using Clinic.Shared.Contracts;
using Clinic.Shared.Forms;
using Clinic.Shared.Sync;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Tests;

public sealed class DynamicFormsTests(ClinicApiFactory factory) : IClassFixture<ClinicApiFactory>
{
    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static FieldDefinition F(string key, FieldType type, string label, Action<FieldDefinition>? configure = null)
    {
        var f = new FieldDefinition { Key = key, Type = type, Label = new LocalizedText(label) };
        configure?.Invoke(f);
        return f;
    }

    /// <summary>A handover-note form: shift (required radio), priority, follow-up date shown only when needed, calculated urgency.</summary>
    private static FormDefinition NoteForm(params FieldDefinition[] extra) => new()
    {
        EntityType = SyncEntityNames.BranchNote,
        Name = new LocalizedText("Note details"),
        Sections =
        [
            new SectionDefinition
            {
                Key = "details", Title = new LocalizedText("Details"),
                Fields =
                [
                    F("shift", FieldType.SingleSelect, "Shift", f =>
                    {
                        f.Required = true;
                        f.Options = [new("morning", "Morning"), new("evening", "Evening"), new("night", "Night")];
                    }),
                    F("priority", FieldType.SingleSelect, "Priority", f => f.Options = [new("normal", "Normal"), new("high", "High")]),
                    F("follow_up_needed", FieldType.YesNo, "Follow-up needed"),
                    F("follow_up_by", FieldType.Date, "Follow up by", f =>
                    {
                        f.VisibleWhen = "follow_up_needed == true";
                        f.RequiredWhen = "follow_up_needed == true";
                    }),
                    F("urgent", FieldType.Calculated, "Urgent", f => f.Formula = "priority == 'high' && follow_up_needed == true"),
                    .. extra,
                ],
            },
        ],
    };

    private static async Task<FormDto> CreateAndPublishAsync(HttpClient admin, FormDefinition definition, string name)
    {
        var form = await (await admin.PostAsJsonAsync(FormRoutes.Forms,
            new CreateFormRequest(name, definition.EntityType, Definition: definition))).ReadAsync<FormDto>();
        Assert.Empty(form.Problems);
        await (await admin.PostAsync($"{FormRoutes.Forms}/{form.Id}/publish", null)).EnsureSuccessAsync();
        return form;
    }

    private static async Task<FormVersionDto> RepublishAsync(HttpClient admin, FormDto form, FormDefinition definition)
    {
        await (await admin.PutAsJsonAsync($"{FormRoutes.Forms}/{form.Id}",
            new SaveFormDraftRequest(form.Name, null, null, true, definition))).EnsureSuccessAsync();
        return await (await admin.PostAsync($"{FormRoutes.Forms}/{form.Id}/publish", null)).ReadAsync<FormVersionDto>();
    }

    /// <summary>Deactivates every other BranchNote form so each test controls which form devices resolve.</summary>
    private static async Task IsolateNoteFormsAsync(HttpClient admin)
    {
        foreach (var f in (await admin.GetFromJsonAsync<List<FormSummaryDto>>($"{FormRoutes.Forms}?entityType=BranchNote"))!.Where(f => f.IsActive))
        {
            var full = (await admin.GetFromJsonAsync<FormDto>($"{FormRoutes.Forms}/{f.Id}"))!;
            await (await admin.PutAsJsonAsync($"{FormRoutes.Forms}/{f.Id}",
                new SaveFormDraftRequest(full.Name, full.ContextBranchId, full.ContextSpecialty, false, full.Draft))).EnsureSuccessAsync();
        }
    }

    private static DynamicFormModel Model(FormVersionInfo version, JsonObject? values = null, bool isNew = true, params string[] roles) =>
        new(version, values ?? new JsonObject(), new EvaluationContext { Roles = roles }, isNewRecord: isNew);

    /// <summary>What a screen does on save: note row + its dynamic values in one transaction.</summary>
    private static async Task<LocalBranchNote> SaveNoteAsync(ClientDevice device, LocalBranchNote note, DynamicFormModel model, bool isNew)
    {
        await DynamicDataService.SaveRecordAsync(device.Store, note, isNew, model.Version, model.ValuesToSave(),
            SyncEntityNames.BranchNote, note.BranchId);
        return note;
    }

    // ---------------------------------------------------------------------
    // Phase 3 acceptance
    // ---------------------------------------------------------------------

    /// <summary>
    /// "Admin adds a field; it appears on devices after sync and works offline with validation and conditions."
    /// </summary>
    [Fact]
    public async Task Admin_adds_a_field_and_devices_use_it_offline_with_validation_and_conditions()
    {
        using var admin = await factory.DemoAdminAsync();
        await IsolateNoteFormsAsync(admin);
        var form = await CreateAndPublishAsync(admin, NoteForm(), "Acceptance note form");

        await using var a = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        await using var b = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        await a.Engine.SyncAsync();
        await b.Engine.SyncAsync();

        // ---- Device A, offline: the published form drives validation and conditions.
        a.Transport.Offline = true;
        var forms = new DynamicDataService(a.Db);
        var v1 = await forms.ResolveFormAsync(SyncEntityNames.BranchNote, a.Session.BranchId);
        Assert.NotNull(v1);
        Assert.Equal(1, v1!.VersionNo);

        var model = Model(v1);
        Assert.False(model.Validate());
        Assert.Contains("Shift is required.", model.VisibleErrors("shift"));
        Assert.False(model.State("follow_up_by").Visible);

        model.SetValue("shift", "night");
        model.SetValue("priority", "high");
        model.SetValue("follow_up_needed", true);
        Assert.True(model.State("follow_up_by").Visible);
        Assert.False(model.Validate()); // follow-up date now required
        model.SetValue("follow_up_by", "2026-12-24");
        Assert.True(model.Validate());
        Assert.True(model.GetValue("urgent")!.GetValue<bool>());

        var note = await SaveNoteAsync(a, new LocalBranchNote { BranchId = a.Session.BranchId, Title = "Generator fuel low" }, model, isNew: true);
        Assert.Equal(1, (await a.Center.ListAsync()).Count(e => e.EntityType == SyncEntityNames.BranchNote));
        Assert.True((await a.Center.ListAsync()).Count(e => e.EntityType == SyncEntityNames.FieldValue) >= 4);

        // ---- Reconnect: A pushes, B pulls; values and the snapshot arrive on B.
        a.Transport.Offline = false;
        var pushed = await a.Engine.SyncAsync(manual: true);
        Assert.True(pushed.Outstanding == 0, string.Join(" | ", (await a.Center.ListAsync()).Select(e => $"{e.Summary}: {e.Status} {e.LastError}")) + " / " + pushed.Message);
        await b.Engine.SyncAsync();

        var onB = await new DynamicDataService(b.Db).LoadValuesAsync(SyncEntityNames.BranchNote, note.Id);
        Assert.Equal("night", onB["shift"]!.GetValue<string>());
        Assert.Equal("2026-12-24", onB["follow_up_by"]!.GetValue<string>());
        Assert.True(onB["urgent"]!.GetValue<bool>());
        var noteOnB = await b.Store.GetAsync<LocalBranchNote>(note.Id);
        Assert.Equal(v1.Id, noteOnB!.FormVersionId);
        Assert.Contains("\"shift\":\"night\"", noteOnB.DynamicData);

        // ---- Admin adds a field (v2). Devices get it after sync; the old record keeps v1 (FR-DYN-11/24).
        var v2Def = NoteForm(F("assigned_to", FieldType.Text, "Assigned to", f => f.RequiredWhen = "follow_up_needed == true"));
        var v2 = await RepublishAsync(admin, form, v2Def);
        Assert.Equal(2, v2.VersionNo);

        await b.Engine.SyncAsync();
        b.Transport.Offline = true;
        var bForms = new DynamicDataService(b.Db);
        var latest = await bForms.ResolveFormAsync(SyncEntityNames.BranchNote, b.Session.BranchId);
        Assert.Equal(2, latest!.VersionNo);
        Assert.NotNull(latest.Definition.FindField("assigned_to"));

        var old = await bForms.GetVersionAsync(noteOnB.FormVersionId!.Value);
        Assert.Equal(1, old!.VersionNo);
        Assert.False(old.IsCurrent);
        Assert.Null(old.Definition.FindField("assigned_to"));

        var newModel = Model(latest);
        newModel.SetValue("shift", "morning");
        newModel.SetValue("follow_up_needed", true);
        newModel.SetValue("follow_up_by", "2026-12-20");
        Assert.False(newModel.Validate());
        Assert.Contains("Assigned to is required.", newModel.VisibleErrors("assigned_to"));
    }

    [Fact]
    public async Task Two_devices_editing_dynamic_fields_of_one_record_offline_merge()
    {
        using var admin = await factory.DemoAdminAsync();
        await IsolateNoteFormsAsync(admin);
        await CreateAndPublishAsync(admin, NoteForm(F("assigned_to", FieldType.Text, "Assigned to")), "Merge note form");

        await using var a = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        await using var b = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        await a.Engine.SyncAsync();
        var version = (await new DynamicDataService(a.Db).ResolveFormAsync(SyncEntityNames.BranchNote, a.Session.BranchId))!;

        var m = Model(version);
        m.SetValue("shift", "morning");
        var note = await SaveNoteAsync(a, new LocalBranchNote { BranchId = a.Session.BranchId, Title = "Shared" }, m, isNew: true);
        await a.Engine.SyncAsync();
        await b.Engine.SyncAsync();

        // Both offline: A sets priority, B sets assigned_to (different fields) and both set shift (same field).
        a.Transport.Offline = b.Transport.Offline = true;
        foreach (var (device, priority, assigned, shift) in new[] { (a, "high", (string?)null, "evening"), (b, (string?)null, "Ali", "night") })
        {
            var svc = new DynamicDataService(device.Db);
            var local = (await device.Store.GetAsync<LocalBranchNote>(note.Id))!;
            var model = Model(version, await svc.LoadValuesAsync(SyncEntityNames.BranchNote, note.Id), isNew: false);
            if (priority is not null) model.SetValue("priority", priority);
            if (assigned is not null) model.SetValue("assigned_to", assigned);
            model.SetValue("shift", shift);
            await SaveNoteAsync(device, local, model, isNew: false);
        }

        a.Transport.Offline = b.Transport.Offline = false;
        await a.Engine.SyncAsync(manual: true);
        var bState = await b.Engine.SyncAsync(manual: true);
        Assert.Equal(0, bState.Outstanding); // nothing rejected
        await a.Engine.SyncAsync();

        foreach (var d in new[] { a, b })
        {
            var values = await new DynamicDataService(d.Db).LoadValuesAsync(SyncEntityNames.BranchNote, note.Id);
            Assert.Equal("high", values["priority"]!.GetValue<string>());
            Assert.Equal("Ali", values["assigned_to"]!.GetValue<string>());
            Assert.Equal("night", values["shift"]!.GetValue<string>()); // same field: last writer wins
        }
    }

    [Fact]
    public async Task Server_revalidates_values_and_recomputes_calculations()
    {
        using var admin = await factory.DemoAdminAsync();
        await IsolateNoteFormsAsync(admin);
        await CreateAndPublishAsync(admin, NoteForm(
            F("count", FieldType.Integer, "Count", f => { f.Min = 0; f.Max = 10; }),
            F("city", FieldType.Lookup, "City", f => f.LookupList = "cities"),
            F("ref_no", FieldType.Text, "Reference", f => f.Unique = true)), "Server checks form");

        var session = await factory.DemoAdminDeviceAsync();
        await using var a = await ClientDevice.CreateAsync(session);
        await a.Engine.SyncAsync();
        var version = (await new DynamicDataService(a.Db).ResolveFormAsync(SyncEntityNames.BranchNote, a.Session.BranchId))!;

        var m = Model(version);
        m.SetValue("shift", "morning");
        m.SetValue("ref_no", "REF-" + Guid.NewGuid().ToString("N")[..6]);
        var note = await SaveNoteAsync(a, new LocalBranchNote { BranchId = a.Session.BranchId, Title = "Checks" }, m, isNew: true);
        await a.Engine.SyncAsync();

        SyncChange Value(string key, JsonObject data) => new(Guid.NewGuid(), SyncEntityNames.FieldValue,
            DeterministicGuid.ForFieldValue(note.Id, key), SyncOperation.Insert, null, data.Select(kv => kv.Key).ToList(), data, DateTimeOffset.UtcNow);
        JsonObject Data(string key, string kind, JsonNode? text = null, decimal? number = null) => new()
        {
            ["recordId"] = note.Id, ["entityType"] = SyncEntityNames.BranchNote, ["fieldKey"] = key,
            ["formVersionId"] = version.Id, ["valueKind"] = kind, ["valueText"] = text, ["valueNumber"] = number,
        };

        async Task<SyncChangeResult> Push(SyncChange c) =>
            (await session.Transport.PushAsync(new SyncPushRequest(session.DeviceId, "1.0.0", [c]), default)).Results.Single();

        Assert.Contains("at most 10", (await Push(Value("count", Data("count", ValueKinds.Number, number: 99)))).Error);
        Assert.Contains("not one of the options", (await Push(Value("shift", Data("shift", ValueKinds.Text, "lunch")))).Error);
        Assert.Contains("not in the 'cities' list", (await Push(Value("city", Data("city", ValueKinds.Text, "XYZ")))).Error);
        Assert.Contains("not part of form", (await Push(Value("nope", Data("nope", ValueKinds.Text, "x")))).Error);
        Assert.Equal(SyncChangeStatus.Applied, (await Push(Value("city", Data("city", ValueKinds.Text, "KHI")))).Status);

        // Device lies about a calculated field: the server recomputes and devices get the server's value.
        await Push(Value("priority", Data("priority", ValueKinds.Text, "high")));
        await Push(Value("follow_up_needed", new JsonObject
        {
            ["recordId"] = note.Id, ["entityType"] = SyncEntityNames.BranchNote, ["fieldKey"] = "follow_up_needed",
            ["formVersionId"] = version.Id, ["valueKind"] = ValueKinds.Bool, ["valueBool"] = true,
        }));
        await Push(Value("urgent", new JsonObject
        {
            ["recordId"] = note.Id, ["entityType"] = SyncEntityNames.BranchNote, ["fieldKey"] = "urgent",
            ["formVersionId"] = version.Id, ["valueKind"] = ValueKinds.Bool, ["valueBool"] = false,
        }));
        await a.Engine.SyncAsync();
        var values = await new DynamicDataService(a.Db).LoadValuesAsync(SyncEntityNames.BranchNote, note.Id);
        Assert.True(values["urgent"]!.GetValue<bool>());

        // Unique within the tenant.
        var other = Model(version);
        other.SetValue("shift", "night");
        var second = await SaveNoteAsync(a, new LocalBranchNote { BranchId = a.Session.BranchId, Title = "Dup ref" }, other, isNew: true);
        await a.Engine.SyncAsync();
        var dup = await Push(new SyncChange(Guid.NewGuid(), SyncEntityNames.FieldValue, DeterministicGuid.ForFieldValue(second.Id, "ref_no"),
            SyncOperation.Insert, null, ["recordId", "entityType", "fieldKey", "formVersionId", "valueKind", "valueText"],
            new JsonObject
            {
                ["recordId"] = second.Id, ["entityType"] = SyncEntityNames.BranchNote, ["fieldKey"] = "ref_no",
                ["formVersionId"] = version.Id, ["valueKind"] = ValueKinds.Text, ["valueText"] = FormValues.AsString(m.GetValue("ref_no")),
            }, DateTimeOffset.UtcNow));
        Assert.Contains("already used", dup.Error);
    }

    [Fact]
    public async Task Field_permissions_limit_who_edits_and_who_receives_a_value()
    {
        using var admin = await factory.DemoAdminAsync();
        await IsolateNoteFormsAsync(admin);
        await CreateAndPublishAsync(admin, NoteForm(F("clinical_note", FieldType.MultilineText, "Clinical note", f =>
            f.Permissions = new FieldPermissions { ViewRoles = ["Doctor"], EditRoles = ["Doctor"] })), "Restricted field form");

        var doctorName = await factory.CreateUserAsync("Doctor");
        await using var doctor = await ClientDevice.CreateAsync(await factory.DeviceAsync("demo", doctorName, SyncTestExtensions.TestPassword));
        await using var adminDevice = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        await doctor.Engine.SyncAsync();
        await adminDevice.Engine.SyncAsync();

        var version = (await new DynamicDataService(doctor.Db).ResolveFormAsync(SyncEntityNames.BranchNote, doctor.Session.BranchId))!;
        Assert.True(Model(version, roles: "Doctor").State("clinical_note").CanEdit);
        Assert.False(Model(version, roles: "Tenant Admin").State("clinical_note").CanView);

        var m = Model(version, roles: "Doctor");
        m.SetValue("shift", "morning");
        m.SetValue("clinical_note", "Seen by doctor; refer to cardiology.");
        var note = await SaveNoteAsync(doctor, new LocalBranchNote { BranchId = doctor.Session.BranchId, Title = "Doctor note" }, m, isNew: true);
        Assert.Equal(0, (await doctor.Engine.SyncAsync()).Outstanding);

        await adminDevice.Engine.SyncAsync();
        var adminValues = await new DynamicDataService(adminDevice.Db).LoadValuesAsync(SyncEntityNames.BranchNote, note.Id);
        Assert.Equal("morning", adminValues["shift"]!.GetValue<string>());
        Assert.False(adminValues.ContainsKey("clinical_note")); // never reached the admin's device

        // The admin's role may not edit it either.
        var adminModel = Model(version, adminValues, isNew: false, "Tenant Admin");
        adminModel.SetValue("clinical_note", "overwritten"); // a tampered device: the UI would not offer this field
        var local = (await adminDevice.Store.GetAsync<LocalBranchNote>(note.Id))!;
        await SaveNoteAsync(adminDevice, local, adminModel, isNew: false);
        var state = await adminDevice.Engine.SyncAsync();
        Assert.Equal(1, state.Rejected);
        Assert.Contains("may not edit", (await adminDevice.Center.ListAsync()).Single(e => e.Status == OutboxStatus.Rejected).LastError);
    }

    // ---------------------------------------------------------------------
    // Form builder API
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Drafts_save_with_problems_but_publish_requires_a_valid_compatible_definition()
    {
        using var admin = await factory.DemoAdminAsync();
        var broken = NoteForm(F("bad", FieldType.Calculated, "Bad", f => f.Formula = "missing + 1"));
        var form = await (await admin.PostAsJsonAsync(FormRoutes.Forms,
            new CreateFormRequest("Broken form", "BranchNote", Definition: broken))).ReadAsync<FormDto>();
        Assert.Contains(form.Problems, p => p.Message.Contains("Unknown name 'missing'"));

        var publish = await admin.PostAsync($"{FormRoutes.Forms}/{form.Id}/publish", null);
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
        var problem = await publish.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains(problem!.Errors.Keys, k => k.Contains("formula"));

        // Fix and publish v1.
        await (await admin.PutAsJsonAsync($"{FormRoutes.Forms}/{form.Id}", new SaveFormDraftRequest("Broken form", null, null, false, NoteForm()))).EnsureSuccessAsync();
        await (await admin.PostAsync($"{FormRoutes.Forms}/{form.Id}/publish", null)).EnsureSuccessAsync();

        // A key may not change to an incompatible type.
        var retyped = NoteForm();
        retyped.Sections[0].Fields.Single(f => f.Key == "priority").Type = FieldType.Integer;
        var result = await (await admin.PostAsJsonAsync($"{FormRoutes.Forms}/validate", new ValidateFormRequest(retyped, form.Id))).ReadAsync<FormValidationResult>();
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "priority");

        var versions = await admin.GetFromJsonAsync<List<FormVersionDto>>($"{FormRoutes.Forms}/{form.Id}/versions");
        Assert.Single(versions!);
        Assert.False((await admin.GetFromJsonAsync<FormDto>($"{FormRoutes.Forms}/{form.Id}"))!.HasUnpublishedChanges);
    }

    [Fact]
    public async Task Every_global_template_is_valid_and_publishable()
    {
        Assert.True(FormTemplates.All.Count >= 8);
        using var admin = await factory.DemoAdminAsync();
        var templates = await admin.GetFromJsonAsync<List<FormTemplateDto>>(FormRoutes.FormTemplates);
        Assert.Contains(templates!, t => t.Key == "dental");

        foreach (var t in templates!)
        {
            var form = await (await admin.PostAsJsonAsync(FormRoutes.Forms,
                new CreateFormRequest($"T {t.Name}", t.EntityType, TemplateKey: t.Key))).ReadAsync<FormDto>();
            Assert.True(form.Problems.Count == 0, $"{t.Key}: {string.Join("; ", form.Problems.Select(p => $"{p.Path} {p.Message}"))}");
            var version = await (await admin.PostAsync($"{FormRoutes.Forms}/{form.Id}/publish", null)).ReadAsync<FormVersionDto>();
            Assert.Equal(1, version.VersionNo);

            // Deactivate again so these forms do not interfere with other tests.
            await (await admin.PutAsJsonAsync($"{FormRoutes.Forms}/{form.Id}",
                new SaveFormDraftRequest(form.Name, null, null, false, form.Draft))).EnsureSuccessAsync();
        }
    }

    [Fact]
    public async Task Removing_a_field_that_holds_data_is_blocked_until_it_is_retired()
    {
        using var admin = await factory.DemoAdminAsync();
        await IsolateNoteFormsAsync(admin);
        var form = await CreateAndPublishAsync(admin, NoteForm(F("assigned_to", FieldType.Text, "Assigned to")), "Retire test");

        await using var a = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        await a.Engine.SyncAsync();
        var version = (await new DynamicDataService(a.Db).ResolveFormAsync(SyncEntityNames.BranchNote, a.Session.BranchId))!;
        var m = Model(version);
        m.SetValue("shift", "morning");
        m.SetValue("assigned_to", "Sara");
        await SaveNoteAsync(a, new LocalBranchNote { BranchId = a.Session.BranchId, Title = "Has data" }, m, isNew: true);
        await a.Engine.SyncAsync();

        var removed = NoteForm();
        await (await admin.PutAsJsonAsync($"{FormRoutes.Forms}/{form.Id}", new SaveFormDraftRequest(form.Name, null, null, true, removed))).EnsureSuccessAsync();
        var blocked = await admin.PostAsync($"{FormRoutes.Forms}/{form.Id}/publish", null);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("retired", await blocked.Content.ReadAsStringAsync());

        var retired = NoteForm(F("assigned_to", FieldType.Text, "Assigned to", f => f.Retired = true));
        var v2 = await RepublishAsync(admin, form, retired);
        Assert.Equal(2, v2.VersionNo);

        // Retired: hidden on new records, still shown where it has a value.
        await a.Engine.SyncAsync();
        var latest = (await new DynamicDataService(a.Db).ResolveFormAsync(SyncEntityNames.BranchNote, a.Session.BranchId))!;
        Assert.False(Model(latest).State("assigned_to").Visible);
        Assert.True(Model(latest, new JsonObject { ["assigned_to"] = "Sara" }, isNew: false).State("assigned_to").Visible);
    }

    [Fact]
    public async Task Lookups_are_seeded_editable_importable_and_cascade_on_devices()
    {
        using var admin = await factory.DemoAdminAsync();
        var lists = await admin.GetFromJsonAsync<List<LookupListDto>>(FormRoutes.Lookups);
        foreach (var key in new[] { "gender", "blood_group", "cities", "areas", "specialties" })
            Assert.Contains(lists!, l => l.Key == key && l.IsSystem);
        var cities = lists!.Single(l => l.Key == "cities");
        var areas = lists!.Single(l => l.Key == "areas");
        Assert.Equal("cities", areas.ParentListKey);

        var key2 = "wards_" + Guid.NewGuid().ToString("N")[..6];
        var wards = await (await admin.PostAsJsonAsync(FormRoutes.Lookups, new SaveLookupListRequest(key2, "Wards", null))).ReadAsync<LookupListDto>();
        var import = await (await admin.PostAsJsonAsync($"{FormRoutes.Lookups}/{wards.Id}/import", new ImportLookupItemsRequest(
            "code,labelEn,labelUr,sortOrder\nW1,\"General ward, ground floor\",جنرل وارڈ,1\nW2,Maternity,,2\n,,,\nW3,,,3\n"))).ReadAsync<ImportLookupItemsResult>();
        Assert.Equal(2, import.Added);
        Assert.Single(import.Errors);
        var detail = await admin.GetFromJsonAsync<LookupListDetailDto>($"{FormRoutes.Lookups}/{wards.Id}");
        Assert.Equal("General ward, ground floor", detail!.Items.Single(i => i.Code == "W1").LabelEn);
        Assert.Equal("جنرل وارڈ", detail.Items.Single(i => i.Code == "W1").LabelUr);

        var renamed = await admin.PutAsJsonAsync($"{FormRoutes.Lookups}/{wards.Id}", new SaveLookupListRequest("other_key", "Wards", null));
        Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
        var badParent = await admin.PostAsJsonAsync($"{FormRoutes.Lookups}/{areas.Id}/items", new SaveLookupItemRequest("x_area", "X", null, "NOPE"));
        Assert.Equal(HttpStatusCode.BadRequest, badParent.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{FormRoutes.Lookups}/{cities.Id}")).StatusCode);

        // Cascading options on a device, offline.
        await IsolateNoteFormsAsync(admin);
        await CreateAndPublishAsync(admin, NoteForm(
            F("city", FieldType.Lookup, "City", f => f.LookupList = "cities"),
            F("area", FieldType.Lookup, "Area", f => { f.LookupList = "areas"; f.LookupParentField = "city"; })), "Cascade form");

        await using var a = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        await a.Engine.SyncAsync();
        a.Transport.Offline = true;
        var svc = new DynamicDataService(a.Db);
        var version = (await svc.ResolveFormAsync(SyncEntityNames.BranchNote, a.Session.BranchId))!;
        var model = new DynamicFormModel(version, new JsonObject(), new EvaluationContext(), await svc.LoadLookupsAsync(version.Definition));

        var areaField = version.Definition.FindField("area")!;
        Assert.Empty(model.OptionsFor(areaField));
        model.SetValue("city", "LHE");
        Assert.Contains(model.OptionsFor(areaField), o => o.Code == "lhe_gulberg");
        Assert.DoesNotContain(model.OptionsFor(areaField), o => o.Code == "khi_clifton");
        model.SetValue("area", "lhe_gulberg");
        model.SetValue("city", "KHI"); // changing the parent clears the child
        Assert.Null(model.GetValue("area"));
        Assert.Contains(model.OptionsFor(version.Definition.FindField("city")!), o => o.Label.Ur == "کراچی");
    }

    [Fact]
    public async Task Form_drafts_autosave_locally()
    {
        await using var a = await ClientDevice.CreateAsync(await factory.DemoAdminDeviceAsync());
        var svc = new DynamicDataService(a.Db);
        var key = DynamicDataService.DraftKey("BranchNote", Guid.NewGuid());
        Assert.Null(await svc.LoadDraftAsync(key));
        await svc.SaveDraftAsync(key, new JsonObject { ["shift"] = "night" });
        Assert.Equal("night", (await svc.LoadDraftAsync(key))!["shift"]!.GetValue<string>());
        await svc.DeleteDraftAsync(key);
        Assert.Null(await svc.LoadDraftAsync(key));
        Assert.Equal(0, await a.Center.CountAsync()); // drafts never sync
    }
}
