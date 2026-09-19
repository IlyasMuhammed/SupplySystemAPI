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
