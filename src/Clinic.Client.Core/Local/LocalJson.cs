using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clinic.Client.Core.Local;

/// <summary>Converts local rows to and from the camelCase JSON used on the sync wire.</summary>
public static class LocalJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonObject ToJson(object entity) =>
        JsonSerializer.SerializeToNode(entity, entity.GetType(), Options)!.AsObject();

    public static LocalEntity ToEntity(JsonObject json, Type type) =>
        (LocalEntity)json.Deserialize(type, Options)!;

    public static JsonObject? Parse(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json)?.AsObject();

    public static List<string> Fields(string changedFieldsJson) =>
        JsonSerializer.Deserialize<List<string>>(changedFieldsJson, Options) ?? [];

    /// <summary>A readable label for the Sync Center: Title, Name or Code, whichever the row has.</summary>
    public static string Describe(LocalEntity entity, string entityType)
    {
        foreach (var name in new[] { "Title", "Name", "Code", "FieldKey" })
        {
            if (entity.GetType().GetProperty(name)?.GetValue(entity) is string { Length: > 0 } value)
                return $"{entityType}: {value}";
        }
        return entityType;
    }
}
