using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>A stored payload turned into what the provider writes, with everything resolved to QuickBooks ids.</summary>
internal sealed class BuiltRemoteEntity
{
    public required RemoteEntity Entity { get; init; }

    /// <summary>Customer/Vendor/Item: the name used in QuickBooks after any D-2 suffix.</summary>
    public string? EffectiveName { get; init; }

    public string? DocNumber { get; init; }

    /// <summary>How to look this record up in QuickBooks (unknown outcome, duplicate fault).</summary>
    public required RemoteLookup Lookup { get; init; }

    /// <summary>Records this one references that have no QuickBooks id yet (live mode only — dry runs use placeholders).</summary>
    public IReadOnlyList<GatewayDependency> Unresolved { get; init; } = [];

    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Stored payload → provider DTO (plan §4). Applies the D-2 names, D-4/S-11 tax codes (by code, then
/// rate), D-5 discount line, S-10 exchange rates and the account defaults from settings. Reads only this
/// module's tables (and SMS's exchange rates through SMS.Shared); never calls Intuit.
/// </summary>
internal interface IQboObjectBuilder
{
    Task<BuiltRemoteEntity> BuildAsync(
        EntityMap map, object payload, IntegrationConnection connection, IntegrationSettings settings, bool dryRun,
        CancellationToken ct = default);
}

internal sealed class QboObjectBuilder : IQboObjectBuilder
{
    /// <summary>What an unresolved reference looks like in dry-run output.</summary>
    public const string DryRunRefPrefix = "dry-run:";

    private readonly IntegrationDbContext _db;
    private readonly IRemoteNameResolver  _names;
    private readonly ICurrencyRules       _currency;

    public QboObjectBuilder(IntegrationDbContext db, IRemoteNameResolver names, ICurrencyRules currency)
    {
        _db       = db;
        _names    = names;
        _currency = currency;
    }

    public async Task<BuiltRemoteEntity> BuildAsync(
        EntityMap map, object payload, IntegrationConnection connection, IntegrationSettings settings, bool dryRun,
        CancellationToken ct = default) =>
        payload switch
        {
            CustomerPayload c     => await BuildPartyAsync(map, c, new RemoteCustomer(), connection, ct),
            VendorPayload v       => await BuildVendorAsync(map, v, connection, ct),
            ItemPayload i         => await BuildItemAsync(map, i, settings, ct),
            SalesInvoicePayload s => await BuildInvoiceAsync(map, s, connection, settings, dryRun, ct),
            BillPayload b         => await BuildBillAsync(map, b, connection, settings, dryRun, ct),
            _ => throw new ArgumentException($"Unsupported payload type {payload.GetType().Name}.", nameof(payload))
        };

    // ── Parties ──────────────────────────────────────────────────────────────

    private async Task<BuiltRemoteEntity> BuildVendorAsync(EntityMap map, VendorPayload p, IntegrationConnection connection, CancellationToken ct)
    {
        var vendor = new RemoteVendor { AccountNumber = Trim(p.AccountNumber) };
        return await BuildPartyAsync(map, p, vendor, connection, ct);
    }

    private async Task<BuiltRemoteEntity> BuildPartyAsync(
        EntityMap map, PartyPayload p, RemoteParty party, IntegrationConnection connection, CancellationToken ct)
    {
        var name = await _names.ResolvePartyNameAsync(map, p, ct);

        party.DisplayName  = name;
        party.CompanyName  = Trim(p.CompanyName) ?? RemoteNameResolver.PartyBaseName(p);
        party.Email        = Trim(p.Email);
        party.Phone        = Trim(p.Phone);
        party.Fax          = Trim(p.Fax);
        party.Website      = Trim(p.Website);
        party.TaxId        = Trim(p.TaxId);
        party.BillAddress  = Address(p.BillingAddress);
        party.CurrencyCode = Trim(p.CurrencyCode)?.ToUpperInvariant();
        party.Notes        = Trim(p.Notes);
        party.Active       = p.IsActive;

        var built = new BuiltRemoteEntity
        {
            Entity        = party,
            EffectiveName = name,
            Lookup        = new RemoteLookup(Name: name)
        };

        // Unmapped → sent without a term (validation already warned about it).
        var termId = Trim(p.PaymentTermExternalId);
        if (termId is not null)
            party.TermId = await _db.PaymentTermMappings
                .Where(t => t.ConnectionId == connection.Id && t.PaymentTermExternalId == termId && t.QboTermId != "")
                .Select(t => t.QboTermId)
                .FirstOrDefaultAsync(ct);

        return built;
    }

    private static RemoteAddress? Address(AddressPayload? a)
    {
        if (a is null) return null;
        var address = new RemoteAddress
        {
            Line1      = Trim(a.Line1),
            Line2      = Trim(a.Line2),
            City       = Trim(a.City),
            Region     = Trim(a.Region),
            PostalCode = Trim(a.PostalCode),
            Country    = Trim(a.Country)
        };
        return address.Line1 is null && address.Line2 is null && address.City is null && address.Region is null
               && address.PostalCode is null && address.Country is null
            ? null
            : address;
    }

    // ── Item ─────────────────────────────────────────────────────────────────

    private async Task<BuiltRemoteEntity> BuildItemAsync(EntityMap map, ItemPayload p, IntegrationSettings settings, CancellationToken ct)
    {
        var name = await _names.ResolveItemNameAsync(map, p, ct);

        var item = new RemoteItem
        {
            Name        = name,
            Sku         = Trim(p.Sku),
            Type        = p.Kind == ItemPayloadKind.Service
                              ? RemoteItemType.Service
                              : settings.ItemTypeDefault == ItemTypeDefault.Service ? RemoteItemType.Service : RemoteItemType.NonInventory,
            Description = Trim(p.Description),
            Active      = p.IsActive
        };

        if (p.IsSold)
        {
            item.UnitPrice       = p.SalesPrice;
            item.IncomeAccountId = settings.DefaultIncomeAccountId;
        }

        if (p.IsPurchased)
        {
            item.PurchaseDesc     = Trim(p.Description);
            item.PurchaseCost     = p.PurchaseCost;
            item.ExpenseAccountId = settings.DefaultExpenseAccountId;
        }

        return new BuiltRemoteEntity { Entity = item, EffectiveName = name, Lookup = new RemoteLookup(Name: name) };
    }

    // ── Sales invoice ────────────────────────────────────────────────────────

    private async Task<BuiltRemoteEntity> BuildInvoiceAsync(
        EntityMap map, SalesInvoicePayload p, IntegrationConnection connection, IntegrationSettings settings, bool dryRun,
        CancellationToken ct)
    {
        var refs     = await LoadRefsAsync(map, SyncPayloads.DependenciesOf(SyncKind.SalesInvoice, p), ct);
        var taxes    = await LoadTaxCodesAsync(connection, ct);
        var missing  = new List<GatewayDependency>();
        var currency = CurrencyRules.Normalize(p.CurrencyCode);
        var (rate, rateNote) = await ExchangeRateAsync(currency, p.TxnDate, (p.ExchangeRate, p.ExchangeRateCurrencyCode), connection, "invoice", ct);

        var invoice = new RemoteInvoice
        {
            CustomerId   = Ref(refs, SyncKind.Customer, p.CustomerExternalId, dryRun, missing),
            DocNumber    = p.DocNumber.Trim(),
            TxnDate      = p.TxnDate,
            DueDate      = p.DueDate,
            CurrencyCode = currency,
            ExchangeRate = rate,
            CustomerMemo = Trim(p.CustomerMemo),
            PrivateNote  = Trim(p.PrivateNote)
        };

        // Plan D-5: every line at gross — QuickBooks checks Amount against Qty × UnitPrice — and all the
        // discounts (per line and on the header) in one discount line. That line is the residue that makes
        // QuickBooks' total equal the caller's grand total; see InvoiceMath.
        foreach (var line in p.Lines ?? [])
        {
            invoice.Lines.Add(new RemoteSalesLine
            {
                ItemId      = Ref(refs, SyncKind.Item, line.ItemExternalId, dryRun, missing),
                Description = Trim(line.Description),
                Quantity    = line.Quantity,
                UnitPrice   = line.UnitPrice,
                Amount      = SyncPayloads.Money(line.Quantity * line.UnitPrice),
                // Plan S-11: by the line's tax code first, else by its rate.
                TaxCodeId   = taxes.For(line.TaxCode, line.TaxPercent)
            });
        }

        var math = InvoiceMath.Of(p);
        invoice.DiscountAmount    = math.DiscountLine;
        invoice.DiscountAccountId = math.DiscountLine > 0 ? settings.DiscountAccountId : null;

        var built = new BuiltRemoteEntity
        {
            Entity     = invoice,
            DocNumber  = invoice.DocNumber,
            Lookup     = new RemoteLookup(DocNumber: invoice.DocNumber),
            Unresolved = missing.Distinct().ToList()
        };

        if (math.QuickBooksTotalDiffers)
            built.Warnings.Add(
                $"QuickBooks' total may differ from SCM's by {SyncPayloads.Amount(math.QuickBooksDifference)} because QuickBooks " +
                "rounds each line separately.");
        if (rateNote is not null) built.Warnings.Add(rateNote);

        return built;
    }

    // ── Bill ─────────────────────────────────────────────────────────────────

    private async Task<BuiltRemoteEntity> BuildBillAsync(
        EntityMap map, BillPayload p, IntegrationConnection connection, IntegrationSettings settings, bool dryRun,
        CancellationToken ct)
    {
        var refs     = await LoadRefsAsync(map, SyncPayloads.DependenciesOf(SyncKind.Bill, p), ct);
        var taxes    = await LoadTaxCodesAsync(connection, ct);
        var missing  = new List<GatewayDependency>();
        var currency = CurrencyRules.Normalize(p.CurrencyCode);
        var (rate, rateNote) = await ExchangeRateAsync(currency, p.TxnDate, (p.ExchangeRate, p.ExchangeRateCurrencyCode), connection, "bill", ct);

        var bill = new RemoteBill
        {
            VendorId     = Ref(refs, SyncKind.Vendor, p.VendorExternalId, dryRun, missing),
            DocNumber    = p.DocNumber.Trim(),
            TxnDate      = p.TxnDate,
            DueDate      = p.DueDate,
            CurrencyCode = currency,
            ExchangeRate = rate,
            PrivateNote  = Trim(p.PrivateNote)
        };

        foreach (var line in p.Lines ?? [])
        {
            var remote = new RemoteBillLine
            {
                Description = Trim(line.Description),
                Quantity    = line.Quantity,
                UnitPrice   = line.UnitPrice,
                Amount      = SyncPayloads.Money(line.Amount),
                // Plan S-11: by the line's tax code first, else by its rate. Plan D-10: a line with neither
                // → the default purchase tax code (which may be none).
                TaxCodeId   = SyncPayloads.NormalizeTaxCode(line.TaxCode) is null && !line.TaxPercent.HasValue
                    ? settings.DefaultPurchaseTaxCodeId
                    : taxes.For(line.TaxCode, line.TaxPercent)
            };

            if (!string.IsNullOrWhiteSpace(line.ItemExternalId))
                remote.ItemId = Ref(refs, SyncKind.Item, line.ItemExternalId, dryRun, missing);
            else
                remote.AccountId = line.Category == BillLineCategory.Freight
                    ? settings.FreightExpenseAccountId
                    : settings.DefaultExpenseAccountId;

            bill.Lines.Add(remote);
        }

        var vendorId = bill.VendorId.StartsWith(DryRunRefPrefix, StringComparison.Ordinal) ? null : bill.VendorId;

        var built = new BuiltRemoteEntity
        {
            Entity     = bill,
            DocNumber  = bill.DocNumber,
            Lookup     = new RemoteLookup(DocNumber: bill.DocNumber, VendorRemoteId: string.IsNullOrEmpty(vendorId) ? null : vendorId),
            Unresolved = missing.Distinct().ToList()
        };
        if (rateNote is not null) built.Warnings.Add(rateNote);

        return built;
    }

    /// <summary>
    /// Plan S-10: the rate a foreign-currency document is sent with — home units per one unit of its
    /// currency. The document's own rate first (the caller's snapshot, S-5: SCM fixes it when the invoice is
    /// issued or the bill approved), when it converts into QuickBooks' home currency — so QuickBooks books the
    /// document at the rate SMS booked it, even after SMS's rate table is corrected. Otherwise SMS's exchange
    /// rates for its date, looked up now. None for a home-currency document, or while multicurrency is off
    /// (validation refuses foreign documents then). The rate is part of the built object, so the sync log's
    /// request shows it.
    /// </summary>
    /// <returns>The rate, and a note for the record's warning when the rate is worth pointing out.</returns>
    private async Task<(decimal? Rate, string? Note)> ExchangeRateAsync(
        string? currency, DateTime txnDate, (decimal? Rate, string? CurrencyCode) own, IntegrationConnection connection, string what,
        CancellationToken ct)
    {
        if (currency is null) return (null, null);

        var facts = await _currency.FactsAsync(connection, ct);
        var home  = facts.HomeCurrency;
        if (!facts.MultiCurrencyEnabled || home is null || currency == home) return (null, null);

        if (CurrencyRules.DocumentRateToHome(own.Rate, own.CurrencyCode, home) is { } ownRate) return (ownRate, null);

        // A rate of the document's own that is not into QuickBooks' home currency cannot be sent as it is.
        var ownNote = own.Rate is > 0m && CurrencyRules.Normalize(own.CurrencyCode) is { } other && other != home
            ? $"This {what}'s own exchange rate converts {currency} into {other}, not QuickBooks' home currency {home}, so SMS's " +
              $"{currency} → {home} rate for {txnDate:dd MMM yyyy} was used instead."
            : null;

        var quote = await _currency.RateToHomeAsync(currency, home, txnDate, ct);
        if (quote is null)
            // Validation refuses this before anything is built; only a rate deleted in between gets here.
            return (null, Join(ownNote,
                $"SMS had no {currency} → {home} exchange rate for {txnDate:dd MMM yyyy} when this {what} was built, so it was sent " +
                "without one and QuickBooks applied its own rate."));

        var rate = CurrencyRules.ForQuickBooks(quote.Rate);
        var note = quote.Inverted
            ? $"Exchange rate: SMS has no {currency} → {home} rate, so the reciprocal of its {home} → {currency} rate dated " +
              $"{quote.EffectiveDate:dd MMM yyyy} was sent: 1 {currency} = {CurrencyRules.Format(rate)} {home}."
            : null;
        return (rate, Join(ownNote, note));
    }

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : first + " " + second;

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private async Task<Dictionary<(SyncKind, string), string?>> LoadRefsAsync(
        EntityMap owner, IReadOnlyList<GatewayDependency> deps, CancellationToken ct)
    {
        var ids = deps.Select(d => d.ExternalId).Distinct().ToList();
        if (ids.Count == 0) return new();

        var rows = await _db.EntityMaps
            .Where(m => m.ConnectionId == owner.ConnectionId && m.SourceSystem == owner.SourceSystem && ids.Contains(m.ExternalId))
            .Select(m => new { m.Kind, m.ExternalId, m.RemoteId })
            .ToListAsync(ct);

        var result = new Dictionary<(SyncKind, string), string?>();
        foreach (var row in rows) result[(row.Kind, row.ExternalId)] = row.RemoteId;
        return result;
    }

    private static string Ref(
        Dictionary<(SyncKind, string), string?> refs, SyncKind kind, string? externalId, bool dryRun, List<GatewayDependency> missing)
    {
        var id = (externalId ?? string.Empty).Trim();
        if (refs.TryGetValue((kind, id), out var remoteId) && !string.IsNullOrEmpty(remoteId)) return remoteId;

        missing.Add(new GatewayDependency(kind, id));
        return dryRun ? $"{DryRunRefPrefix}{kind}:{id}" : string.Empty;
    }

    private async Task<TaxCodeLookup> LoadTaxCodesAsync(IntegrationConnection connection, CancellationToken ct) =>
        new(await _db.TaxCodeMappings.AsNoTracking().Where(t => t.ConnectionId == connection.Id).ToListAsync(ct));

    /// <summary>
    /// Plan S-11: a caller's tax → QuickBooks tax code id. By the caller's code first (code rows); then, for a
    /// line without a code, by its rate at four places (percent rows — D-4). A code row's own TaxPercent is
    /// informational and never answers a rate lookup.
    /// </summary>
    internal sealed class TaxCodeLookup
    {
        private readonly Dictionary<string, string>  _byCode = new(StringComparer.Ordinal);
        private readonly Dictionary<decimal, string> _byRate = new();

        public TaxCodeLookup(IEnumerable<TaxCodeMapping> mappings)
        {
            foreach (var m in mappings.Where(m => !string.IsNullOrWhiteSpace(m.QboTaxCodeId)))
            {
                if (SyncPayloads.NormalizeTaxCode(m.SourceTaxCode) is { } code) _byCode.TryAdd(code, m.QboTaxCodeId.Trim());
                else _byRate.TryAdd(SyncPayloads.NormalizeRate(m.TaxPercent), m.QboTaxCodeId.Trim());
            }
        }

        /// <returns>
        /// Null when the line's code (or, for a line without one, its rate) is not mapped — validation refuses that
        /// before a build. A coded line never falls back to its rate: two codes can share a rate and mean different
        /// things to QuickBooks, which is the reason codes are mapped at all.
        /// </returns>
        public string? For(string? taxCode, decimal? taxPercent)
        {
            if (SyncPayloads.NormalizeTaxCode(taxCode) is { } code) return _byCode.TryGetValue(code, out var byCode) ? byCode : null;
            return taxPercent is { } rate && _byRate.TryGetValue(SyncPayloads.NormalizeRate(rate), out var byRate) ? byRate : null;
        }
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
