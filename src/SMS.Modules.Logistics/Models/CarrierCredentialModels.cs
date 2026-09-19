namespace SMS.Modules.Logistics.Models;

public class SetCarrierCredentialRequest
{
    /// <summary>The name the adapter asks for it by — <c>ApiKey</c>, <c>ClientSecret</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The secret. Encrypted immediately and never returned by any endpoint.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>A note for whoever maintains it. Not the secret, and safe to show.</summary>
    public string? Description { get; set; }

    /// <summary>When the carrier says it expires, if it says.</summary>
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// A credential as the outside world may see it: everything except the credential.
/// <para>
/// There is deliberately no value here and no masked preview. A mask still discloses length and
/// shape, and the reason always given for wanting one — "so an administrator can check it" — is
/// better served by attempting a booking.
/// </para>
/// </summary>
public class CarrierCredentialModel
{
    public Guid      UUID          { get; set; }
    public string    Key           { get; set; } = string.Empty;
    public string?   Description   { get; set; }
    public DateTime? ExpiresAt     { get; set; }

    /// <summary>True when <see cref="ExpiresAt"/> has passed. Every booking fails once it has.</summary>
    public bool      IsExpired     { get; set; }

    public DateTime  SetAt         { get; set; }
    public int       SetBy         { get; set; }
}
