using System.Reflection;
using System.Text.Json;
using SMS.Modules.Onboarding.Domain;

namespace SMS.Modules.Onboarding.Services.Defaults;

/// <summary>One embedded pack file: Packs/{CC}_{INDUSTRY}/{CATEGORY}.json (DATA). The content is the payload verbatim.</summary>
internal sealed record PackFile(string CountryCode, string IndustryType, string DataCategory, string Payload)
{
    public string DisplayName(JsonElement payload) =>
        JsonCanonical.TopLevelString(payload, "displayName") ?? $"{CountryCode} {IndustryType} — {DataCategory}";
}

/// <summary>
/// A50 D-4 — the embedded packs and payload schemas (logical names "Onboarding.Packs/&lt;relative path&gt;", see the csproj).
/// Schemas: Packs/schemas/{CATEGORY}.json (or {CATEGORY}.schema.json). Read once per process.
/// </summary>
internal sealed class PackCatalog
{
    private const string Prefix = "Onboarding.Packs/";
    private const string SchemaFolder = "schemas";

    private readonly Lazy<(IReadOnlyList<PackFile> Packs, IReadOnlyDictionary<string, JsonElement> Schemas)> _data;

    public PackCatalog() : this(typeof(PackCatalog).Assembly) { }

    internal PackCatalog(Assembly assembly) => _data = new(() => Load(assembly));

    /// <summary>For tests: a catalog over the given files and schemas.</summary>
    internal PackCatalog(IEnumerable<PackFile> packs, IReadOnlyDictionary<string, string>? schemas = null) =>
        _data = new(() => (packs.ToList(), (schemas ?? new Dictionary<string, string>())
            .ToDictionary(k => k.Key.ToUpperInvariant(), v => JsonDocument.Parse(v.Value).RootElement.Clone(), StringComparer.OrdinalIgnoreCase)));

    public IReadOnlyList<PackFile> Packs => _data.Value.Packs;

    public JsonElement? SchemaFor(string category) =>
        _data.Value.Schemas.TryGetValue(category, out var s) ? s : null;

    public bool HasSchema(string category) => _data.Value.Schemas.ContainsKey(category);

    private static (IReadOnlyList<PackFile>, IReadOnlyDictionary<string, JsonElement>) Load(Assembly assembly)
    {
        var packs = new List<PackFile>();
        var schemas = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            var relative = name[Prefix.Length..].Replace('\\', '/');
            var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !parts[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            var file = parts[1][..^".json".Length];
            if (parts[0].Equals(SchemaFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (file.EndsWith(".schema", StringComparison.OrdinalIgnoreCase)) file = file[..^".schema".Length];
                schemas[file.ToUpperInvariant()] = JsonDocument.Parse(text).RootElement.Clone();
                continue;
            }

            var cut = parts[0].IndexOf('_');
            if (cut <= 0 || cut == parts[0].Length - 1) continue;
            var country  = SetupKey.Country(parts[0][..cut]);
            var industry = SetupKey.Industry(parts[0][(cut + 1)..]);
            packs.Add(new PackFile(country, industry, file.ToUpperInvariant(), text));
        }

        return (packs, schemas);
    }
}

/// <summary>Normalization of the (country, industry, category) key: upper-case country and category, GLOBAL upper-case.</summary>
internal static class SetupKey
{
    public static string Country(string? code) =>
        string.IsNullOrWhiteSpace(code) ? SetupCodes.Global : code.Trim().ToUpperInvariant();

    public static string Industry(string? industry) =>
        string.IsNullOrWhiteSpace(industry) || industry.Trim().Equals(SetupCodes.Global, StringComparison.OrdinalIgnoreCase)
            ? SetupCodes.Global : industry.Trim();

    public static string Category(string? category) => (category ?? string.Empty).Trim().ToUpperInvariant();

    public static string Format(string country, string industry, string category) => $"{country}/{industry}/{category}";
}
