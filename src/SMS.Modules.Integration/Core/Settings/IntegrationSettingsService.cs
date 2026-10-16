using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Integration.Core.Settings;

/// <summary>Settings, tax and term mappings, the dry-run/live switch and the matching sign-off (plan QBI-13).</summary>
public interface IIntegrationSettingsService
{
    Task<IntegrationSettingsModel> GetSettingsAsync(CancellationToken ct = default);
    Task<IntegrationSettingsModel> UpdateSettingsAsync(UpdateIntegrationSettingsRequest request, int userId, CancellationToken ct = default);

    Task<List<TaxCodeMappingModel>> GetTaxMappingsAsync(CancellationToken ct = default);
    Task<List<TaxCodeMappingModel>> SaveTaxMappingsAsync(SaveTaxCodeMappingsRequest request, int userId, CancellationToken ct = default);
    /// <summary>A44 D-18: suggests a QuickBooks code for every unmapped SMS code / rate and saves the unambiguous ones.</summary>
    Task<TaxImportResultModel> ImportTaxMappingsAsync(int userId, CancellationToken ct = default);

    Task<List<TermMappingModel>> GetTermMappingsAsync(CancellationToken ct = default);
    Task<List<TermMappingModel>> SaveTermMappingsAsync(SaveTermMappingsRequest request, int userId, CancellationToken ct = default);

    Task<IntegrationSettingsModel> SetModeAsync(SetModeRequest request, int userId, CancellationToken ct = default);
    Task<IntegrationSettingsModel> ConfirmMatchingAsync(ConfirmMatchingCompleteRequest request, int userId, CancellationToken ct = default);
}

/// <summary>
/// Every value that refers to QuickBooks — an account, a tax code, a term — is checked against the
/// reference snapshot before it is saved, so a typo or a stale id is refused here with the reason,
/// not by QuickBooks months later on the first invoice that uses it. Every change is audited with its
/// before and after.
/// </summary>
internal sealed partial class IntegrationSettingsService : IIntegrationSettingsService
{
    // AccountType arrives from the provider either as QuickBooks' display text ("Cost of Goods Sold")
    // or as the SDK enum name ("CostofGoodsSold"). Compared with spaces and case removed, both match.
    private static readonly string[] IncomeTypes   = ["income", "otherincome"];
    private static readonly string[] ExpenseTypes  = ["expense", "otherexpense", "costofgoodssold"];
    private static readonly string[] DiscountTypes = ["income", "expense", "otherincome", "otherexpense"];

    [GeneratedRegex("^[0-9+-]")]
    private static partial Regex LooksNumeric();

    private readonly IntegrationDbContext                 _db;
    private readonly IConnectionAccessor                  _accessor;
    private readonly ReferenceDataService                 _reference;
    private readonly IPreflightService                    _preflight;
    private readonly IServiceProvider                     _services;
    private readonly ILogger<IntegrationSettingsService>  _logger;

    public IntegrationSettingsService(
        IntegrationDbContext db, IConnectionAccessor accessor, ReferenceDataService reference, IPreflightService preflight,
        IServiceProvider services, ILogger<IntegrationSettingsService> logger)
    {
        _db        = db;
        _accessor  = accessor;
        _reference = reference;
        _preflight = preflight;
        _services  = services;
        _logger    = logger;
    }

    /// <summary>
    /// A settings or mapping change can be exactly what a Blocked record was waiting for (an account chosen, a tax
    /// code mapped): re-validate them now rather than when someone presses Retry.
    /// </summary>
    private Task RevalidateBlockedAsync(IntegrationConnection connection, CancellationToken ct) =>
        BlockedRecordRevalidator.AfterChangeAsync(_services, connection, _logger, ct);

    // ── Settings ────────────────────────────────────────────────────────────────────────────

    public async Task<IntegrationSettingsModel> GetSettingsAsync(CancellationToken ct = default)
    {
        var connection = await _accessor.GetCurrentAsync(ct);
        if (connection is null) return ToModel(new IntegrationSettings());

        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.ConnectionId == connection.Id, ct)
                    ?? new IntegrationSettings();
        return await ModelAsync(settings, connection.Id, ct);
    }

    public async Task<IntegrationSettingsModel> UpdateSettingsAsync(
        UpdateIntegrationSettingsRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await RequireConnectionAsync(ct);
        var itemType   = ParseEnum<ItemTypeDefault>(request.ItemTypeDefault, "Item type");
        var scope      = ParseEnum<PartnerScope>(request.PartnerScope, "Partner scope");

        var income   = Clean(request.DefaultIncomeAccountId);
        var expense  = Clean(request.DefaultExpenseAccountId);
        var freight  = Clean(request.FreightExpenseAccountId);
        var discount = Clean(request.DiscountAccountId);
        var taxCode  = Clean(request.DefaultPurchaseTaxCodeId);

        if (income is not null || expense is not null || freight is not null || discount is not null || taxCode is not null)
        {
            var data = await RequireReferenceDataAsync(connection.Id, ct);

            RequireAccount(data, income,   "default income",  IncomeTypes,   "Income or Other Income");
            RequireAccount(data, expense,  "default expense", ExpenseTypes,  "Expense, Other Expense or Cost of Goods Sold");
            RequireAccount(data, freight,  "freight expense", ExpenseTypes,  "Expense, Other Expense or Cost of Goods Sold");
            RequireAccount(data, discount, "discount",        DiscountTypes, "Income, Expense, Other Income or Other Expense");
            if (taxCode is not null) RequireTaxCode(data, taxCode, "The default purchase tax code");
        }

        var settings   = await _accessor.GetOrCreateSettingsAsync(connection, ct);
        var before     = Snapshot(settings);
        var posWasOn   = settings.AutoPushPosSales;
        var switches   = LegacySwitches(settings);

        settings.AutoPushCustomers        = request.AutoPushCustomers;
        settings.AutoPushVendors          = request.AutoPushVendors;
        settings.AutoPushItems            = request.AutoPushItems;
        settings.AutoPushSalesInvoices    = request.AutoPushSalesInvoices;
        settings.AutoPushBills            = request.AutoPushBills;
        settings.AutoPushPosSales         = request.AutoPushPosSales ?? settings.AutoPushPosSales;
        settings.ItemTypeDefault          = itemType;
        settings.PartnerScope             = scope;
        settings.DefaultIncomeAccountId   = income;
        settings.DefaultExpenseAccountId  = expense;
        settings.FreightExpenseAccountId  = freight;
        settings.DiscountAccountId        = discount;
        settings.DefaultPurchaseTaxCodeId = taxCode;
        // A date, not an instant: documents dated on or after this day are sent.
        settings.DocumentStartDate        = request.DocumentStartDate?.Date;
        settings.ModifiedBy               = userId;
        settings.ModifiedDate             = DateTime.UtcNow;

        // A44 D-14: the switches this save changed are written onto their sync-control rows (the screen that still sends
        // them keeps working until it moves to the Sync control tab).
        var enabledNow = await ApplyLegacySwitchesAsync(connection, settings, switches, userId, ct);

        IntegrationAudit.Add(_db, connection.Id, AuditAreas.Settings, "Updated", before, Snapshot(settings), userId);
        await _db.SaveChangesAsync(ct);

        // "Sync POS sales" turned off: what is queued but not sent yet is held too. Turned on (or on, with sales
        // invoices just turned on): every held POS sale is offered again — idempotent, so it runs on every save.
        if (posWasOn && !settings.AutoPushPosSales)
        {
            await HeldRecords.HoldQueuedAsync(_services, _db, connection.Id, SyncControlDocumentTypes.PosSale, ct);
            if (_services.GetService<IHeldRecordReleaser>() is not null)
                await HeldRecords.HoldQueuedAsync(_services, _db, connection.Id, SyncControlDocumentTypes.PosReturn, ct);
        }

        await RevalidateBlockedAsync(connection, ct);
        foreach (var type in enabledNow)
            await HeldRecords.ReleaseAsync(_services, connection.Id, type, _logger, ct);

        return await ModelAsync(settings, connection.Id, ct);
    }

    /// <summary>The old switches and the sync-control types each one drives.</summary>
    private static readonly (string[] Types, Func<IntegrationSettings, bool> Read)[] LegacyMap =
    [
        ([SyncControlDocumentTypes.Customer],     s => s.AutoPushCustomers),
        ([SyncControlDocumentTypes.Vendor],       s => s.AutoPushVendors),
        ([SyncControlDocumentTypes.Item],         s => s.AutoPushItems),
        ([SyncControlDocumentTypes.SalesInvoice], s => s.AutoPushSalesInvoices),
        ([SyncControlDocumentTypes.PurchaseBill], s => s.AutoPushBills),
        // POS sales went only with both switches on.
        ([SyncControlDocumentTypes.PosSale, SyncControlDocumentTypes.PosReturn], s => s.AutoPushPosSales && s.AutoPushSalesInvoices)
    ];

    private static bool[] LegacySwitches(IntegrationSettings s) => LegacyMap.Select(m => m.Read(s)).ToArray();

    /// <summary>
    /// Writes the switches the request changed onto their rows (creating a row where a type has none) — only those, so a
    /// save from the old screen never undoes a change made on the Sync control tab. Returns the types that are enabled
    /// after the save and may have held records to release (POS always: the old save released on every save). Does not save.
    /// </summary>
    private async Task<List<string>> ApplyLegacySwitchesAsync(
        IntegrationConnection connection, IntegrationSettings settings, bool[] before, int userId, CancellationToken ct)
    {
        var rows = await _db.SyncControlConfigs.Where(c => c.ConnectionId == connection.Id).ToListAsync(ct);
        var now  = DateTime.UtcNow;
        var release = new List<string>();

        for (var i = 0; i < LegacyMap.Length; i++)
        {
            var (types, read) = LegacyMap[i];
            var value = read(settings);

            foreach (var type in types)
            {
                var row = rows.FirstOrDefault(r => string.Equals(r.DocumentType, type, StringComparison.OrdinalIgnoreCase));

                if (value != before[i])
                {
                    if (row is null)
                    {
                        row = new SyncControlConfig
                        {
                            OrganizationId   = connection.OrganizationId,
                            ConnectionId     = connection.Id,
                            DocumentType     = type,
                            DisplayLabel     = SyncControlDefaults.Labels[type],
                            HoldWhenDisabled = SyncControlDefaults.Legacy(settings, type)?.HoldWhenDisabled ?? false
                        };
                        _db.SyncControlConfigs.Add(row);
                        rows.Add(row);
                    }

                    if (row.IsEnabled != value)
                    {
                        row.IsEnabled  = value;
                        row.ModifiedBy = userId;
                        row.ModifiedAt = now;
                    }
                }

                var enabled = row?.IsEnabled ?? value;
                if (enabled && (value != before[i] || type is SyncControlDocumentTypes.PosSale or SyncControlDocumentTypes.PosReturn))
                    release.Add(type);
            }
        }

        return release;
    }

    /// <summary>The settings as the screen shows them, with the count of POS sales waiting and the accounts in effect.</summary>
    private async Task<IntegrationSettingsModel> ModelAsync(IntegrationSettings settings, int connectionId, CancellationToken ct)
    {
        var model = ToModel(settings);
        model.PosSalesWaiting = await HeldRecords.CountAsync(_services, _db, connectionId, SyncControlDocumentTypes.PosSale, ct);
        if (_services.GetService<IEffectiveAccountsResolver>() is { } accounts)
            model.EffectiveAccounts = EffectiveAccountsModel.From(await accounts.ResolveAsync(settings, ct));
        return model;
    }

    // ── Tax mappings ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Both kinds of row (plan S-11): first the SMS tax codes — every active SMS tax code (sales and purchase,
    /// from Finance through SMS.Shared's <see cref="ITaxCodeLookup"/>, so a code can be mapped before any document
    /// uses it), every code seen on stored invoice/bill lines and every code mapped — then the bare rates seen on
    /// lines <i>without</i> a code plus every percent mapping (records from before tax codes, other systems).
    /// </summary>
    public async Task<List<TaxCodeMappingModel>> GetTaxMappingsAsync(CancellationToken ct = default)
    {
        var connection = await _accessor.GetCurrentAsync(ct);
        if (connection is null) return [];

        var stored = await _db.TaxCodeMappings.AsNoTracking()
            .Where(m => m.ConnectionId == connection.Id)
            .ToListAsync(ct);
        var facts         = await StoredPayloadScanner.ScanAsync(_db, connection.Id, ct);
        var (data, _)     = await _reference.LoadAsync(connection.Id, ct);
        var taxCodeNames  = (data?.TaxCodes ?? []).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().Name);
        var smsCodes      = await SmsTaxCodesAsync(ct);

        string? NameOf(string? id) => id is not null && taxCodeNames.TryGetValue(id, out var name) ? name : null;

        var (byCode, byRate) = Split(stored);

        var codeRows = byCode.Keys.Union(facts.TaxCodes.Keys, StringComparer.Ordinal).Union(smsCodes.Keys, StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .Select(code =>
            {
                byCode.TryGetValue(code, out var mapping);
                facts.TaxCodes.TryGetValue(code, out var seen);
                smsCodes.TryGetValue(code, out var sms);

                // The rate most lines with this code carry (a code's rate can change; documents keep theirs and
                // are what QuickBooks will tax), else the code's current rate in SMS, else the rate stored with
                // the mapping.
                var rate = facts.TaxCodeRates.TryGetValue(code, out var rates) && rates.Count > 0
                    ? rates.OrderByDescending(r => r.Value).ThenByDescending(r => r.Key).First().Key
                    : sms is not null ? StoredPayloadScanner.NormalizeRate(sms.RatePercent)
                    : mapping is not null ? StoredPayloadScanner.NormalizeRate(mapping.TaxPercent) : 0m;

                var suggested = mapping is null && byRate.TryGetValue(rate, out var percentRow) ? percentRow.QboTaxCodeId : null;

                return new TaxCodeMappingModel
                {
                    SourceTaxCode           = code,
                    SourceTaxCodeName       = sms?.Name,
                    SourceTaxCodeUsage      = sms?.Usage.Trim().ToUpperInvariant(),
                    TaxPercent              = rate,
                    QboTaxCodeId            = mapping?.QboTaxCodeId,
                    QboTaxCodeName          = NameOf(mapping?.QboTaxCodeId),
                    TimesSeen               = seen,
                    SuggestedQboTaxCodeId   = suggested,
                    SuggestedQboTaxCodeName = NameOf(suggested)
                };
            });

        var rateRows = byRate.Keys.Union(facts.TaxRates.Keys)
            .OrderBy(r => r)
            .Select(rate =>
            {
                byRate.TryGetValue(rate, out var mapping);
                facts.TaxRates.TryGetValue(rate, out var seen);
                return new TaxCodeMappingModel
                {
                    TaxPercent     = rate,
                    QboTaxCodeId   = mapping?.QboTaxCodeId,
                    QboTaxCodeName = NameOf(mapping?.QboTaxCodeId),
                    TimesSeen      = seen
                };
            });

        return codeRows.Concat(rateRows).ToList();
    }

    /// <summary>
    /// Upserts and deletes (an empty QuickBooks id) rows of both kinds; rows the request does not mention
    /// are left alone. A request with no <c>SourceTaxCode</c> anywhere is exactly the old percent-only save.
    /// </summary>
    public async Task<List<TaxCodeMappingModel>> SaveTaxMappingsAsync(
        SaveTaxCodeMappingsRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await RequireConnectionAsync(ct);
        var items      = (request.Mappings ?? []).ToList();

        if (items.Any(i => i is null))
            throw new BadRequestException("A tax mapping row is empty.");

        foreach (var item in items)
        {
            if (item.TaxPercent is < 0 or > 100)
                throw new BadRequestException(
                    $"Tax percent {PreflightService.FormatRate(item.TaxPercent)} is out of range — it must be between 0 and 100.");

            if (SyncPayloads.NormalizeTaxCode(item.SourceTaxCode) is { Length: > SyncPayloads.MaxTaxCodeLength } longCode)
                throw new BadRequestException(
                    $"Tax code '{longCode}' is longer than {SyncPayloads.MaxTaxCodeLength} characters.");
        }

        var codeItems = items.Where(i => SyncPayloads.NormalizeTaxCode(i.SourceTaxCode) is not null).ToList();
        var rateItems = items.Where(i => SyncPayloads.NormalizeTaxCode(i.SourceTaxCode) is null).ToList();

        var duplicateCode = codeItems.GroupBy(i => SyncPayloads.NormalizeTaxCode(i.SourceTaxCode)!, StringComparer.Ordinal)
                                     .FirstOrDefault(g => g.Count() > 1);
        if (duplicateCode is not null)
            throw new BadRequestException($"Tax code {duplicateCode.Key} appears more than once.");

        var duplicate = rateItems.GroupBy(i => StoredPayloadScanner.NormalizeRate(i.TaxPercent)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new BadRequestException($"Tax percent {PreflightService.FormatRate(duplicate.Key)} appears more than once.");

        if (items.Any(i => !string.IsNullOrWhiteSpace(i.QboTaxCodeId)))
        {
            var data = await RequireReferenceDataAsync(connection.Id, ct);
            foreach (var item in items.Where(i => !string.IsNullOrWhiteSpace(i.QboTaxCodeId)))
                RequireTaxCode(data, item.QboTaxCodeId!.Trim(),
                    SyncPayloads.NormalizeTaxCode(item.SourceTaxCode) is { } code
                        ? $"The tax code for SMS tax code {code}"
                        : $"The tax code for {PreflightService.FormatRate(item.TaxPercent)}");
        }

        var existing         = await _db.TaxCodeMappings.Where(m => m.ConnectionId == connection.Id).ToListAsync(ct);
        var (byCode, byRate) = Split(existing);
        var before           = TaxSnapshot(existing);
        var now              = DateTime.UtcNow;

        void Apply<TKey>(Dictionary<TKey, TaxCodeMapping> rows, TKey key, string? target, Func<TaxCodeMapping> create,
                         Action<TaxCodeMapping> update) where TKey : notnull
        {
            rows.TryGetValue(key, out var mapping);

            if (target is null)
            {
                if (mapping is not null)
                {
                    _db.TaxCodeMappings.Remove(mapping);
                    rows.Remove(key);
                }
                return;
            }

            if (mapping is null)
            {
                mapping = create();
                _db.TaxCodeMappings.Add(mapping);
                rows[key] = mapping;
            }

            update(mapping);
            mapping.QboTaxCodeId = target;
            mapping.ModifiedBy   = userId;
            mapping.ModifiedDate = now;
        }

        foreach (var item in codeItems)
        {
            var code = SyncPayloads.NormalizeTaxCode(item.SourceTaxCode)!;
            var rate = StoredPayloadScanner.NormalizeRate(item.TaxPercent);
            Apply(byCode, code, Clean(item.QboTaxCodeId),
                () => new TaxCodeMapping { OrganizationId = connection.OrganizationId, ConnectionId = connection.Id, SourceTaxCode = code },
                m =>
                {
                    m.SourceTaxCode = code;
                    m.TaxPercent    = rate;   // the code's rate when mapped: informational only
                });
        }

        foreach (var item in rateItems)
        {
            var rate = StoredPayloadScanner.NormalizeRate(item.TaxPercent);
            Apply(byRate, rate, Clean(item.QboTaxCodeId),
                () => new TaxCodeMapping { OrganizationId = connection.OrganizationId, ConnectionId = connection.Id, TaxPercent = rate },
                _ => { });
        }

        var after = TaxSnapshot(byCode.Values.Concat(byRate.Values));
        IntegrationAudit.Add(_db, connection.Id, AuditAreas.TaxMapping, "Saved", before, after, userId);
        await _db.SaveChangesAsync(ct);
        await RevalidateBlockedAsync(connection, ct);

        return await GetTaxMappingsAsync(ct);
    }

    /// <summary>
    /// D-18 "Import &amp; suggest". For every code row and bare-rate row without a mapping: a positive rate → the active
    /// QuickBooks codes with that single rate, narrowed by name when several share it; 0 % → QuickBooks' <c>NON</c> code
    /// when the company has one (US companies), else the 0 % / non-taxable codes, preferring one whose name says
    /// "exempt" or "zero" as the SMS code's name does. Exactly one candidate is saved (through the normal save, so it is
    /// validated, audited and Blocked records re-checked); anything else is returned with its candidates for a person.
    /// Existing mappings are never changed.
    /// </summary>
    public async Task<TaxImportResultModel> ImportTaxMappingsAsync(int userId, CancellationToken ct = default)
    {
        var connection = await RequireConnectionAsync(ct);
        var data       = await RequireReferenceDataAsync(connection.Id, ct);
        var rows       = await GetTaxMappingsAsync(ct);
        var active     = data.TaxCodes.Where(t => t.Active).ToList();
        var result     = new TaxImportResultModel();
        var saves      = new List<TaxCodeMappingItem>();

        static string Norm(string? s) => Matching.MatchNormalizer.Id(s);
        static bool Says(string? name, string word) => (name ?? string.Empty).Contains(word, StringComparison.OrdinalIgnoreCase);
        static ReferenceItemModel Item(RemoteTaxCode t) => new()
        {
            Id = t.Id, Name = t.Name, Type = t.Taxable ? "Taxable" : "NonTaxable", Rate = t.RatePercent, Active = t.Active
        };

        foreach (var row in rows)
        {
            if (row.QboTaxCodeId is not null)
            {
                result.AlreadyMapped++;
                continue;
            }

            var rate    = StoredPayloadScanner.NormalizeRate(row.TaxPercent);
            var scmName = row.SourceTaxCodeName ?? row.SourceTaxCode;
            List<RemoteTaxCode> candidates;
            RemoteTaxCode? pick = null;
            string reason;

            if (rate == 0m)
            {
                var non = active.FirstOrDefault(t => string.Equals(t.Id, "NON", StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals(t.Name, "NON", StringComparison.OrdinalIgnoreCase));
                var zeros = active.Where(t => t.RatePercent is 0m || (t.RatePercent is null && !t.Taxable)).ToList();
                var named = zeros.Where(t => Says(t.Name, "exempt") || Says(t.Name, "zero")).ToList();
                var preferred = Says(scmName, "exempt") ? named.Where(t => Says(t.Name, "exempt")).ToList()
                              : Says(scmName, "zero")   ? named.Where(t => Says(t.Name, "zero")).ToList()
                              : named;

                if (non is not null)
                {
                    pick = non;
                    candidates = [non];
                    reason = "0 % / exempt: QuickBooks' NON (non-taxable) code.";
                }
                else
                {
                    candidates = preferred.Count > 0 ? preferred : zeros;
                    if (preferred.Count == 1) pick = preferred[0];
                    reason = pick is not null
                        ? $"0 % / exempt: the only 0 % QuickBooks code named like it ('{pick.Name}')."
                        : candidates.Count == 0
                            ? "0 %: QuickBooks has no active 0 % or non-taxable code. Create one in QuickBooks, refresh, then map it."
                            : "0 %: more than one 0 % QuickBooks code could be meant — choose one.";
                }
            }
            else
            {
                var byRate = active.Where(t => t.RatePercent is { } r && StoredPayloadScanner.NormalizeRate(r) == rate).ToList();
                candidates = byRate;

                if (byRate.Count == 1)
                {
                    pick   = byRate[0];
                    reason = $"The only active QuickBooks code at {PreflightService.FormatRate(rate)}.";
                }
                else if (byRate.Count > 1)
                {
                    var keys   = new[] { Norm(row.SourceTaxCode), Norm(row.SourceTaxCodeName) }.Where(k => k.Length > 0).ToList();
                    var byName = byRate.Where(t => keys.Any(k => Norm(t.Name) == k || Norm(t.Name).Contains(k, StringComparison.Ordinal))).ToList();
                    if (byName.Count == 1) pick = byName[0];
                    reason = pick is not null
                        ? $"{byRate.Count} QuickBooks codes are {PreflightService.FormatRate(rate)}; '{pick.Name}' has the SMS code's name."
                        : $"{byRate.Count} QuickBooks codes are {PreflightService.FormatRate(rate)} — choose one.";
                }
                else
                {
                    reason = $"No active QuickBooks code is {PreflightService.FormatRate(rate)}. Create one in QuickBooks, refresh, then map it.";
                }
            }

            var line = new TaxImportRowModel
            {
                SourceTaxCode     = row.SourceTaxCode,
                SourceTaxCodeName = row.SourceTaxCodeName,
                TaxPercent        = row.TaxPercent,
                QboTaxCodeId      = pick?.Id,
                QboTaxCodeName    = pick?.Name,
                Reason            = reason,
                Candidates        = candidates.Select(Item).ToList()
            };

            if (pick is null)
            {
                result.NeedsReview.Add(line);
                continue;
            }

            saves.Add(new TaxCodeMappingItem { SourceTaxCode = row.SourceTaxCode, TaxPercent = row.TaxPercent, QboTaxCodeId = pick.Id });
            result.SavedRows.Add(line);
        }

        result.Saved    = saves.Count;
        result.Mappings = saves.Count > 0
            ? await SaveTaxMappingsAsync(new SaveTaxCodeMappingsRequest { Mappings = saves }, userId, ct)
            : rows;
        return result;
    }

    /// <summary>
    /// The organization's active SMS tax codes, sales and purchase, once each (a BOTH code is on both lists), by
    /// normalized code. Read through SMS.Shared — Finance's lookup, resolved optional-safe and scoped to the
    /// current tenant, which is the connection's organization here. Without Finance, or if it cannot answer, the
    /// screen still lists what payloads and mappings hold.
    /// </summary>
    private async Task<Dictionary<string, TaxCodeInfo>> SmsTaxCodesAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, TaxCodeInfo>(StringComparer.Ordinal);
        var lookup = _services.GetService<ITaxCodeLookup>();
        if (lookup is null) return result;

        try
        {
            var sales    = await lookup.ListActiveAsync(TaxCodeUsage.Sales, ct);
            var purchase = await lookup.ListActiveAsync(TaxCodeUsage.Purchase, ct);

            foreach (var info in sales.Concat(purchase))
                if (SyncPayloads.NormalizeTaxCode(info.Code) is { Length: <= SyncPayloads.MaxTaxCodeLength } code)
                    result.TryAdd(code, info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SMS tax codes could not be read for the QuickBooks tax mapping screen; listing mapped and used codes only.");
        }

        return result;
    }

    /// <summary>Code rows by normalized code, percent rows by four-place rate.</summary>
    private static (Dictionary<string, TaxCodeMapping> ByCode, Dictionary<decimal, TaxCodeMapping> ByRate) Split(
        IEnumerable<TaxCodeMapping> mappings)
    {
        var list   = mappings.ToList();
        var byCode = list.Where(m => SyncPayloads.NormalizeTaxCode(m.SourceTaxCode) is not null)
                         .GroupBy(m => SyncPayloads.NormalizeTaxCode(m.SourceTaxCode)!, StringComparer.Ordinal)
                         .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byRate = list.Where(m => SyncPayloads.NormalizeTaxCode(m.SourceTaxCode) is null)
                         .GroupBy(m => StoredPayloadScanner.NormalizeRate(m.TaxPercent))
                         .ToDictionary(g => g.Key, g => g.First());
        return (byCode, byRate);
    }

    /// <summary>Code rows first (by code), then percent rows (by rate) — what the audit's before/after hold.</summary>
    private static object TaxSnapshot(IEnumerable<TaxCodeMapping> mappings) =>
        mappings
            .OrderBy(m => m.SourceTaxCode is null)
            .ThenBy(m => m.SourceTaxCode, StringComparer.Ordinal)
            .ThenBy(m => m.TaxPercent)
            .Select(m => new { m.SourceTaxCode, m.TaxPercent, m.QboTaxCodeId })
            .ToList();

    // ── Term mappings ───────────────────────────────────────────────────────────────────────

    public async Task<List<TermMappingModel>> GetTermMappingsAsync(CancellationToken ct = default)
    {
        var connection = await _accessor.GetCurrentAsync(ct);
        if (connection is null) return [];

        var stored = await _db.PaymentTermMappings.AsNoTracking()
            .Where(m => m.ConnectionId == connection.Id)
            .ToListAsync(ct);
        var facts     = await StoredPayloadScanner.ScanAsync(_db, connection.Id, ct);
        var (data, _) = await _reference.LoadAsync(connection.Id, ct);
        var termNames = (data?.Terms ?? []).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().Name);

        var byId = stored.ToDictionary(m => m.PaymentTermExternalId, StringComparer.OrdinalIgnoreCase);
        var ids  = byId.Keys.Union(facts.PaymentTermIds.Keys, StringComparer.OrdinalIgnoreCase);

        return ids.Select(id =>
            {
                byId.TryGetValue(id, out var mapping);
                return new TermMappingModel
                {
                    PaymentTermExternalId = mapping?.PaymentTermExternalId ?? id,
                    PaymentTermName       = mapping?.PaymentTermName,
                    QboTermId             = mapping?.QboTermId,
                    QboTermName           = mapping is not null && termNames.TryGetValue(mapping.QboTermId, out var name) ? name : null
                };
            })
            .OrderBy(m => m.PaymentTermName ?? m.PaymentTermExternalId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<TermMappingModel>> SaveTermMappingsAsync(
        SaveTermMappingsRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await RequireConnectionAsync(ct);
        var items      = request.Mappings ?? [];

        foreach (var item in items)
        {
            var id = item.PaymentTermExternalId?.Trim();
            if (string.IsNullOrEmpty(id))
                throw new BadRequestException("Every term mapping needs the SCM payment term's id.");
            if (id.Length > 100)
                throw new BadRequestException($"Payment term id '{id[..20]}…' is longer than 100 characters.");
            if (item.PaymentTermName is { Length: > 200 })
                throw new BadRequestException($"The name of payment term '{id}' is longer than 200 characters.");
        }

        var duplicate = items.GroupBy(i => i.PaymentTermExternalId.Trim(), StringComparer.OrdinalIgnoreCase)
                             .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new BadRequestException($"Payment term '{duplicate.Key}' appears more than once.");

        if (items.Any(i => !string.IsNullOrWhiteSpace(i.QboTermId)))
        {
            var data = await RequireReferenceDataAsync(connection.Id, ct);
            foreach (var item in items.Where(i => !string.IsNullOrWhiteSpace(i.QboTermId)))
            {
                var termId = item.QboTermId!.Trim();
                var term   = data.Terms.FirstOrDefault(t => t.Id == termId)
                    ?? throw new BadRequestException(
                           $"QuickBooks term '{termId}' (for payment term '{item.PaymentTermName ?? item.PaymentTermExternalId}') "
                         + "is not in the reference data. Refresh it, then choose again.");
                if (!term.Active)
                    throw new BadRequestException($"QuickBooks term '{term.Name}' is inactive.");
            }
        }

        var existing = await _db.PaymentTermMappings.Where(m => m.ConnectionId == connection.Id).ToListAsync(ct);
        var byId     = existing.ToDictionary(m => m.PaymentTermExternalId, StringComparer.OrdinalIgnoreCase);
        var before   = existing.OrderBy(m => m.PaymentTermExternalId)
            .Select(m => new { m.PaymentTermExternalId, m.PaymentTermName, m.QboTermId }).ToList();
        var now      = DateTime.UtcNow;

        foreach (var item in items)
        {
            var id     = item.PaymentTermExternalId.Trim();
            var target = Clean(item.QboTermId);
            byId.TryGetValue(id, out var mapping);

            if (target is null)
            {
                if (mapping is not null)
                {
                    _db.PaymentTermMappings.Remove(mapping);
                    byId.Remove(id);
                }
                continue;
            }

            if (mapping is null)
            {
                mapping = new PaymentTermMapping
                {
                    OrganizationId = connection.OrganizationId, ConnectionId = connection.Id, PaymentTermExternalId = id
                };
                _db.PaymentTermMappings.Add(mapping);
                byId[id] = mapping;
            }

            mapping.QboTermId       = target;
            mapping.PaymentTermName = Clean(item.PaymentTermName) ?? mapping.PaymentTermName;
            mapping.ModifiedBy      = userId;
            mapping.ModifiedDate    = now;
        }

        var after = byId.Values.OrderBy(m => m.PaymentTermExternalId)
            .Select(m => new { m.PaymentTermExternalId, m.PaymentTermName, m.QboTermId }).ToList();
        IntegrationAudit.Add(_db, connection.Id, AuditAreas.TermMapping, "Saved", before, after, userId);
        await _db.SaveChangesAsync(ct);
        await RevalidateBlockedAsync(connection, ct);

        return await GetTermMappingsAsync(ct);
    }

    // ── Mode & matching ─────────────────────────────────────────────────────────────────────

    public async Task<IntegrationSettingsModel> SetModeAsync(SetModeRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mode       = ParseEnum<SyncMode>(request.Mode, "Mode");
        var connection = await RequireConnectionAsync(ct);
        var settings   = await _accessor.GetOrCreateSettingsAsync(connection, ct);

        if (mode == SyncMode.Live)
        {
            var preflight = await _preflight.RunAsync(ct);
            var missing   = preflight.Checks
                .Where(c => c.Status == "Fail")
                .Select(c => $"{c.Title}: {c.Message}")
                .ToList();

            if (settings.MatchingConfirmedAt is null)
                missing.Add("Existing records matched: confirm matching of customers, vendors and items first.");

            if (missing.Count > 0)
                throw new BadRequestException(
                    "Live cannot be switched on yet. " + string.Join(" ", missing.Select((m, i) => $"({i + 1}) {m}")));
        }

        var before = new { Mode = settings.Mode.ToString(), ConnectionStatus = connection.Status.ToString() };

        settings.Mode         = mode;
        settings.ModifiedBy   = userId;
        settings.ModifiedDate = DateTime.UtcNow;

        IntegrationAudit.Add(_db, connection.Id, AuditAreas.Mode, $"Set {mode}", before,
            new { Mode = mode.ToString(), ConnectionStatus = ConnectionAfterModeChange(connection.Status).ToString() }, userId);

        // Choosing a mode is what starts syncing: a connection still in setup becomes Live (syncing in
        // whichever mode the settings now say). Revoked/Expired/NotConnected stay as they are.
        await ConnectionPersistence.SaveWithRetryAsync(_db, connection,
            c => c.Status = ConnectionAfterModeChange(c.Status), ct);

        return await ModelAsync(settings, connection.Id, ct);
    }

    public async Task<IntegrationSettingsModel> ConfirmMatchingAsync(
        ConfirmMatchingCompleteRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await RequireConnectionAsync(ct);
        var settings   = await _accessor.GetOrCreateSettingsAsync(connection, ct);

        if (!request.Confirmed && settings.Mode == SyncMode.Live)
            throw new BadRequestException(
                "Matching cannot be reopened while Live is on — Live requires it. Switch to dry run first.");

        var before = new { settings.MatchingConfirmedAt };

        settings.MatchingConfirmedAt = request.Confirmed ? settings.MatchingConfirmedAt ?? DateTime.UtcNow : null;
        settings.ModifiedBy          = userId;
        settings.ModifiedDate        = DateTime.UtcNow;

        IntegrationAudit.Add(_db, connection.Id, AuditAreas.Matching, request.Confirmed ? "Confirmed" : "Reopened",
            before, new { settings.MatchingConfirmedAt }, userId);
        await _db.SaveChangesAsync(ct);

        return await ModelAsync(settings, connection.Id, ct);
    }

    private static ConnectionStatus ConnectionAfterModeChange(ConnectionStatus status) =>
        status is ConnectionStatus.Connected or ConnectionStatus.NeedsSetup ? ConnectionStatus.Live : status;

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private async Task<IntegrationConnection> RequireConnectionAsync(CancellationToken ct) =>
        await _accessor.GetCurrentAsync(ct)
        ?? throw new ConflictException("Connect QuickBooks first — settings belong to a connected company.");

    private async Task<RemoteReferenceData> RequireReferenceDataAsync(int connectionId, CancellationToken ct)
    {
        var (data, _) = await _reference.LoadAsync(connectionId, ct);
        return data ?? throw new BadRequestException(
            "QuickBooks' accounts, tax codes and terms have not been loaded yet. Refresh the reference data first.");
    }

    private static void RequireAccount(RemoteReferenceData data, string? id, string label, string[] allowed, string allowedText)
    {
        if (id is null) return;

        var account = data.Accounts.FirstOrDefault(a => a.Id == id)
            ?? throw new BadRequestException(
                   $"The {label} account '{id}' is not in QuickBooks' chart of accounts. Refresh the reference data, then choose again.");

        if (!account.Active)
            throw new BadRequestException($"The {label} account '{account.Name}' is inactive in QuickBooks.");

        if (!allowed.Contains(NormalizeAccountType(account.AccountType)))
            throw new BadRequestException(
                $"The {label} account '{account.Name}' is a {account.AccountType} account; it must be {allowedText}.");
    }

    private static void RequireTaxCode(RemoteReferenceData data, string id, string label)
    {
        var code = data.TaxCodes.FirstOrDefault(t => t.Id == id)
            ?? throw new BadRequestException(
                   $"{label} ('{id}') is not one of QuickBooks' tax codes. Refresh the reference data, then choose again.");

        if (!code.Active)
            throw new BadRequestException($"{label} ('{code.Name}') is inactive in QuickBooks.");
    }

    internal static string NormalizeAccountType(string? type) =>
        new string((type ?? string.Empty).Where(char.IsLetter).ToArray()).ToLowerInvariant();

    private static TEnum ParseEnum<TEnum>(string? value, string label) where TEnum : struct, Enum
    {
        var trimmed = value?.Trim();

        // Enum.TryParse also accepts "1" or "-3"; only the names are part of the contract.
        if (!string.IsNullOrEmpty(trimmed) && !LooksNumeric().IsMatch(trimmed)
            && Enum.TryParse<TEnum>(trimmed, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            return parsed;

        throw new BadRequestException(
            $"{label} '{value}' is not valid. Use one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static object Snapshot(IntegrationSettings s) => new
    {
        Mode            = s.Mode.ToString(),
        s.AutoPushCustomers, s.AutoPushVendors, s.AutoPushItems, s.AutoPushSalesInvoices, s.AutoPushBills, s.AutoPushPosSales,
        ItemTypeDefault = s.ItemTypeDefault.ToString(),
        PartnerScope    = s.PartnerScope.ToString(),
        s.DefaultIncomeAccountId, s.DefaultExpenseAccountId, s.FreightExpenseAccountId, s.DiscountAccountId,
        s.DefaultPurchaseTaxCodeId, s.DocumentStartDate, s.MatchingConfirmedAt
    };

    internal static IntegrationSettingsModel ToModel(IntegrationSettings s) => new()
    {
        Mode                     = s.Mode.ToString(),
        AutoPushCustomers        = s.AutoPushCustomers,
        AutoPushVendors          = s.AutoPushVendors,
        AutoPushItems            = s.AutoPushItems,
        AutoPushSalesInvoices    = s.AutoPushSalesInvoices,
        AutoPushBills            = s.AutoPushBills,
        AutoPushPosSales         = s.AutoPushPosSales,
        ItemTypeDefault         = s.ItemTypeDefault.ToString(),
        PartnerScope             = s.PartnerScope.ToString(),
        DefaultIncomeAccountId   = s.DefaultIncomeAccountId,
        DefaultExpenseAccountId  = s.DefaultExpenseAccountId,
        FreightExpenseAccountId  = s.FreightExpenseAccountId,
        DiscountAccountId        = s.DiscountAccountId,
        DefaultPurchaseTaxCodeId = s.DefaultPurchaseTaxCodeId,
        DocumentStartDate        = s.DocumentStartDate,
        MatchingConfirmedAt      = s.MatchingConfirmedAt
    };
}
