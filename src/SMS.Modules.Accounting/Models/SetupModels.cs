namespace SMS.Modules.Accounting.Models;

// A42 API-CONTRACT "Setup status — api/finance/setup".

public sealed class SetupStatusModel
{
    public string    Status                  { get; set; } = string.Empty;
    public Guid?     TemplateUuid            { get; set; }
    public string?   TemplateName            { get; set; }
    public DateTime? TemplateAppliedAt       { get; set; }
    public int       AccountsCreatedCount    { get; set; }
    public bool      FiscalYearConfigured    { get; set; }
    public bool      DefaultsConfigured      { get; set; }
    public bool      OpeningBalancesEntered  { get; set; }
    public bool      OpeningBalancesBalanced { get; set; }
    public DateTime? SetupCompletedAt        { get; set; }
    public string?   SetupCompletedByName    { get; set; }
    /// <summary>yyyy-MM-dd; the copy taken when the opening balances were posted (D-11).</summary>
    public DateOnly? ConversionDate          { get; set; }
    /// <summary>1..6 — the first wizard step not done (6 when everything is done).</summary>
    public int       NextStep                { get; set; }
    public List<SetupStepModel> Steps        { get; set; } = [];
}

public sealed class SetupStepModel
{
    public int    Step  { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool   Done  { get; set; }
}

public sealed class SetupHistoryItemModel
{
    public DateTime At        { get; set; }
    public string   EventType { get; set; } = string.Empty;
    public string   Summary   { get; set; } = string.Empty;
    public string?  UserName  { get; set; }
}

/// <summary>D-4 — the 403 body of a <c>[RequiresGlSetup]</c> endpoint (also carries the ApiResponse fields).</summary>
public sealed class GlSetupRequiredModel
{
    public bool   Success       { get; set; }
    public string Error         { get; set; } = "FINANCE_SETUP_REQUIRED";
    public string Message       { get; set; } = string.Empty;
    public string CurrentStatus { get; set; } = string.Empty;
    public string RedirectUrl   { get; set; } = string.Empty;
    public object? Result       { get; set; }
}
