using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Couriers;

/// <summary>
/// Everything the booking flow needs to know about who it is about to talk to.
/// </summary>
/// <param name="Capabilities">Already narrowed by the account — see <see cref="CarrierCapabilityResolver"/>.</param>
/// <param name="Credentials">
/// Decrypted, ready to hand straight to the adapter. Empty for the manual path, which has nothing
/// to authenticate against. <b>Never log this.</b>
/// </param>
internal sealed record ResolvedCarrierAccount(
    Guid                AccountUuid,
    string              AccountName,
    string?             AccountNumber,
    bool                IsSandbox,
    string?             DefaultServiceCode,
    ICourierProvider    Provider,
    CourierCapabilities Capabilities,
    IReadOnlyDictionary<string, string> Credentials);

internal interface ICarrierAccountResolver
{
    /// <summary>
    /// The account a booking should go out on, and the adapter that will carry it.
    /// <para>
    /// Pass an account explicitly to override; otherwise the carrier's default is used.
    /// </para>
    /// </summary>
    Task<ResolvedCarrierAccount> ResolveAsync(
        Guid carrierUuid, Guid? accountUuid = null, CancellationToken ct = default);
}

/// <summary>
/// Turns "book this with carrier X" into "call adapter Y on account Z, which may do these things".
/// <para>
/// One place, so the booking flow, the label fetch and the tracking poll all agree about which
/// account they are acting on — three separate lookups would eventually disagree, and the symptom
/// would be a label fetched against a different contract from the one the parcel was booked under.
/// </para>
/// </summary>
internal sealed class CarrierAccountResolver : ICarrierAccountResolver
{
    private readonly LogisticsDbContext        _db;
    private readonly ICourierProviderRegistry  _registry;
    private readonly ICarrierCredentialVault   _vault;

    public CarrierAccountResolver(
        LogisticsDbContext db, ICourierProviderRegistry registry, ICarrierCredentialVault vault)
    {
        _db       = db;
        _registry = registry;
        _vault    = vault;
    }

    public async Task<ResolvedCarrierAccount> ResolveAsync(
        Guid carrierUuid, Guid? accountUuid = null, CancellationToken ct = default)
    {
        var carrier = await _db.Carriers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UUID == carrierUuid && !c.IsDelete, ct)
            ?? throw new NotFoundException("Carrier", carrierUuid);

        var provider = _registry.Require(ProviderKeyFor(carrier));

        var account = await FindAccountAsync(carrier, accountUuid, ct);

        // Decrypted only here, at the point a booking is about to go out, and only for the account
        // it will go out on.
        var credentials = account is null
            ? new Dictionary<string, string>()
            : await _vault.GetForAccountAsync(account.Id, ct);

        return new ResolvedCarrierAccount(
            AccountUuid:        account?.UUID ?? Guid.Empty,
            AccountName:        account?.AccountName ?? "(no account)",
            AccountNumber:      account?.AccountNumber,
            IsSandbox:          account?.IsSandbox ?? false,
            DefaultServiceCode: account?.DefaultServiceCode,
            Provider:           provider,
            Capabilities:       CarrierCapabilityResolver.Resolve(provider.Capabilities, account),
            Credentials:        credentials);
    }

    /// <summary>
    /// Which adapter carries this carrier.
    /// <para>
    /// A carrier set to MANUAL integration goes to the manual adapter whatever its provider key
    /// says — the integration mode is the operator's statement of intent, and honouring a stale
    /// provider key over it would send a real booking to an API the operator has decided not to
    /// use. A null integration mode means MANUAL, which is what every carrier predating the column
    /// is.
    /// </para>
    /// </summary>
    private static string ProviderKeyFor(Carrier carrier)
    {
        var mode = string.IsNullOrWhiteSpace(carrier.IntegrationMode)
            ? CarrierIntegrationMode.Manual
            : LogisticsCode.TryParse<CarrierIntegrationMode>(carrier.IntegrationMode, out var parsed)
                ? parsed
                : throw new ConflictException(
                    $"Carrier {carrier.Name} has an unrecognised integration mode " +
                    $"'{carrier.IntegrationMode}'. Valid values: " +
                    $"{string.Join(", ", LogisticsCode.Codes<CarrierIntegrationMode>())}.");

        return mode == CarrierIntegrationMode.Manual
            ? Manual.ManualCourierProvider.ProviderKey
            : carrier.ProviderKey ?? throw new ConflictException(
                $"Carrier {carrier.Name} is set to {LogisticsCode.Of(mode)} integration but names " +
                "no courier provider, so there is no adapter to book through.");
    }

    private async Task<CarrierAccount?> FindAccountAsync(
        Carrier carrier, Guid? accountUuid, CancellationToken ct)
    {
        if (accountUuid is { } requested)
        {
            var account = await _db.CarrierAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.UUID == requested && !a.IsDelete, ct)
                ?? throw new NotFoundException("Carrier account", requested);

            if (account.CarrierId != carrier.Id)
                throw new ConflictException(
                    $"Account '{account.AccountName}' belongs to a different carrier. Booking on it " +
                    $"would put the consignment on somebody else's contract.");

            if (!account.IsActive)
                throw new ConflictException(
                    $"Account '{account.AccountName}' is inactive and cannot be booked on.");

            return account;
        }

        var accounts = await _db.CarrierAccounts
            .AsNoTracking()
            .Where(a => a.CarrierId == carrier.Id && !a.IsDelete && a.IsActive)
            .ToListAsync(ct);

        if (accounts.Count == 0)
        {
            // The manual path needs no account — there is no carrier system to authenticate
            // against — so an unconfigured manual carrier still books. Anything else does not.
            if (ProviderKeyFor(carrier) == Manual.ManualCourierProvider.ProviderKey) return null;

            throw new ConflictException(
                $"Carrier {carrier.Name} has no active account configured, so there are no " +
                "credentials to book with. Add one before booking through the adapter.");
        }

        return accounts.FirstOrDefault(a => a.IsDefault)
            // No default set is a configuration gap, not a reason to guess: picking one silently
            // means the contract a parcel is booked under depends on row order.
            ?? (accounts.Count == 1
                ? accounts[0]
                : throw new ConflictException(
                    $"Carrier {carrier.Name} has {accounts.Count} active accounts and none is " +
                    "marked default, so there is no way to tell which contract to book on. " +
                    "Set a default, or name the account on the consignment."));
    }
}
