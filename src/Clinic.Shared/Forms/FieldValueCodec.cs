using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Clinic.Shared.Forms;

/// <summary>Which typed column of a FieldValue row holds the value (FR-DYN-30).</summary>
public static class ValueKinds
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Bool = "bool";
    public const string Date = "date";
    public const string DateTime = "datetime";
    public const string Json = "json";
}

/// <summary>One dynamic value spread over typed columns, so search and reports can index it.</summary>
public sealed record TypedValue(string Kind, string? Text, decimal? Number, DateTime? Date, bool? Bool, string? Json)
{
    public static readonly TypedValue Empty = new(ValueKinds.Text, null, null, null, null, null);
    public bool IsEmpty => Text is null && Number is null && Date is null && Bool is null && Json is null;
}

/// <summary>
/// Converts between the JSON values the form engine works with and FieldValue's typed columns.
/// Used identically on device and server.
/// </summary>
public static class FieldValueCodec
{
    public static TypedValue Encode(FieldType type, JsonNode? node)
    {
        if (node is null) return TypedValue.Empty;

        switch (type)
        {
            case FieldType.MultiSelect:
            case FieldType.RepeatingGroup:
                return node is JsonArray { Count: 0 } ? TypedValue.Empty
                    : new(ValueKinds.Json, null, null, null, null, node.ToJsonString());

            case FieldType.Integer:
            case FieldType.Decimal:
                return FormValues.AsDecimal(node) is { } n
                    ? new(ValueKinds.Number, null, n, null, null, null)
                    : Text(node); // keep what the user typed; validation reports it

            case FieldType.YesNo:
                return FormValues.AsBool(node) is { } b ? new(ValueKinds.Bool, null, null, null, b, null) : TypedValue.Empty;

            case FieldType.Date:
                return FormValues.AsDate(node) is { } d
                    ? new(ValueKinds.Date, d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, d.Date, null, null)
                    : Text(node);

            case FieldType.DateTime:
                return FormValues.AsDate(node) is { } dt
                    ? new(ValueKinds.DateTime, dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), null, dt, null, null)
                    : Text(node);

            case FieldType.Calculated:
                if (node is JsonValue v)
                {
                    if (v.TryGetValue<bool>(out var cb)) return new(ValueKinds.Bool, null, null, null, cb, null);
                    if (FormValues.AsDecimal(node) is { } cn && !v.TryGetValue<string>(out _)) return new(ValueKinds.Number, null, cn, null, null, null);
                }
                return Text(node);

            default:
                return Text(node);
        }
    }

    private static TypedValue Text(JsonNode node)
    {
        var s = node is JsonValue v && v.TryGetValue<string>(out var str) ? str : node.ToJsonString();
        return string.IsNullOrEmpty(s) ? TypedValue.Empty : new(ValueKinds.Text, s, null, null, null, null);
    }

    public static JsonNode? Decode(string? kind, string? text, decimal? number, DateTime? date, bool? @bool, string? json) => kind switch
    {
        ValueKinds.Number => number is { } n ? JsonValue.Create(n) : null,
        ValueKinds.Bool => @bool is { } b ? JsonValue.Create(b) : null,
        ValueKinds.Date => text ?? date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ValueKinds.DateTime => text ?? date?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        ValueKinds.Json => json is null ? null : JsonNode.Parse(json),
        _ => text is null ? null : JsonValue.Create(text),
    };

    public static JsonNode? Decode(TypedValue v) => Decode(v.Kind, v.Text, v.Number, v.Date, v.Bool, v.Json);
}

/// <summary>Reading form values from JSON nodes, tolerant of how they were typed.</summary>
public static class FormValues
{
    public static string? AsString(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => node.ToJsonString(),
    };

    public static decimal? AsDecimal(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var m)) return m;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return (decimal)d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) &&
            decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public static bool? AsBool(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s)) return s.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => null,
        };
        return null;
    }

    public static DateTime? AsDate(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<DateTime>(out var dt)) return dt;
        if (v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) &&
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p)) return p;
        return null;
    }

    public static List<string> AsStringList(JsonNode? node) => node switch
    {
        JsonArray a => a.Select(AsString).OfType<string>().ToList(),
        JsonValue v when AsString(v) is { Length: > 0 } s => [s],
        _ => [],
    };

    public static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s),
        _ => false,
    };
}

/// <summary>RFC 4122 version-5 (SHA-1, name-based) GUIDs.</summary>
public static class DeterministicGuid
{
    public static Guid Create(Guid @namespace, string name)
    {
        Span<byte> ns = stackalloc byte[16];
        @namespace.TryWriteBytes(ns, bigEndian: true, out _);
        var nameBytes = Encoding.UTF8.GetBytes(name);

        var input = new byte[16 + nameBytes.Length];
        ns.CopyTo(input);
        nameBytes.CopyTo(input, 16);
        var hash = SHA1.HashData(input);

        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>
    /// FieldValue id for (record, field). Two devices that set the same field of the same record offline
    /// create the same id, so the server merges them as one value instead of two rows.
    /// </summary>
    public static Guid ForFieldValue(Guid recordId, string fieldKey) => Create(recordId, "field:" + fieldKey);
}
