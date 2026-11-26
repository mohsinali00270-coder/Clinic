using System.Text.Json;
using Clinic.Shared.Forms;

namespace Clinic.Application.Forms;

/// <summary>FR-DYN-13 global templates (General OPD, Dental, Gynae, Paediatrics, Physiotherapy, …), embedded JSON.</summary>
public static class FormTemplates
{
    public sealed record Template(string Key, string Name, string EntityType, string Description, FormDefinition Definition);

    private sealed record TemplateFile(string Key, string Name, string EntityType, string Description, FormDefinition Definition);

    private static readonly Lazy<IReadOnlyList<Template>> Loaded = new(Load);

    public static IReadOnlyList<Template> All => Loaded.Value;

    public static Template? Find(string key) => All.FirstOrDefault(t => t.Key == key);

    private static IReadOnlyList<Template> Load()
    {
        var assembly = typeof(FormTemplates).Assembly;
        var list = new List<Template>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Forms.Templates.") && n.EndsWith(".json")).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var file = JsonSerializer.Deserialize<TemplateFile>(stream, FormJson.Options)
                       ?? throw new InvalidOperationException($"Template {name} is empty.");
            list.Add(new Template(file.Key, file.Name, file.EntityType, file.Description, file.Definition));
        }
        return list;
    }
}
