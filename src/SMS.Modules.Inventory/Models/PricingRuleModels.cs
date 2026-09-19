namespace SMS.Modules.Inventory.Models;

// A29-P2-04 §2.2/§2.4 — Pricing Rules CRUD + resolve preview.

public class CreatePricingRuleRequest
{
    public Guid     VariantUuid   { get; set; }
    // Null = applies to every partner (an org-wide list price).
    public Guid?    PartnerUuid   { get; set; }
    public string   PriceType     { get; set; } = string.Empty;
    public decimal? MinQty        { get; set; }
    public decimal? MaxQty        { get; set; }
    public decimal  UnitPrice     { get; set; }
    public Guid?    CurrencyId    { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo   { get; set; }
}

public class UpdatePricingRuleRequest
{
    public string   PriceType     { get; set; } = string.Empty;
    public decimal? MinQty        { get; set; }
    public decimal? MaxQty        { get; set; }
    public decimal  UnitPrice     { get; set; }
    public Guid     CurrencyId    { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo  { get; set; }
    public bool     IsActive      { get; set; } = true;
}

public class PricingRuleModel
{
    public Guid     Uuid          { get; set; }
    public Guid     VariantUuid   { get; set; }
    public string   VariantSku    { get; set; } = string.Empty;
    public string   ProductName   { get; set; } = string.Empty;
    public Guid?    PartnerUuid   { get; set; }
    public string   PriceType     { get; set; } = string.Empty;
    public decimal? MinQty        { get; set; }
    public decimal? MaxQty        { get; set; }
    public decimal  UnitPrice     { get; set; }
    public Guid     CurrencyId    { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo  { get; set; }
    public bool     IsActive      { get; set; }
    public DateTime CreatedDate   { get; set; }
}

public class PricingRuleListFilter
{
    public Guid?    VariantUuid { get; set; }
    public Guid?    PartnerUuid { get; set; }
    public string?  PriceType   { get; set; }
    public bool?    IsActive    { get; set; }
    public int      Page        { get; set; } = 1;
    public int      PageSize    { get; set; } = 20;
}
