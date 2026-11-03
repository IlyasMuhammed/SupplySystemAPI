namespace SMS.Modules.Finance.ArAp;

/// <summary>API-CONTRACT §1.1 — a partner's financial profile with the partner master fields it reads.</summary>
public sealed class PartnerFinancialProfileModel
{
    public Guid      Uuid                  { get; set; }
    public Guid      PartnerUuid           { get; set; }
    public string    Role                  { get; set; } = string.Empty;
    public string?   PartnerCode           { get; set; }
    public string?   PartnerName           { get; set; }
    public string?   PartnerEmail          { get; set; }
    public bool      PartnerIsActive       { get; set; }

    /// <summary>BusinessPartner.CreditLimit (read-only here). Null/0 = no limit.</summary>
    public decimal?  CreditLimit           { get; set; }
    public decimal?  TemporaryCreditLimit  { get; set; }
    public string?   TemporaryCreditExpiry { get; set; }
    public bool      TemporaryLimitActive  { get; set; }
    /// <summary>Null = no limit (D-CORE-3).</summary>
    public decimal?  EffectiveCreditLimit  { get; set; }
    public bool      CreditHold            { get; set; }
    public string?   CreditHoldReason      { get; set; }
    public DateTime? CreditHoldAt          { get; set; }
    public string?   CreditHoldByName      { get; set; }
    public string    RiskTier              { get; set; } = string.Empty;

    public Guid?     DunningProcedureUuid  { get; set; }
    public string?   DunningProcedureCode  { get; set; }
    public int       CurrentDunningLevel   { get; set; }
    public string?   LastDunningDate       { get; set; }
    public bool      BlockSalesAtDunning   { get; set; }
    public bool      ExcludeFromDunning    { get; set; }
    public string?   DefaultPaymentMethod  { get; set; }

    public string    PaymentPriority          { get; set; } = string.Empty;
    public string?   PreferredPaymentMethod   { get; set; }
    public bool      WithholdingTaxApplicable { get; set; }
    public Guid?     EarlyPaymentTermUuid     { get; set; }
    public string?   EarlyPaymentTermCode     { get; set; }
    /// <summary>BusinessPartner.PaymentTermsDays (read-only; the spec's default_payment_term_days).</summary>
    public int?      PaymentTermsDays         { get; set; }

    public string?   LastPaymentDate     { get; set; }
    public int?      AverageDaysToPay    { get; set; }
    public decimal?  TotalOverdueAmount  { get; set; }
    public DateTime? StatisticsUpdatedAt { get; set; }
    public string?   Notes               { get; set; }

    public DateTime  CreatedAt      { get; set; }
    public DateTime? ModifiedAt     { get; set; }
    public string?   ModifiedByName { get; set; }
    /// <summary>Base64 of the row version; send it back on PUT for an optimistic check.</summary>
    public string?   RowVersion     { get; set; }
}

/// <summary>API-CONTRACT §1.2 — every field optional; absent = unchanged.</summary>
public sealed class UpdatePartnerFinancialProfileRequest
{
    public string?  RiskTier                 { get; set; }
    public Guid?    DunningProcedureUuid     { get; set; }
    public bool?    ClearDunningProcedure    { get; set; }
    public bool?    BlockSalesAtDunning      { get; set; }
    public bool?    ExcludeFromDunning       { get; set; }
    public string?  DefaultPaymentMethod     { get; set; }
    public string?  PaymentPriority          { get; set; }
    public string?  PreferredPaymentMethod   { get; set; }
    public bool?    WithholdingTaxApplicable { get; set; }
    public Guid?    EarlyPaymentTermUuid     { get; set; }
    public bool?    ClearEarlyPaymentTerm    { get; set; }
    public string?  Notes                    { get; set; }
    public string?  RowVersion               { get; set; }
}

/// <summary>API-CONTRACT §1.3 query.</summary>
public sealed class PartnerProfileListQuery
{
    public string?  Role            { get; set; }
    public bool?    OnHold          { get; set; }
    public string?  RiskTier        { get; set; }
    public int?     MinDunningLevel { get; set; }
    public string?  Search          { get; set; }
    public int      Page            { get; set; } = 1;
    public int      PageSize        { get; set; } = 25;
}

/// <summary>API-CONTRACT §1.4.</summary>
public sealed class ArApSettingsModel
{
    public string? CreditControlMode { get; set; }
}
