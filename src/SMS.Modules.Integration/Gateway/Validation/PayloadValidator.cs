using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Gateway.Validation;

/// <summary>What validation found: errors block the record (state Blocked); warnings are stored on the map and sent anyway.</summary>
internal sealed class PayloadValidationResult
{
    public List<GatewayError> Errors   { get; } = new();
    public List<string>       Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;

    public void Error(string field, string code, string message) => Errors.Add(new GatewayError(field, code, message));
}

/// <summary>
/// One rule set per kind (plan §2.6 step 3, D-1…D-5, D-7, D-10, report V-1…V-8; SAP alignment S-10
/// multicurrency and S-11 tax mapping by code). Runs on the
/// in-process path, the HTTP path and again in the executor before anything is sent, so a settings
/// change made after a payload arrived is caught too. Messages say what is wrong <b>and how to fix it</b>.
/// </summary>
internal interface IPayloadValidator
{
    Task<PayloadValidationResult> ValidateAsync(
        SyncKind kind, object payload, EntityMap map, IntegrationConnection connection, IntegrationSettings settings,
        CancellationToken ct = default);
}

internal sealed partial class PayloadValidator : IPayloadValidator
{
    public const int MaxDocNumberLength = 21;
    public const int MaxEmailLength     = 100;
    public const int MaxSkuLength       = 100;
    public const decimal Tolerance      = 0.01m;

    private readonly IntegrationDbContext  _db;
    private readonly IRemoteNameResolver   _names;
    private readonly ICurrencyRules        _currency;
    private readonly IBaseCurrencyResolver? _baseCurrency;

    /// <param name="baseCurrency">A35 D-19 — the organization's purchase base, for the bill rule. Optional: without it no bill is refused for it.</param>
    public PayloadValidator(IntegrationDbContext db, IRemoteNameResolver names, ICurrencyRules currency, IBaseCurrencyResolver? baseCurrency = null)
    {
        _db           = db;
        _names        = names;
        _currency     = currency;
        _baseCurrency = baseCurrency;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    public async Task<PayloadValidationResult> ValidateAsync(
        SyncKind kind, object payload, EntityMap map, IntegrationConnection connection, IntegrationSettings settings,
        CancellationToken ct = default)
    {
        var result = new PayloadValidationResult();

        switch (kind)
        {
            case SyncKind.Customer or SyncKind.Vendor:
                await ValidatePartyAsync((PartyPayload)payload, kind, map, connection, result, ct);
                break;
            case SyncKind.Item:
                await ValidateItemAsync((ItemPayload)payload, map, settings, result, ct);
                break;
            case SyncKind.SalesInvoice:
                await ValidateSalesInvoiceAsync((SalesInvoicePayload)payload, map, connection, settings, result, ct);
                break;
            case SyncKind.Bill:
                await ValidateBillAsync((BillPayload)payload, map, connection, settings, result, ct);
                break;
        }

        return result;
    }

    // ── Customer / Vendor ────────────────────────────────────────────────────

    private async Task ValidatePartyAsync(
        PartyPayload p, SyncKind kind, EntityMap map, IntegrationConnection connection,
        PayloadValidationResult r, CancellationToken ct)
    {
        var what = kind == SyncKind.Customer ? "customer" : "vendor";

        if (string.IsNullOrWhiteSpace(p.DisplayName))
        {
            r.Error("displayName", "DISPLAY_NAME_REQUIRED", $"The {what} has no name. QuickBooks needs one — give the {what} a name and save it again.");
        }
        else
        {
            var effective = await _names.ResolvePartyNameAsync(map, p, ct);
            CheckName(effective, RemoteNameResolver.PartyBaseName(p), "displayName", what, r);
        }

        if (!string.IsNullOrWhiteSpace(p.Email))
        {
            var email = p.Email.Trim();
            if (email.Length > MaxEmailLength)
                r.Error("email", "EMAIL_INVALID",
                    $"The email address is {email.Length} characters; QuickBooks allows {MaxEmailLength}. Shorten it on the {what}.");
            else if (!EmailPattern().IsMatch(email))
                r.Error("email", "EMAIL_INVALID",
                    $"'{email}' is not a valid email address, and QuickBooks refuses invalid ones. Correct or clear it on the {what}.");
        }

        await CheckCurrencyAsync(p.CurrencyCode, connection, "currencyCode", $"this {what}", documentDate: null, r, ct);

        if (!string.IsNullOrWhiteSpace(p.PaymentTermExternalId))
        {
            var termId = p.PaymentTermExternalId.Trim();
            var mapped = await _db.PaymentTermMappings.AnyAsync(
                t => t.ConnectionId == connection.Id && t.PaymentTermExternalId == termId && t.QboTermId != "", ct);
            if (!mapped)
                r.Warnings.Add(
                    $"Payment term '{termId}' is not mapped to a QuickBooks term, so the {what} is sent without one. " +
                    "Map it under QuickBooks Integration → Mappings → Payment terms.");
        }
    }

    // ── Item ─────────────────────────────────────────────────────────────────

    private async Task ValidateItemAsync(
        ItemPayload p, EntityMap map, IntegrationSettings settings, PayloadValidationResult r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.Name))
        {
            r.Error("name", "NAME_REQUIRED", "The product has no name. QuickBooks needs one — name the product and save it again.");
        }
        else
        {
            var effective = await _names.ResolveItemNameAsync(map, p, ct);
            CheckName(effective, RemoteNameResolver.ItemBaseName(p), "name", "product", r);
        }

        if (!string.IsNullOrEmpty(p.Sku) && p.Sku.Trim().Length > MaxSkuLength)
            r.Error("sku", "SKU_TOO_LONG",
                $"The SKU is {p.Sku.Trim().Length} characters; QuickBooks allows {MaxSkuLength}. Shorten it on the product variant.");

        if (p.IsSold && string.IsNullOrWhiteSpace(settings.DefaultIncomeAccountId))
            r.Error("settings.defaultIncomeAccountId", "SETTINGS_ACCOUNT_MISSING",
                "Products that are sold need an income account in QuickBooks, and none is set. " +
                "Choose the default income account under QuickBooks Integration → Mappings.");

        if (p.IsPurchased && string.IsNullOrWhiteSpace(settings.DefaultExpenseAccountId))
            r.Error("settings.defaultExpenseAccountId", "SETTINGS_ACCOUNT_MISSING",
                "Products that are purchased need an expense account in QuickBooks, and none is set. " +
                "Choose the default expense account under QuickBooks Integration → Mappings.");
    }

    // ── Sales invoice ────────────────────────────────────────────────────────

    private async Task ValidateSalesInvoiceAsync(
        SalesInvoicePayload p, EntityMap map, IntegrationConnection connection, IntegrationSettings settings,
        PayloadValidationResult r, CancellationToken ct)
    {
        if (p.Status == SalesInvoicePayloadStatus.CreditNote)
        {
            r.Error("status", "CREDIT_NOTE_UNSUPPORTED",
                "Credit notes are not sent to QuickBooks yet. Enter this credit note in QuickBooks by hand.");
            return;
        }

        CheckDocNumber(p.DocNumber, "invoice", r);

        if (string.IsNullOrWhiteSpace(p.CustomerExternalId))
            r.Error("customerExternalId", "CUSTOMER_REQUIRED", "The invoice has no customer. Every QuickBooks invoice needs one.");

        if (await CheckCurrencyAsync(p.CurrencyCode, connection, "currencyCode", "this invoice", p.TxnDate, r, ct,
                (p.ExchangeRate, p.ExchangeRateCurrencyCode)))
            await CheckPartyCurrencyAsync(SyncKind.Customer, p.CustomerExternalId, p.CurrencyCode, map, connection, r, ct);

        var lines = p.Lines ?? [];
        if (lines.Count == 0)
        {
            r.Error("lines", "LINES_REQUIRED", "The invoice has no lines. QuickBooks refuses an empty invoice.");
            return;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line  = lines[i];
            var label = LineLabel(line.LineNo, i);
            var field = $"lines[{i}]";

            if (string.IsNullOrWhiteSpace(line.ItemExternalId))
                r.Error($"{field}.itemExternalId", "LINE_ITEM_REQUIRED",
                    $"{label} has no product. QuickBooks invoice lines must name an item.");
            if (line.Quantity <= 0)
                r.Error($"{field}.quantity", "LINE_QUANTITY_INVALID",
                    $"{label} has quantity {line.Quantity}; it must be more than zero.");
            if (line.UnitPrice < 0)
                r.Error($"{field}.unitPrice", "LINE_PRICE_INVALID",
                    $"{label} has a negative unit price ({line.UnitPrice}). Use a credit note for refunds.");
            if (line.DiscountPercent is < 0 or > 100)
                r.Error($"{field}.discountPercent", "LINE_DISCOUNT_INVALID",
                    $"{label} has a discount of {line.DiscountPercent}%; it must be between 0 and 100.");
        }

        if (p.HeaderDiscountAmount < 0)
            r.Error("headerDiscountAmount", "DISCOUNT_INVALID",
                $"The invoice discount is negative ({SyncPayloads.Amount(p.HeaderDiscountAmount)}). It must be zero or more.");

        // Plan S-11: a line with a tax code needs that code mapped; a line without one, its rate.
        await CheckTaxAsync(connection, lines.Select(l => (l.TaxCode, (decimal?)l.TaxPercent)), r, ct);

        // Checked the way the caller totals an invoice: rounded once over the whole invoice.
        var math = InvoiceMath.Of(p);
        if (!math.AddsUp(p))
            r.Error("expectedTotal", "TOTAL_MISMATCH",
                $"The invoice adds up to {SyncPayloads.Amount(math.ComputedTotal)} (subtotal {SyncPayloads.Amount(math.Subtotal)}, " +
                $"less discounts {SyncPayloads.Amount(math.Discount)}, plus tax {SyncPayloads.Amount(p.ExpectedTaxAmount)}), " +
                $"but its total is {SyncPayloads.Amount(p.ExpectedTotal)}. Nothing is sent until they agree — correct the invoice and save it again.");

        // Needed only when a discount line is actually sent (plan D-5).
        if (math.DiscountLine > 0 && string.IsNullOrWhiteSpace(settings.DiscountAccountId))
            r.Error("settings.discountAccountId", "SETTINGS_ACCOUNT_MISSING",
                "This invoice has a discount, and QuickBooks posts discounts to an account that is not chosen yet. " +
                "Choose the discount account under QuickBooks Integration → Mappings.");
    }

    // ── Bill ─────────────────────────────────────────────────────────────────

    private async Task ValidateBillAsync(
        BillPayload p, EntityMap map, IntegrationConnection connection, IntegrationSettings settings,
        PayloadValidationResult r, CancellationToken ct)
    {
        CheckDocNumber(p.DocNumber, "supplier invoice", r);
        await CheckPurchaseBaseAsync(connection, r, ct);

        if (string.IsNullOrWhiteSpace(p.VendorExternalId))
            r.Error("vendorExternalId", "VENDOR_REQUIRED", "The supplier invoice has no supplier. Every QuickBooks bill needs a vendor.");

        if (await CheckCurrencyAsync(p.CurrencyCode, connection, "currencyCode", "this supplier invoice", p.TxnDate, r, ct,
                (p.ExchangeRate, p.ExchangeRateCurrencyCode)))
            await CheckPartyCurrencyAsync(SyncKind.Vendor, p.VendorExternalId, p.CurrencyCode, map, connection, r, ct);

        var lines = p.Lines ?? [];
        if (lines.Count == 0)
        {
            r.Error("lines", "LINES_REQUIRED", "The supplier invoice has no lines. QuickBooks refuses an empty bill.");
            return;
        }

        var needsFreight = false;
        var needsExpense = false;
        decimal sum = 0m;

        for (var i = 0; i < lines.Count; i++)
        {
            var line  = lines[i];
            var label = LineLabel(line.LineNo, i);

            if (line.Amount < 0)
                r.Error($"lines[{i}].amount", "LINE_AMOUNT_INVALID",
                    $"{label} has a negative amount ({SyncPayloads.Amount(line.Amount)}). Supplier credits are not sent yet.");

            switch (line.Category)
            {
                case BillLineCategory.Freight:
                    needsFreight = true;
                    break;
                case BillLineCategory.Other:
                case BillLineCategory.Goods when string.IsNullOrWhiteSpace(line.ItemExternalId):
                    needsExpense = true;
                    break;
            }

            sum += line.Amount;
        }

        if (needsFreight && string.IsNullOrWhiteSpace(settings.FreightExpenseAccountId))
            r.Error("settings.freightExpenseAccountId", "SETTINGS_ACCOUNT_MISSING",
                "This bill has freight, and the freight expense account is not chosen yet. " +
                "Choose it under QuickBooks Integration → Mappings.");

        if (needsExpense && string.IsNullOrWhiteSpace(settings.DefaultExpenseAccountId))
            r.Error("settings.defaultExpenseAccountId", "SETTINGS_ACCOUNT_MISSING",
                "This bill has lines without a product, which QuickBooks posts to the default expense account — and none is chosen yet. " +
                "Choose it under QuickBooks Integration → Mappings.");

        // Plan S-11 / D-10: a line with a tax code needs the code mapped; one with only a rate, the rate;
        // one with neither uses the default purchase tax code (or none at all).
        await CheckTaxAsync(connection, lines.Select(l => (l.TaxCode, l.TaxPercent)), r, ct);

        // Supplier invoices store each line's total rounded, but their subtotal rounded once — so a
        // difference of up to half a cent per line is rounding, not an error.
        var computed   = sum + p.ExpectedTaxAmount;
        var difference = Math.Abs(computed - p.ExpectedTotal);
        var tolerance  = BillTolerance(lines.Count);

        if (difference > tolerance)
            r.Error("expectedTotal", "TOTAL_MISMATCH",
                $"The lines add up to {SyncPayloads.Amount(computed)} (lines {SyncPayloads.Amount(sum)} plus tax " +
                $"{SyncPayloads.Amount(p.ExpectedTaxAmount)}), but the supplier invoice total is {SyncPayloads.Amount(p.ExpectedTotal)}. " +
                "Nothing is sent until they agree — correct the supplier invoice and save it again.");
        else if (difference > Tolerance)
            r.Warnings.Add(
                $"The bill's lines add up to {SyncPayloads.Amount(computed)} but its total is {SyncPayloads.Amount(p.ExpectedTotal)} " +
                $"(a {SyncPayloads.Amount(difference)} rounding difference across {lines.Count} lines); QuickBooks will show the lines' total.");
    }

    /// <summary>
    /// A35 D-19 — QuickBooks keeps its books in its home currency, which is compared with SCM's <b>sale</b> base. A supplier
    /// invoice is booked in the organization's <b>purchase</b> base; when that is another currency, its base amounts mean
    /// nothing to QuickBooks and the bill is refused. Unknown purchase base or home currency: no refusal.
    /// </summary>
    private async Task CheckPurchaseBaseAsync(IntegrationConnection connection, PayloadValidationResult r, CancellationToken ct)
    {
        if (_baseCurrency is null) return;

        var home = (await _currency.FactsAsync(connection, ct)).HomeCurrency;
        if (home is null) return;

        var purchase = CurrencyRules.Normalize(
            (await _baseCurrency.ResolveAsync(connection.OrganizationId, SMS.Shared.Common.TransactionDomain.Purchase, ct)).Code);
        if (purchase is null || purchase == home) return;

        r.Error("currencyCode", "PURCHASE_BASE_NOT_HOME",
            $"SCM's purchase base currency is {purchase} but the QuickBooks company's home currency is {home}. Supplier invoices are " +
            $"booked in {purchase}, which QuickBooks does not keep, so bills are not sent while the two differ. Enter this bill in " +
            $"QuickBooks by hand, or set the purchase base currency to {home} under Settings → Currency Configuration; blocked bills " +
            "are then checked again and sent by themselves.");
    }

    /// <summary>0.01 plus half a cent per line.</summary>
    public static decimal BillTolerance(int lineCount) => Tolerance + 0.005m * lineCount;

    // ── Shared rules ─────────────────────────────────────────────────────────

    private static void CheckName(string effective, string baseName, string field, string what, PayloadValidationResult r)
    {
        if (effective.Length > RemoteNameResolver.MaxNameLength)
        {
            var suffixNote = effective.Length > baseName.Length
                ? $" once the disambiguating suffix '{effective[baseName.Length..]}' is added (another record already uses the plain name)"
                : string.Empty;
            r.Error(field, "NAME_TOO_LONG",
                $"The {what} name is {effective.Length} characters{suffixNote}; QuickBooks allows {RemoteNameResolver.MaxNameLength}. " +
                $"Shorten the name in SCM — it is never cut off automatically, because a cut-off name would stop matching.");
        }

        if (effective.Contains(':'))
            r.Error(field, "NAME_HAS_COLON",
                $"The {what} name '{effective}' contains ':', which QuickBooks reserves for sub-records (Parent:Child). Remove the colon from the name.");
    }

    private static void CheckDocNumber(string? docNumber, string what, PayloadValidationResult r)
    {
        if (string.IsNullOrWhiteSpace(docNumber))
            r.Error("docNumber", "DOCNUMBER_REQUIRED", $"The {what} has no number. QuickBooks needs one to find it again.");
        else if (docNumber.Trim().Length > MaxDocNumberLength)
            r.Error("docNumber", "DOCNUMBER_TOO_LONG",
                $"The {what} number '{docNumber.Trim()}' is {docNumber.Trim().Length} characters; QuickBooks allows {MaxDocNumberLength}. " +
                "It is never cut off automatically (the shortened number would not match). Use a shorter number.");
    }

    /// <summary>
    /// Plans D-1 and S-10. A record in the home currency is always fine. A foreign one needs QuickBooks'
    /// multicurrency on and the currency active there; a foreign document (<paramref name="documentDate"/>
    /// given) also needs an exchange rate into the home currency — its own (the caller's snapshot,
    /// <paramref name="documentRate"/>) or else SMS's for its date — which the builder sends as its ExchangeRate.
    /// </summary>
    /// <returns>False when this check refused the currency (so checks that build on it are skipped).</returns>
    private async Task<bool> CheckCurrencyAsync(
        string? currency, IntegrationConnection connection, string field, string what, DateTime? documentDate,
        PayloadValidationResult r, CancellationToken ct, (decimal? Rate, string? CurrencyCode) documentRate = default)
    {
        var code = CurrencyRules.Normalize(currency);
        if (code is null) return true;

        var facts = await _currency.FactsAsync(connection, ct);
        var home  = facts.HomeCurrency;
        if (home is null || code == home) return true;

        if (!facts.MultiCurrencyEnabled)
        {
            r.Error(field, "CURRENCY_NOT_HOME",
                $"The currency of {what} is {code}, but the QuickBooks company's home currency is {home} and its multicurrency " +
                "setting is off. QuickBooks cannot hold foreign-currency records unless multicurrency is turned on (Account and " +
                $"Settings → Advanced → Currency; it cannot be turned off again), so only {home} records are sent. Enter this one in " +
                "QuickBooks by hand, or turn multicurrency on there and refresh the reference data.");
            return false;
        }

        var active = await _currency.ActiveCurrenciesAsync(connection, ct);
        if (active is null || !active.Contains(code))
        {
            var known = active is null || active.Count == 0
                ? "none are loaded"
                : string.Join(", ", active.OrderBy(c => c, StringComparer.Ordinal));
            r.Error(field, "CURRENCY_NOT_ACTIVE",
                $"The currency of {what} is {code}, which is not one of the QuickBooks company's active currencies ({known}). " +
                $"Add {code} in QuickBooks (Settings → Currencies) and refresh the reference data under QuickBooks Integration; " +
                "it is then checked again and sent by itself.");
            return false;
        }

        if (documentDate is { } date
            && CurrencyRules.DocumentRateToHome(documentRate.Rate, documentRate.CurrencyCode, home) is null
            && await _currency.RateToHomeAsync(code, home, date, ct) is null)
        {
            r.Error(field, "EXCHANGE_RATE_MISSING",
                $"{Capitalize(what)} is in {code}, and SMS has no exchange rate between {code} and {home} (in either direction) " +
                $"dated on or before {date:dd MMM yyyy}. QuickBooks records a foreign-currency transaction at the rate SMS sends. " +
                "Add the rate under Settings → Exchange Rates; the QuickBooks sync checks blocked records again every minute " +
                "and sends this one by itself.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// With multicurrency on, QuickBooks keeps every customer and vendor in one currency — fixed once set — and
    /// refuses a transaction in any other. The party's currency is the one the caller sent for it (none = the
    /// home currency, which is what QuickBooks gives a record created without one), <b>but only where SMS knows
    /// that is what QuickBooks holds</b>:
    /// <list type="bullet">
    /// <item>not in QuickBooks yet — it will be created with the stored payload's currency;</item>
    /// <item>created by us, and the stored payload is the one QuickBooks was last given.</item>
    /// </list>
    /// Otherwise QuickBooks' currency is unknown here and the rule does not guess (QuickBooks has the last word,
    /// and its refusal is reported): a party linked to the accountant's own record (adopted — nothing of ours
    /// set its currency), or one whose newer payload has not reached QuickBooks (a changed currency never does,
    /// QuickBooks keeps the one it was created with). The executor validates again just before sending, by
    /// when a pending party update has usually been written, so the rule applies again there.
    /// Nothing to compare when the party has not been sent at all.
    /// </summary>
    private async Task CheckPartyCurrencyAsync(
        SyncKind partyKind, string? partyExternalId, string? documentCurrency, EntityMap map, IntegrationConnection connection,
        PayloadValidationResult r, CancellationToken ct)
    {
        var docCurrency = CurrencyRules.Normalize(documentCurrency);
        var partyId     = partyExternalId?.Trim();
        if (docCurrency is null || string.IsNullOrEmpty(partyId)) return;

        var facts = await _currency.FactsAsync(connection, ct);
        if (!facts.MultiCurrencyEnabled || facts.HomeCurrency is null) return;

        var stored = await _db.EntityMaps
            .Where(m => m.ConnectionId == connection.Id && m.SourceSystem == map.SourceSystem
                     && m.Kind == partyKind && m.ExternalId == partyId && m.PayloadJson != null)
            .Select(m => new { m.PayloadJson, m.PayloadFingerprint, m.LastPushedFingerprint, m.RemoteId, m.LinkOrigin })
            .FirstOrDefaultAsync(ct);
        if (stored?.PayloadJson is null) return;

        if (stored.RemoteId is not null)
        {
            // The accountant's record: its currency is QuickBooks' own, whatever SCM says.
            if (stored.LinkOrigin == LinkOrigin.Adopted) return;

            // Ours, but QuickBooks may hold an older payload than the stored one.
            var storedFingerprint = stored.PayloadFingerprint ?? SyncPayloads.Fingerprint(stored.PayloadJson);
            if (stored.LastPushedFingerprint is null || stored.LastPushedFingerprint != storedFingerprint) return;
        }

        PartyPayload? party;
        try
        {
            party = (PartyPayload)SyncPayloads.Deserialize(partyKind, stored.PayloadJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        var partyCurrency = CurrencyRules.Normalize(party?.CurrencyCode) ?? facts.HomeCurrency;
        if (partyCurrency == docCurrency) return;

        var (who, doc) = partyKind == SyncKind.Customer ? ("customer", "invoice") : ("vendor", "bill");
        var name = string.IsNullOrWhiteSpace(party?.DisplayName) ? partyId : party!.DisplayName.Trim();
        r.Error("currencyCode", "CURRENCY_PARTY_MISMATCH",
            $"This {doc} is in {docCurrency}, but its {who} '{name}' is in {partyCurrency}. QuickBooks keeps each {who} in one " +
            $"currency and only accepts {doc}s in it. Raise the {doc} in {partyCurrency}, or enter it in QuickBooks by hand.");
    }

    /// <summary>
    /// Plan S-11: tax mapping by code first. A line carrying a tax code needs that code mapped — its rate
    /// alone is not enough, because two codes can share a rate (exempt and zero-rated are both 0%) and mean
    /// different things to QuickBooks. A line with only a rate needs the rate mapped (D-4). A line with
    /// neither is left to the caller (bills: the default purchase tax code, D-10).
    /// </summary>
    private async Task CheckTaxAsync(
        IntegrationConnection connection, IEnumerable<(string? Code, decimal? Rate)> lines, PayloadValidationResult r, CancellationToken ct)
    {
        var list  = lines.ToList();
        var codes = list.Select(l => SyncPayloads.NormalizeTaxCode(l.Code)).Where(c => c is not null).Select(c => c!)
                        .Distinct(StringComparer.Ordinal).ToList();
        var rates = list.Where(l => SyncPayloads.NormalizeTaxCode(l.Code) is null && l.Rate.HasValue)
                        .Select(l => SyncPayloads.NormalizeRate(l.Rate!.Value)).Distinct().ToList();
        if (codes.Count == 0 && rates.Count == 0) return;

        var tooLong = codes.Where(c => c.Length > SyncPayloads.MaxTaxCodeLength).ToList();
        foreach (var code in tooLong)
            r.Error("lines.taxCode", "TAX_CODE_TOO_LONG",
                $"Tax code '{code}' is {code.Length} characters; a tax mapping holds codes of up to {SyncPayloads.MaxTaxCodeLength}. " +
                "Use a shorter tax code.");

        var mappings = await _db.TaxCodeMappings
            .Where(t => t.ConnectionId == connection.Id && t.QboTaxCodeId != "")
            .Select(t => new { t.SourceTaxCode, t.TaxPercent })
            .ToListAsync(ct);

        var mappedCodes = mappings.Where(m => m.SourceTaxCode != null)
            .Select(m => SyncPayloads.NormalizeTaxCode(m.SourceTaxCode)!)
            .ToHashSet(StringComparer.Ordinal);
        var mappedRates = mappings.Where(m => m.SourceTaxCode == null)
            .Select(m => SyncPayloads.NormalizeRate(m.TaxPercent))
            .ToHashSet();

        var unmappedCodes = codes.Except(tooLong).Where(c => !mappedCodes.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (unmappedCodes.Count > 0)
            r.Error("lines.taxCode", "TAX_UNMAPPED",
                $"No QuickBooks tax code is mapped for SMS tax code{(unmappedCodes.Count > 1 ? "s" : "")} " +
                $"{string.Join(", ", unmappedCodes)}. Map {(unmappedCodes.Count > 1 ? "them" : "it")} under " +
                "QuickBooks Integration → Mappings → Tax; saving the mapping checks this record again and sends it.");

        var unmappedRates = rates.Where(rate => !mappedRates.Contains(rate)).OrderBy(rate => rate).ToList();
        if (unmappedRates.Count > 0)
            r.Error("lines.taxPercent", "TAX_UNMAPPED",
                $"No QuickBooks tax code is mapped for tax rate{(unmappedRates.Count > 1 ? "s" : "")} " +
                $"{string.Join(", ", unmappedRates.Select(SyncPayloads.Rate))}. Map {(unmappedRates.Count > 1 ? "them" : "it")} under " +
                "QuickBooks Integration → Mappings → Tax; saving the mapping checks this record again and sends it.");
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string LineLabel(int lineNo, int index) => lineNo > 0 ? $"Line {lineNo}" : $"Line {index + 1}";
}
