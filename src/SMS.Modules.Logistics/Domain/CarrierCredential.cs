using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// One secret belonging to one carrier account — an API key, a password, a client secret.
/// <para>
/// <b>Key–value rather than columns</b>, because every carrier wants something different: an
/// account number and a password here, a client id and secret there, a token somewhere else.
/// Columns would mean a migration per carrier integration, and the adapter contract already
/// carries credentials as an opaque dictionary, so this is the shape it consumes.
/// </para>
/// <para>
/// <b>Its own table, not columns on the account.</b> Every query that lists accounts would
/// otherwise be dragging ciphertext it has no business reading, and the narrower the code that can
/// see a secret, the fewer places can leak one.
/// </para>
/// <para>
/// <see cref="EncryptedValue"/> is the only place the secret exists, and it is never returned by
/// the API — see <c>CarrierCredentialVault</c>.
/// </para>
/// </summary>
internal class CarrierCredential : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int            CarrierAccountId { get; set; }
    public CarrierAccount CarrierAccount   { get; set; } = null!;

    /// <summary>What the adapter asks for it by — <c>ApiKey</c>, <c>ClientSecret</c>.</summary>
    public string CredentialKey { get; set; } = string.Empty;

    /// <summary>Ciphertext. Never logged, never returned, never rendered.</summary>
    public string EncryptedValue { get; set; } = string.Empty;

    /// <summary>
    /// Free text for whoever maintains it — "rotate before renewal", "issued by the account
    /// manager". Not the secret, and safe to show.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// When the carrier says this expires, if it says. A credential that silently lapses takes
    /// every booking with it, and the first symptom is an authentication failure at the worst
    /// possible moment.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
