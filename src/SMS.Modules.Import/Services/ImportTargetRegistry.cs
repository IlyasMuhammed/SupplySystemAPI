using SMS.Modules.Import.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Import.Services;

/// <summary>A registered target with its definition and the field list the importer works with (external_id first).</summary>
internal sealed record ImportTargetEntry(IImportTarget Target, ImportProfileDefinition Definition, IReadOnlyList<ImportFieldDefinition> Fields)
{
    public ImportFieldDefinition? Field(string name) =>
        Fields.FirstOrDefault(f => f.FieldName.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A40 D-3 — every <see cref="IImportTarget"/> registered in DI (one per entity type, owned by its module). The targets'
/// Describe() is the source of truth for profiles and fields; <see cref="ImportProfileService.ReconcileAsync"/> mirrors it
/// into import.profiles at startup.
/// </summary>
internal sealed class ImportTargetRegistry
{
    /// <summary>The importer's own field on every profile.</summary>
    internal static readonly ImportFieldDefinition ExternalIdField = new(
        ImportLimits.ExternalIdField, "External ID",
        "Optional: the record's id in your previous system. Re-importing a row with the same External ID updates the record it created.",
        ImportFieldTypes.Text, IsRequired: false, IsKey: false, SortOrder: 0, SampleValue: "LEGACY-0001", MaxLength: 100,
        Aliases: ["ext_id", "legacy_id", "source_id"]);

    private readonly Dictionary<string, ImportTargetEntry> _byType;

    public ImportTargetRegistry(IEnumerable<IImportTarget> targets)
    {
        _byType = new Dictionary<string, ImportTargetEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            var def = target.Describe();
            if (_byType.ContainsKey(def.EntityType))
                throw new InvalidOperationException($"Two import targets describe entity type {def.EntityType}.");
            var fields = def.Fields.Any(f => f.FieldName.Equals(ImportLimits.ExternalIdField, StringComparison.OrdinalIgnoreCase))
                ? def.Fields.OrderBy(f => f.SortOrder).ToList()
                : new[] { ExternalIdField }.Concat(def.Fields.OrderBy(f => f.SortOrder)).ToList();
            _byType[def.EntityType] = new ImportTargetEntry(target, def, fields);
        }
    }

    public IReadOnlyList<ImportTargetEntry> All =>
        _byType.Values.OrderBy(e => e.Definition.ImportOrder).ThenBy(e => e.Definition.EntityType).ToList();

    public ImportTargetEntry Get(string? entityType) =>
        entityType is not null && _byType.TryGetValue(entityType.Trim(), out var entry)
            ? entry
            : throw new NotFoundException(ImportMessages.UnknownEntity(entityType ?? string.Empty));

    public bool TryGet(string entityType, out ImportTargetEntry entry) => _byType.TryGetValue(entityType, out entry!);
}
