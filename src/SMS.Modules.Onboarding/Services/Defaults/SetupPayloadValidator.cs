using System.Text.Json;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Services.Defaults;

/// <summary>
/// A50 D-4 / task 031 — a category payload is valid when it is a JSON object that passes the category's pack schema
/// (Packs/schemas, DATA) and the registered seeder's <see cref="ISetupCategorySeeder.ValidatePayload"/>. Used by the
/// Super Admin save / import and by the wizard for the admin's edited payloads.
/// </summary>
internal sealed class SetupPayloadValidator
{
    private readonly PackCatalog _catalog;
    private readonly IReadOnlyDictionary<string, ISetupCategorySeeder> _seeders;

    public SetupPayloadValidator(PackCatalog catalog, IEnumerable<ISetupCategorySeeder> seeders)
    {
        _catalog = catalog;
        _seeders = SeederMap(seeders);
    }

    /// <summary>Last registration wins per category (a test can replace a module's seeder).</summary>
    internal static IReadOnlyDictionary<string, ISetupCategorySeeder> SeederMap(IEnumerable<ISetupCategorySeeder> seeders)
    {
        var map = new Dictionary<string, ISetupCategorySeeder>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in seeders) map[s.Category] = s;
        return map;
    }

    public IReadOnlyDictionary<string, ISetupCategorySeeder> Seeders => _seeders;

    /// <summary>The apply order (D-10) plus any extra category a module registers a seeder for (HRM after A49).</summary>
    public IReadOnlyList<string> KnownCategories =>
        SetupCategories.ApplyOrder.Concat(_seeders.Keys.Where(k => !SetupCategories.ApplyOrder.Contains(k, StringComparer.OrdinalIgnoreCase))
            .Select(k => k.ToUpperInvariant()).OrderBy(k => k)).ToList();

    public bool IsKnownCategory(string category) => KnownCategories.Contains(category, StringComparer.OrdinalIgnoreCase);

    public bool HasSchema(string category) => _catalog.HasSchema(category);

    public List<string> Validate(string category, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return ["The payload must be a JSON object."];

        var errors = new List<string>();
        if (_catalog.SchemaFor(category) is { } schema)
            errors.AddRange(JsonSchemaLite.Validate(schema, payload));
        if (_seeders.TryGetValue(category, out var seeder))
        {
            try { errors.AddRange(seeder.ValidatePayload(payload)); }
            catch (Exception ex) { errors.Add($"The {category} payload could not be read: {ex.Message}"); }
        }
        return errors.Distinct().ToList();
    }

    public List<string> Validate(string category, string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return Validate(category, doc.RootElement);
        }
        catch (JsonException ex)
        {
            return [$"The payload is not valid JSON: {ex.Message}"];
        }
    }
}
