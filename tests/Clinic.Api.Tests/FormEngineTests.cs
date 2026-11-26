using System.Text.Json.Nodes;
using Clinic.Shared.Forms;
using Clinic.Shared.Forms.Expressions;

namespace Clinic.Api.Tests;

public sealed class ExpressionTests
{
    private static object? Eval(string expr, params (string Key, object? Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Key, v => v.Value);
        return ExpressionCompiler.Compile(expr).Evaluate(n => map.GetValueOrDefault(n), new DateTime(2026, 12, 1));
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7d)]
    [InlineData("(1 + 2) * 3", 9d)]
    [InlineData("2 ^ 3 ^ 2", 512d)]
    [InlineData("-2 ^ 2", -4d)]
    [InlineData("10 % 4", 2d)]
    [InlineData("round(10 / 3, 2)", 3.33d)]
    [InlineData("max(3, 9, 4) - min(3, 9, 4)", 6d)]
    [InlineData("if(1 > 2, 10, 20)", 20d)]
    public void Arithmetic(string expr, double expected) => Assert.Equal(expected, (double)Eval(expr)!, 6);

    [Fact]
    public void Bmi_from_the_spec()
    {
        var bmi = Eval("round(weight / (height/100)^2, 1)", ("weight", 70d), ("height", 175d));
        Assert.Equal(22.9, (double)bmi!, 6);
        Assert.Null(Eval("weight / (height/100)^2", ("weight", 70d), ("height", null)));
    }

    [Fact]
    public void Lmp_condition_from_the_spec()
    {
        const string cond = "gender == 'Female' && age >= 12";
        Assert.Equal(true, Eval(cond, ("gender", "female"), ("age", 30d)));
        Assert.Equal(false, Eval(cond, ("gender", "Female"), ("age", 10d)));
        Assert.Equal(false, Eval(cond, ("gender", null), ("age", 30d)));
    }

    [Fact]
    public void Dates_lists_and_nulls()
    {
        var lmp = new DateTime(2026, 9, 1);
        Assert.Equal(new DateTime(2027, 6, 8), Eval("lmp + 280", ("lmp", lmp)));
        Assert.Equal(13d, Eval("floor(daysBetween(lmp, today()) / 7)", ("lmp", lmp)));
        Assert.Equal(35d, Eval("ageYears(dob)", ("dob", new DateTime(1990, 12, 2)))); // birthday is tomorrow
        Assert.Equal(36d, Eval("ageYears(dob)", ("dob", new DateTime(1990, 12, 1))));
        Assert.Equal(true, Eval("contains(allergies, 'penicillin')", ("allergies", new List<string> { "Penicillin" })));
        Assert.Equal(true, Eval("isEmpty(x) and not isEmpty('a')"));
        Assert.Equal(true, Eval("x == null"));
        Assert.Equal(false, Eval("x > 3"));
        Assert.Null(Eval("1 / 0"));
    }

    [Theory]
    [InlineData("a = 1", "==")]
    [InlineData("a & b", "&&")]
    [InlineData("foo(1)", "Unknown function")]
    [InlineData("round()", "argument")]
    [InlineData("(1 + 2", "')'")]
    [InlineData("'abc", "quote")]
    [InlineData("1 +", "ends unexpectedly")]
    public void Syntax_errors_are_explained(string expr, string expected)
    {
        var ex = Assert.Throws<ExpressionException>(() => ExpressionCompiler.Compile(expr));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Identifiers_are_reported_for_dependency_checks()
    {
        var e = ExpressionCompiler.Compile("if(a > b, round(c, 2), d)");
        Assert.Equal(["a", "b", "c", "d"], e.Identifiers.OrderBy(x => x));
    }
}

public sealed class FormEvaluatorTests
{
    private static FormDefinition Vitals() => new()
    {
        EntityType = "Vitals",
        Name = new("Vitals"),
        Sections =
        [
            new()
            {
                Key = "main", Title = new("Vitals"),
                Fields =
                [
                    new() { Key = "weight_kg", Type = FieldType.Decimal, Label = new("Weight"), Min = 0.5m, Max = 400 },
                    new() { Key = "height_cm", Type = FieldType.Decimal, Label = new("Height"), Min = 20, Max = 250 },
                    new() { Key = "bmi", Type = FieldType.Calculated, Label = new("BMI"), Decimals = 1, Formula = "weight_kg / (height_cm/100)^2" },
                    new() { Key = "bmi_band", Type = FieldType.Calculated, Label = new("BMI band"), Formula = "if(bmi >= 30, 'obese', if(bmi >= 25, 'overweight', 'normal'))" },
                    new()
                    {
                        Key = "spo2", Type = FieldType.Integer, Label = new("SpO2"), Required = true, Min = 50, Max = 100,
                        Abnormal = [new() { When = "value < 90", Severity = "critical" }, new() { When = "value < 94", Severity = "warning" }],
                    },
                    new() { Key = "pregnant", Type = FieldType.YesNo, Label = new("Pregnant"), VisibleWhen = "gender == 'F' && ageYears >= 12" },
                    new() { Key = "weeks", Type = FieldType.Integer, Label = new("Weeks"), VisibleWhen = "pregnant == true", RequiredWhen = "pregnant == true" },
                    new()
                    {
                        Key = "psych_note", Type = FieldType.MultilineText, Label = new("Psychiatric note"),
                        Permissions = new() { ViewRoles = ["Doctor"], EditRoles = ["Doctor"] },
                    },
                ],
            },
        ],
    };

    private static EvaluationContext Ctx(string gender = "F", int age = 30, params string[] roles) => new()
    {
        Roles = roles,
        Today = new DateTime(2026, 12, 1),
        Variables = new Dictionary<string, object?> { ["gender"] = gender, ["ageYears"] = (double)age },
    };

    [Fact]
    public void Calculations_run_in_dependency_order_and_round()
    {
        var e = new FormEvaluator(Vitals()).Evaluate(new JsonObject { ["weight_kg"] = 95, ["height_cm"] = 175, ["spo2"] = 98 }, Ctx());
        Assert.Equal(31.0m, FormValues.AsDecimal(e.Values["bmi"]));
        Assert.Equal("obese", FormValues.AsString(e.Values["bmi_band"]));
        Assert.True(e.IsValid);
    }

    [Fact]
    public void Required_range_type_and_abnormal()
    {
        var ev = new FormEvaluator(Vitals());
        var missing = ev.Evaluate(new JsonObject(), Ctx());
        Assert.Contains("SpO2 is required.", missing.State("spo2").Errors);

        var bad = ev.Evaluate(new JsonObject { ["spo2"] = 101, ["weight_kg"] = "heavy" }, Ctx());
        Assert.Contains(bad.State("spo2").Errors, e => e.Contains("at most 100"));
        Assert.Contains(bad.State("weight_kg").Errors, e => e.Contains("must be a number"));

        Assert.Equal(AbnormalSeverity.Critical, ev.Evaluate(new JsonObject { ["spo2"] = 88 }, Ctx()).State("spo2").Abnormal);
        Assert.Equal(AbnormalSeverity.Warning, ev.Evaluate(new JsonObject { ["spo2"] = 92 }, Ctx()).State("spo2").Abnormal);
        Assert.Equal(AbnormalSeverity.None, ev.Evaluate(new JsonObject { ["spo2"] = 97 }, Ctx()).State("spo2").Abnormal);
    }

    [Fact]
    public void Conditions_show_hide_and_require()
    {
        var ev = new FormEvaluator(Vitals());
        Assert.False(ev.Evaluate(new JsonObject { ["spo2"] = 98 }, Ctx("M")).State("pregnant").Visible);

        var preg = ev.Evaluate(new JsonObject { ["spo2"] = 98, ["pregnant"] = true }, Ctx("F"));
        Assert.True(preg.State("weeks").Visible);
        Assert.Contains("Weeks is required.", preg.State("weeks").Errors);

        // Hidden fields are never required.
        var notPreg = ev.Evaluate(new JsonObject { ["spo2"] = 98, ["pregnant"] = false }, Ctx("F"));
        Assert.False(notPreg.State("weeks").Visible);
        Assert.True(notPreg.IsValid);
    }

    [Fact]
    public void Field_permissions_by_role()
    {
        var ev = new FormEvaluator(Vitals());
        Assert.False(ev.Evaluate(new JsonObject(), Ctx(roles: "Nurse / Assistant")).State("psych_note").CanView);
        var doctor = ev.Evaluate(new JsonObject(), Ctx(roles: "Doctor")).State("psych_note");
        Assert.True(doctor.CanView);
        Assert.True(doctor.CanEdit);
    }

    [Fact]
    public void Repeating_group_rows_are_validated_and_calculated_per_row()
    {
        var def = new FormDefinition
        {
            EntityType = "Encounter",
            Name = new("Dental"),
            Sections =
            [
                new()
                {
                    Key = "teeth", Title = new("Teeth"),
                    Fields =
                    [
                        new()
                        {
                            Key = "findings", Type = FieldType.RepeatingGroup, Label = new("Findings"), MinRows = 1,
                            Fields =
                            [
                                new() { Key = "tooth", Type = FieldType.Text, Label = new("Tooth"), Required = true, Pattern = "^[1-8][1-8]$" },
                                new() { Key = "fee", Type = FieldType.Decimal, Label = new("Fee") },
                                new() { Key = "fee_with_tax", Type = FieldType.Calculated, Label = new("With tax"), Formula = "fee * 1.1", Decimals = 2 },
                            ],
                        },
                    ],
                },
            ],
        };
        Assert.Empty(FormDefinitionValidator.Validate(def));

        var values = new JsonObject
        {
            ["findings"] = new JsonArray(
                new JsonObject { ["tooth"] = "36", ["fee"] = 100 },
                new JsonObject { ["tooth"] = "99" }),
        };
        var e = new FormEvaluator(def).Evaluate(values, Ctx());
        Assert.Equal(110.00m, FormValues.AsDecimal(((JsonArray)e.Values["findings"]!)[0]!["fee_with_tax"]));
        Assert.Empty(e.State("findings[0].tooth").Errors);
        Assert.NotEmpty(e.State("findings[1].tooth").Errors);

        var empty = new FormEvaluator(def).Evaluate(new JsonObject(), Ctx());
        Assert.Contains(empty.State("findings").Errors, x => x.Contains("at least 1"));
    }

    [Fact]
    public void Mask_email_phone_and_not_in_future()
    {
        var def = new FormDefinition
        {
            EntityType = "Patient",
            Name = new("Patient"),
            Sections =
            [
                new()
                {
                    Key = "id", Title = new("ID"),
                    Fields =
                    [
                        new() { Key = "cnic", Type = FieldType.IdNumber, Label = new("CNIC"), Mask = "#####-#######-#" },
                        new() { Key = "email", Type = FieldType.Email, Label = new("Email") },
                        new() { Key = "dob", Type = FieldType.Date, Label = new("DOB"), NotInFuture = true },
                        new() { Key = "joined", Type = FieldType.Date, Label = new("Joined"), Default = "=today()" },
                    ],
                },
            ],
        };
        var ev = new FormEvaluator(def);
        var bad = ev.Evaluate(new JsonObject { ["cnic"] = "12345-1234567", ["email"] = "nope", ["dob"] = "2030-01-01" }, Ctx());
        Assert.NotEmpty(bad.State("cnic").Errors);
        Assert.NotEmpty(bad.State("email").Errors);
        Assert.NotEmpty(bad.State("dob").Errors);
        Assert.True(ev.Evaluate(new JsonObject { ["cnic"] = "42101-1234567-1", ["email"] = "a@b.pk", ["dob"] = "1990-05-01" }, Ctx()).IsValid);

        var withDefaults = new JsonObject();
        ev.ApplyDefaults(withDefaults, Ctx());
        Assert.Equal("2026-12-01", FormValues.AsString(withDefaults["joined"]));
    }

    [Fact]
    public void Validator_reports_keys_expressions_options_and_loops()
    {
        var def = new FormDefinition
        {
            EntityType = "Encounter",
            Name = new("Bad"),
            Sections =
            [
                new()
                {
                    Key = "s", Title = new("S"),
                    Fields =
                    [
                        new() { Key = "Bad Key", Type = FieldType.Text, Label = new("x") },
                        new() { Key = "choice", Type = FieldType.SingleSelect, Label = new("Choice") },
                        new() { Key = "shown", Type = FieldType.Text, Label = new("Shown"), VisibleWhen = "missing_field > 1" },
                        new() { Key = "a", Type = FieldType.Calculated, Label = new("A"), Formula = "b + 1" },
                        new() { Key = "b", Type = FieldType.Calculated, Label = new("B"), Formula = "a + 1" },
                        new() { Key = "gender", Type = FieldType.Text, Label = new("Gender") },
                    ],
                },
            ],
        };
        var errors = FormDefinitionValidator.Validate(def).Select(e => e.Message).ToList();
        Assert.Contains(errors, e => e.Contains("lower_snake_case"));
        Assert.Contains(errors, e => e.Contains("needs at least one option"));
        Assert.Contains(errors, e => e.Contains("Unknown name 'missing_field'"));
        Assert.Contains(errors, e => e.Contains("reserved"));

        // Fix everything except the loop: the evaluator catches it.
        def.Sections[0].Fields.RemoveAll(f => f.Key is "Bad Key" or "choice" or "shown" or "gender");
        Assert.Contains(FormDefinitionValidator.Validate(def), e => e.Message.Contains("loop"));
    }

    [Fact]
    public void Compatibility_blocks_type_changes_and_removing_fields_with_data()
    {
        var v1 = Vitals();
        var draft = Vitals();
        draft.Sections[0].Fields.Single(f => f.Key == "spo2").Type = FieldType.Text;
        draft.Sections[0].Fields.RemoveAll(f => f.Key == "pregnant");

        var errors = FormDefinitionValidator.CheckCompatibility([v1], draft, new HashSet<string> { "pregnant" });
        Assert.Contains(errors, e => e.Path == "spo2");
        Assert.Contains(errors, e => e.Path == "pregnant" && e.Message.Contains("retired"));
    }

    [Fact]
    public void Codec_round_trips_every_kind()
    {
        (FieldType, JsonNode)[] cases =
        [
            (FieldType.Text, "hello"),
            (FieldType.Decimal, 12.5m),
            (FieldType.YesNo, true),
            (FieldType.Date, "2026-12-01"),
            (FieldType.MultiSelect, new JsonArray("a", "b")),
        ];
        foreach (var (type, node) in cases)
        {
            var encoded = FieldValueCodec.Encode(type, node);
            var decoded = FieldValueCodec.Decode(encoded);
            Assert.True(JsonNode.DeepEquals(node, decoded), $"{type}: {node.ToJsonString()} → {decoded?.ToJsonString()}");
        }
        Assert.Equal(DeterministicGuid.ForFieldValue(Guid.Empty, "x"), DeterministicGuid.ForFieldValue(Guid.Empty, "x"));
        Assert.NotEqual(DeterministicGuid.ForFieldValue(Guid.Empty, "x"), DeterministicGuid.ForFieldValue(Guid.Empty, "y"));
    }
}
