namespace SMS.Modules.Logistics.Models;

public class CreateCarrierAccountRequest
{
    public Guid    CarrierUuid        { get; set; }
    public string  AccountName        { get; set; } = string.Empty;
    public string? AccountNumber      { get; set; }
    public string? DefaultServiceCode { get; set; }
    public bool    IsDefault          { get; set; }
    public bool    IsSandbox          { get; set; }

    /// <summary>Null leaves it to the adapter. False switches it off for this account.</summary>
    public bool? CodEnabled           { get; set; }
    public bool? LabelsEnabled        { get; set; }
    public bool? TrackingEnabled      { get; set; }
    public bool? CancellationEnabled  { get; set; }
    public bool? PickupBookingEnabled { get; set; }

    public string? Notes { get; set; }
}

public class PatchCarrierAccountRequest
{
    public string? AccountName        { get; set; }
    public string? AccountNumber      { get; set; }
    public string? DefaultServiceCode { get; set; }
    public bool?   IsDefault          { get; set; }
    public bool?   IsSandbox          { get; set; }
    public bool?   IsActive           { get; set; }

    public bool? CodEnabled           { get; set; }
    public bool? LabelsEnabled        { get; set; }
    public bool? TrackingEnabled      { get; set; }
    public bool? CancellationEnabled  { get; set; }
    public bool? PickupBookingEnabled { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Which capability overrides to clear back to "whatever the adapter says". Needed because a
    /// null in the fields above means "leave alone" on a patch, so there would otherwise be no way
    /// to express "stop overriding this".
    /// </summary>
    public List<string>? ClearOverrides { get; set; }
}

/// <summary>What a capability resolves to, and why — so a screen can explain itself.</summary>
public class CarrierCapabilityModel
{
    public string Name { get; set; } = string.Empty;

    /// <summary>What the adapter can do.</summary>
    public bool SupportedByProvider { get; set; }

    /// <summary>The account's override: null means it defers to the adapter.</summary>
    public bool? EnabledOnAccount { get; set; }

    /// <summary>What actually applies. Never true when the provider cannot do it.</summary>
    public bool Effective { get; set; }
}

public class CarrierAccountModel
{
    public Guid   UUID        { get; set; }
    public Guid   CarrierUuid { get; set; }
    public string CarrierName { get; set; } = string.Empty;

    public string  AccountName        { get; set; } = string.Empty;
    public string? AccountNumber      { get; set; }
    public string? DefaultServiceCode { get; set; }

    public bool IsDefault { get; set; }
    public bool IsSandbox { get; set; }
    public bool IsActive  { get; set; }

    /// <summary>The adapter this carrier books through, resolved from its integration mode.</summary>
    public string? ProviderKey         { get; set; }
    public string? ProviderDisplayName { get; set; }

    /// <summary>Null when no adapter is registered for the carrier's provider key.</summary>
    public string? ProviderWarning { get; set; }

    public List<CarrierCapabilityModel> Capabilities { get; set; } = [];

    public string?  Notes       { get; set; }
    public DateTime CreatedDate { get; set; }
}

// ── Which adapter a carrier books through ─────────────────────────────────────
//
// Deliberately not on CarrierDetailModel: the legacy carrier screens read that, and a contract test
// holds it to the shape they were built against. Provider configuration is a separate surface.

public class SetCarrierIntegrationRequest
{
    /// <summary>MANUAL (a person books it and keys in the airway bill) or API (an adapter books it).</summary>
    public string  IntegrationMode { get; set; } = string.Empty;

    /// <summary>Required for API, and one of <c>GET carrier-accounts/providers</c>. Ignored for MANUAL.</summary>
    public string? ProviderKey     { get; set; }
}

public class CarrierIntegrationModel
{
    public Guid    CarrierUuid         { get; set; }
    public string  CarrierName         { get; set; } = string.Empty;
    public string  IntegrationMode     { get; set; } = string.Empty;
    public string? ProviderKey         { get; set; }
    public string? ProviderDisplayName { get; set; }

    /// <summary>Set when the carrier names an adapter nothing registers — what the screen is opened to fix.</summary>
    public string? Warning { get; set; }
}

/// <summary>One credential an adapter reads from its account. Never a value — only what to enter.</summary>
public class CourierCredentialModel
{
    public string Key         { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool   Required    { get; set; }
    public bool   IsSecret    { get; set; }
}

/// <summary>A courier adapter this deployment has, and what it can do.</summary>
public class CourierProviderModel
{
    public string Key         { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public bool SupportsBooking      { get; set; }
    public bool SupportsRating       { get; set; }
    public bool SupportsTracking     { get; set; }
    public bool SupportsLabels       { get; set; }
    public bool SupportsCancellation { get; set; }
    public bool SupportsCod          { get; set; }
    public bool SupportsMultiPiece   { get; set; }

    /// <summary>What to add under the account's credentials before this adapter can book.</summary>
    public List<CourierCredentialModel> Credentials { get; set; } = [];
}
