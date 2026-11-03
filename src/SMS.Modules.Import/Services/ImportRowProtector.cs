using SMS.Modules.Import.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Import.Services;

/// <summary>
/// A49 D-POST-10: for a profile declaring <see cref="ImportProfileDefinition.SensitiveFields"/>. Whole-row encryption, because which raw column
/// holds a CNIC is known only from the mapping, which can change after the upload. Other profiles' rows pass through untouched.
/// </summary>
internal sealed class ImportRowProtector
{
    private const string Prefix = "enc:";
    public const string Mask = "*****";

    private readonly IEncryptionService? _encryption;
    public ImportRowProtector(IEncryptionService? encryption = null) => _encryption = encryption;

    public static bool IsSensitive(ImportProfileDefinition? def) => def?.SensitiveFields is { Count: > 0 };

    public string Protect(ImportProfileDefinition def, string json) =>
        IsSensitive(def)
            ? Prefix + (_encryption ?? throw new InvalidOperationException("This import profile needs encryption, and this host has none.")).Encrypt(json)
            : json;

    public string Open(string stored) =>
        stored.StartsWith(Prefix, StringComparison.Ordinal)
            ? (_encryption ?? throw new InvalidOperationException("This import row is encrypted, and this host has no encryption.")).Decrypt(stored[Prefix.Length..])
            : stored;

    /// <summary>A value as a read may show it.</summary>
    public static string? MaskValue(ImportProfileDefinition? def, string? field, string? value)
    {
        if (!IsSensitive(def) || string.IsNullOrEmpty(value)) return value;
        return field is not null && !def!.SensitiveFields!.Contains(field, StringComparer.OrdinalIgnoreCase) ? value : Mask;
    }

    /// <summary>What a stored or returned error may carry: never the value of a sensitive field.</summary>
    public static List<ImportFieldError> Sanitize(ImportProfileDefinition def, List<ImportFieldError> errors) =>
        !IsSensitive(def) ? errors
            : errors.Select(e => def.SensitiveFields!.Contains(e.Field, StringComparer.OrdinalIgnoreCase) ? e with { Value = null } : e).ToList();

    /// <summary>A raw row for a report: mapped sensitive columns and every unmapped column masked. Null field = unmapped.</summary>
    public string?[] MaskedRaw(ImportProfileDefinition? def, ImportRow row, IReadOnlyList<ImportColumnMapping> mappings)
    {
        var raw = System.Text.Json.JsonSerializer.Deserialize<string?[]>(Open(row.RawJson), ImportSessionService.Json) ?? [];
        if (!IsSensitive(def)) return raw;
        return raw.Select((v, i) => MaskValue(def, mappings.FirstOrDefault(m => m.SourceColumnIndex == i)?.TargetFieldName, v)).ToArray();
    }
}