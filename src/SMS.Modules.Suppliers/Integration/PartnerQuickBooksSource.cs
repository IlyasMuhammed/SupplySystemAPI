using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Suppliers.Integration;

/// <summary>
/// Business partners, for the QuickBooks gateway. Called <b>by</b> the gateway — from background jobs,
/// with the tenant set through <c>HangfireTenantScope</c> — when an invoice or bill waits on a customer or
/// vendor it has never been sent, for "Push now", and for the hourly reconciliation. The same class sends
/// a partner's roles after an edit in this module (<see cref="SendRolesAsync"/>), so the two paths can
/// never build a partner differently.
/// </summary>
internal sealed class PartnerQuickBooksSource : IQuickBooksSource
{
    private static readonly IReadOnlyCollection<SyncKind> SupportedKinds = [SyncKind.Customer, SyncKind.Vendor];

    private readonly SuppliersDbContext                _db;
    private readonly IQuickBooksGateway                _gateway;
    private readonly IPartnerCurrencyCodes             _currencies;
    private readonly ILogger<PartnerQuickBooksSource>  _log;

    public PartnerQuickBooksSource(
        SuppliersDbContext db, IQuickBooksGateway gateway, IPartnerCurrencyCodes currencies,
        ILogger<PartnerQuickBooksSource> log)
    {
        _db         = db;
        _gateway    = gateway;
        _currencies = currencies;
        _log        = log;
    }

    public IReadOnlyCollection<SyncKind> Kinds => SupportedKinds;

    /// <summary>
    /// Sends the partners asked for as the kind asked for. The partner's own flags are not checked here:
    /// the gateway asks for a customer because an invoice names it, and refusing would leave that invoice
    /// waiting for good. Unknown, malformed and deleted ids are skipped.
    /// </summary>
    public async Task PushAsync(SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        EnsureKind(kind);

        var ids = QuickBooksSupport.ParseIds(externalIds);
        if (ids.Count == 0) return;

        var partners = await OwnPartners().AsNoTracking()
            .Where(p => ids.Contains(p.UUID) && !p.IsDelete)
            .ToListAsync(ct);

        if (partners.Count == 0) return;

        var codes = _currencies.Load();
        foreach (var partner in partners)
            await SendAsync(kind, partner, codes, ct);
    }

    /// <summary>
    /// Every active partner in the role, changed since <paramref name="changedSince"/> (created, edited or
    /// moved through the status workflow — which stamps <c>StatusChangedAt</c>, not <c>ModifiedDate</c>).
    /// Loaded <see cref="QuickBooksSupport.BatchSize"/> at a time by id, so memory stays flat however many
    /// partners an organization has.
    /// </summary>
    public async Task<int> PushAllAsync(SyncKind kind, DateTime? changedSince, CancellationToken ct = default)
    {
        EnsureKind(kind);

        var query = OwnPartners().AsNoTracking()
            .Where(p => !p.IsDelete && p.IsActive
                     && p.Status != "BLACKLISTED" && p.Status != "REJECTED" && p.Status != "INACTIVE");

        query = kind == SyncKind.Customer
            ? query.Where(p => p.IsCustomer)
            : query.Where(p => p.IsVendor || p.IsCarrier || p.IsServiceProvider);

        if (changedSince is { } since)
            query = query.Where(p => p.CreatedDate >= since || p.ModifiedDate >= since || p.StatusChangedAt >= since);

        var codes  = _currencies.Load();
        var sent   = 0;
        var lastId = 0;

        while (true)
        {
            var batch = await query.Where(p => p.Id > lastId)
                .OrderBy(p => p.Id)
                .Take(QuickBooksSupport.BatchSize)
                .ToListAsync(ct);

            foreach (var partner in batch)
            {
                // The query's status test is exact; this repeats it whatever the status's casing.
                if (!PartnerPayloadFactory.IsActiveInQuickBooks(partner)) continue;
                if (await SendAsync(kind, partner, codes, ct)) sent++;
            }

            if (batch.Count < QuickBooksSupport.BatchSize) break;
            lastId = batch[^1].Id;
        }

        return sent;
    }

    /// <summary>
    /// After an edit: the partner in every role it holds. A partner that is neither customer nor payee
    /// sends nothing. Whether a role is in scope (already mapped, or "all active") is the gateway's call.
    /// </summary>
    internal async Task SendRolesAsync(BusinessPartner partner, CancellationToken ct = default)
    {
        var customer = PartnerPayloadFactory.IsCustomerRole(partner);
        var vendor   = PartnerPayloadFactory.IsVendorRole(partner);
        if (!customer && !vendor) return;

        var codes = _currencies.Load();
        if (customer) await SendAsync(SyncKind.Customer, partner, codes, ct);
        if (vendor)   await SendAsync(SyncKind.Vendor,   partner, codes, ct);
    }

    /// <summary>One partner, one role. A gateway failure is logged and reported as not sent, never thrown.</summary>
    private async Task<bool> SendAsync(
        SyncKind kind, BusinessPartner partner, IReadOnlyDictionary<Guid, string> codes, CancellationToken ct)
    {
        var externalId = partner.UUID.ToString();
        var currency   = partner.PreferredCurrency is { } currencyId && codes.TryGetValue(currencyId, out var code)
            ? code
            : null;

        try
        {
            var result = kind == SyncKind.Customer
                ? await _gateway.UpsertCustomerAsync(PartnerPayloadFactory.BuildCustomer(partner, currency), ct)
                : await _gateway.UpsertVendorAsync(PartnerPayloadFactory.BuildVendor(partner, currency), ct);

            QuickBooksSupport.LogResult(_log, result, kind, externalId, partner.SupplierCode);
            return true;
        }
        catch (Exception ex) when (QuickBooksSupport.IsNotCancellation(ex, ct))
        {
            _log.LogWarning(ex,
                "Business partner {PartnerUuid} ({PartnerCode}) could not be handed to the QuickBooks gateway as a {Kind}; " +
                "the hourly reconciliation will offer it again.",
                partner.UUID, partner.SupplierCode, kind);
            return false;
        }
    }

    /// <summary>
    /// The current organization's partners, limited <b>explicitly</b>: "Sync all" / "Push now" call this
    /// source inside a super admin's request, which bypasses the tenant query filter — without this, every
    /// organization's customers and vendors would be handed to the caller's QuickBooks company (security audit Q1).
    /// </summary>
    private IQueryable<BusinessPartner> OwnPartners()
    {
        var organizationId = _db.TenantContext.OrganizationId;
        return _db.BusinessPartners.Where(p => p.OrganizationId == organizationId);
    }

    private static void EnsureKind(SyncKind kind)
    {
        if (kind is not (SyncKind.Customer or SyncKind.Vendor))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Business partners are sent as customers or vendors only.");
    }
}
