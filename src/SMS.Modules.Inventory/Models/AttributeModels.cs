namespace SMS.Modules.Inventory.Models;

// ── Attribute Definitions (FSD Addendum 26 §4.1) ────────────────────────────────

public class AttributeDefinitionModel
{
    public Guid Uuid { get; set; }
    public string AttributeName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string ControlType { get; set; } = string.Empty;
    public List<string>? DropdownOptions { get; set; }
    public string? DefaultValue { get; set; }
    public string? ValidationRegex { get; set; }
    public bool IsRequired { get; set; }
    public bool IsSearchable { get; set; }
    public bool IsFilterable { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class CreateAttributeDefinitionRequest
{
    public string AttributeName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string ControlType { get; set; } = string.Empty;
    public List<string>? DropdownOptions { get; set; }
    public string? DefaultValue { get; set; }
    public string? ValidationRegex { get; set; }
    public bool IsRequired { get; set; }
    public bool IsSearchable { get; set; }
    public bool IsFilterable { get; set; }
    public int SortOrder { get; set; }
}

public class UpdateAttributeDefinitionRequest
{
    public string? DisplayName { get; set; }
    public string? ControlType { get; set; }
    public List<string>? DropdownOptions { get; set; }
    public string? DefaultValue { get; set; }
    public string? ValidationRegex { get; set; }
    public bool? IsRequired { get; set; }
    public bool? IsSearchable { get; set; }
    public bool? IsFilterable { get; set; }
    public int? SortOrder { get; set; }
    public bool? IsActive { get; set; }
}

// Mirrors CategoryDeleteResult's hard-delete-else-report-references shape (ProductModels.cs) —
// same UX contract: try a real delete, and if it's in use, tell the caller exactly what's
// blocking it instead of a generic error.
public class AttributeDeleteResult
{
    public bool Deleted { get; set; }
    public int ReferencedCategoryCount { get; set; }
    public int ReferencedVariantValueCount { get; set; }
}

// ── Category Attributes (FSD Addendum 26 §4.2) ──────────────────────────────────

public class CategoryAttributeModel
{
    public int CategoryId { get; set; }
    public Guid AttributeUuid { get; set; }
    public string AttributeName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string ControlType { get; set; } = string.Empty;
    public List<string>? DropdownOptions { get; set; }
    public string? ValidationRegex { get; set; }
    public bool IsRequired { get; set; }
    public bool IsSearchable { get; set; }
    public int DisplayOrder { get; set; }
}

public class CreateCategoryAttributeRequest
{
    public Guid AttributeUuid { get; set; }
    // Null = inherit AttributeDefinition.IsRequired; set to override for this category.
    public bool? IsRequired { get; set; }
    public int DisplayOrder { get; set; }
}

// Full-replace payload for the Configure Attributes admin screen's Save button — the whole
// panel's state (adds, removes, reorders, required-toggles) in one call (FSD §6).
public class SetCategoryAttributesRequest
{
    public List<CategoryAttributeOrderItem> Attributes { get; set; } = [];
}

public class CategoryAttributeOrderItem
{
    public Guid AttributeUuid { get; set; }
    public bool IsRequired { get; set; }
    public int DisplayOrder { get; set; }
}

// ── Variant Attribute Values (FSD Addendum 26 §4.3) ─────────────────────────────

public class VariantAttributeValueModel
{
    public Guid AttributeUuid { get; set; }
    public string AttributeName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class VariantAttributeValueInput
{
    public Guid AttributeUuid { get; set; }
    public string Value { get; set; } = string.Empty;
}

public class SetVariantAttributeValuesRequest
{
    public List<VariantAttributeValueInput> Values { get; set; } = [];
}
