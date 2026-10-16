using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Matching;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Integration.Core.Settings;

/// <summary>The Accounts tab (A44 D-16): the general ledger's chart against QuickBooks' accounts.</summary>
public interface IQuickBooksAccountMappingService
{
    Task<AccountMappingsModel> GetAsync(CancellationToken ct = default);
    Task<AutoMapAccountsResult> AutoMapAsync(AutoMapAccountsRequest request, int userId, CancellationToken ct = default);
    Task<AccountMappingsModel> SaveAsync(SaveAccountMappingsRequest request, int userId, CancellationToken ct = default);
}

/// <summary>
/// The mappings themselves live in Accounting (A42 <c>finance.ExternalSystemMappings</c>, QUICKBOOKS_ONLINE, external
/// code = QuickBooks account id) and are read and written only through <see cref="IExternalAccountMapReader"/>; QuickBooks'
/// accounts come from the reference snapshot. Without the general ledger (the Null reader) everything is empty and saving
/// is refused. Changes are audited here too (who, from which screen), alongside Accounting's own record.
/// </summary>
internal sealed class AccountMappingService : IQuickBooksAccountMappingService
{
    public const string RuleAcctNum        = "AcctNum";
    public const string RuleExactName      = "ExactName";
    public const string RuleNormalizedName = "NormalizedName";

    private static readonly Dictionary<string, string> UsedFor = new(StringComparer.OrdinalIgnoreCase)
    {
        [EffectiveAccountsResolver.DefaultRevenue]       = "Default income account (when the setting is empty)",
        [EffectiveAccountsResolver.DefaultCogs]          = "Default expense account (when the setting is empty)",
        [EffectiveAccountsResolver.DefaultPurchase]      = "Freight / expense account (when the setting is empty)",
        [EffectiveAccountsResolver.DefaultDiscountGiven] = "Discount account (when the setting is empty)",
        ["DEFAULT_CASH"]                                 = "Cash receipts / payments (when no deposit or bank account is chosen)",
        ["DEFAULT_BANK"]                                 = "Other receipts / payments (when no deposit or bank account is chosen)"
    };

    private readonly IntegrationDbContext            _db;
    private readonly IConnectionAccessor             _accessor;
    private readonly IReferenceDataReader            _reference;
    private readonly IServiceProvider                _services;

    public AccountMappingService(
        IntegrationDbContext db, IConnectionAccessor accessor, IReferenceDataReader reference, IServiceProvider services)
    {
        _db        = db;
        _accessor  = accessor;
        _reference = reference;
        _services  = services;
    }

    /// <summary>A mapped default (cash, bank, revenue…) can be what a Blocked record waited for (D-16 fallbacks).</summary>
    private Task RevalidateBlockedAsync(IntegrationConnection connection, CancellationToken ct) =>
        Sync.BlockedRecordRevalidator.AfterChangeAsync(_services, connection,
            _services.GetService<Microsoft.Extensions.Logging.ILogger<AccountMappingService>>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountMappingService>.Instance, ct);

    private IExternalAccountMapReader Ledger => _services.GetService<IExternalAccountMapReader>() ?? new NullExternalAccountMapReader();

    // ── Read ────────────────────────────────────────────────────────────────────────────────

    public async Task<AccountMappingsModel> GetAsync(CancellationToken ct = default)
    {
        var connection = await _accessor.GetCurrentAsync(ct);
        var data       = connection is null ? null : await _reference.GetCachedAsync(connection.Id, ct);
        var fetchedAt  = connection is null ? null : await FetchedAtAsync(connection.Id, ct);

        var leaves = await Ledger.ListLeafAccountsAsync(ct);
        var map    = await Ledger.GetAsync(ExternalSystemTypes.QuickBooksOnline, ct);
        var qbo    = (data?.Accounts ?? []).ToDictionary(a => a.Id, StringComparer.Ordinal);

        // Which default keys point at which leaf: the default's external account equals the leaf's mapping.
        var defaultsByCode = map.ByAccountCode.Values
            .ToDictionary(e => e.AccountCode, e => map.ByDefaultKey.Where(d => d.Value == e.ExternalAccountCode).Select(d => d.Key).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var rows = leaves.OrderBy(l => l.Code, StringComparer.OrdinalIgnoreCase).Select(leaf =>
        {
            map.ByAccountCode.TryGetValue(leaf.Code, out var entry);
            var external = entry?.ExternalAccountCode;
            RemoteAccount? account = external is not null && qbo.TryGetValue(external, out var a) ? a : null;

            var status = external is null ? "Unmapped"
                       : data is not null && account is not { Active: true } ? "Invalid"
                       : "Mapped";

            return new AccountMappingRowModel
            {
                AccountUuid    = leaf.AccountUuid,
                Code           = leaf.Code,
                Name           = leaf.Name,
                AccountType    = leaf.AccountType,
                IsActive       = leaf.IsActive,
                QboAccountId   = external,
                QboAccountName = account?.Name ?? entry?.ExternalAccountName,
                QboAccountType = account?.AccountType,
                Status         = status,
                DefaultKeys    = external is not null && defaultsByCode.TryGetValue(leaf.Code, out var keys) ? keys : []
            };
        }).ToList();

        var mappedCounts = rows.Where(r => r.QboAccountId is not null).GroupBy(r => r.QboAccountId!).ToDictionary(g => g.Key, g => g.Count());

        return new AccountMappingsModel
        {
            GlAvailable        = leaves.Count > 0,
            ReferenceFetchedAt = fetchedAt,
            TotalAccounts      = rows.Count,
            Mapped             = rows.Count(r => r.Status == "Mapped"),
            Unmapped           = rows.Count(r => r.Status == "Unmapped"),
            Invalid            = rows.Count(r => r.Status == "Invalid"),
            Accounts           = rows,
            QboAccounts        = (data?.Accounts ?? []).OrderBy(a => a.AccountType).ThenBy(a => a.Name)
                .Select(a => ToModel(a, mappedCounts.GetValueOrDefault(a.Id))).ToList(),
            DefaultRoles       = map.ByDefaultKey.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => new DefaultRoleMappingModel
            {
                DefaultKey     = d.Key,
                QboAccountId   = d.Value,
                QboAccountName = qbo.TryGetValue(d.Value, out var a) ? a.Name : null,
                UsedFor        = UsedFor.GetValueOrDefault(d.Key)
            }).ToList()
        };
    }

    // ── Auto-map ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// D-16: for each SCM leaf (unmapped, unless <see cref="AutoMapAccountsRequest.Overwrite"/>), the active QuickBooks
    /// account whose account number is the leaf's code, else whose name is the leaf's name (case and spacing ignored),
    /// else whose name matches with punctuation removed — taken only when exactly one QuickBooks account matches at that
    /// rule. Ambiguous ones are returned for a person. Never removes a mapping.
    /// </summary>
    public async Task<AutoMapAccountsResult> AutoMapAsync(AutoMapAccountsRequest request, int userId, CancellationToken ct = default)
    {
        request ??= new AutoMapAccountsRequest();
        var connection = await RequireConnectionAsync(ct);
        var data       = await RequireReferenceAsync(connection.Id, ct);
        var leaves     = await RequireLeavesAsync(ct);
        var map        = await Ledger.GetAsync(ExternalSystemTypes.QuickBooksOnline, ct);

        var active  = data.Accounts.Where(a => a.Active).ToList();
        var byNum   = active.Where(a => !string.IsNullOrWhiteSpace(a.AcctNum)).ToLookup(a => a.AcctNum!.Trim().ToUpperInvariant());
        var byExact = active.ToLookup(a => MatchNormalizer.Exact(a.Name));
        var byLoose = active.ToLookup(a => Normalized(a.Name));

        var result = new AutoMapAccountsResult();
        var rows   = new List<AccountMappingUpsert>();

        foreach (var leaf in leaves.OrderBy(l => l.Code, StringComparer.OrdinalIgnoreCase))
        {
            if (!leaf.IsActive && !request.IncludeInactive) continue;
            if (!request.Overwrite && map.ByAccountCode.ContainsKey(leaf.Code)) continue;

            var tiers = new (string Rule, List<RemoteAccount> Matches)[]
            {
                (RuleAcctNum,        byNum[leaf.Code.Trim().ToUpperInvariant()].ToList()),
                (RuleExactName,      byExact[MatchNormalizer.Exact(leaf.Name)].ToList()),
                (RuleNormalizedName, byLoose[Normalized(leaf.Name)].ToList())
            };

            var decided = false;
            foreach (var (rule, matches) in tiers)
            {
                if (matches.Count == 0) continue;
                if (matches.Count > 1)
                {
                    result.Ambiguous.Add(new AmbiguousAccountModel
                    {
                        AccountUuid = leaf.AccountUuid, Code = leaf.Code, Name = leaf.Name, Rule = rule,
                        Candidates  = matches.Select(m => ToModel(m, 0)).ToList()
                    });
                    decided = true;
                    break;
                }

                var account = matches[0];
                if (map.ByAccountCode.TryGetValue(leaf.Code, out var current) && current.ExternalAccountCode == account.Id)
                {
                    decided = true;   // already exactly this
                    break;
                }

                rows.Add(new AccountMappingUpsert(leaf.AccountUuid, account.Id, account.Name, $"AUTO_MAPPED:{rule}"));
                result.Matches.Add(new AutoMappedAccountModel
                {
                    AccountUuid = leaf.AccountUuid, Code = leaf.Code, Name = leaf.Name,
                    QboAccountId = account.Id, QboAccountName = account.Name, Rule = rule
                });
                decided = true;
                break;
            }

            // Names that are empty after normalizing match nothing meaningful.
            if (!decided) result.Unmatched++;
        }

        if (rows.Count > 0)
        {
            result.Mapped = await Ledger.UpsertMappingsAsync(ExternalSystemTypes.QuickBooksOnline, rows, ct);
            IntegrationAudit.Add(_db, connection.Id, AuditAreas.AccountMapping, "AutoMapped", null,
                result.Matches.Select(m => new { m.Code, m.QboAccountId, m.Rule }).ToList(), userId);
            await _db.SaveChangesAsync(ct);
            await RevalidateBlockedAsync(connection, ct);
        }

        result.Accounts = await GetAsync(ct);
        return result;
    }

    // ── Save ────────────────────────────────────────────────────────────────────────────────

    public async Task<AccountMappingsModel> SaveAsync(SaveAccountMappingsRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = await RequireConnectionAsync(ct);
        var items      = request.Mappings ?? [];
        if (items.Count == 0) throw new BadRequestException("Send at least one mapping.");
        if (items.Any(i => i is null)) throw new BadRequestException("A mapping row is empty.");

        var duplicate = items.GroupBy(i => i.AccountUuid).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new BadRequestException($"Account {duplicate.Key} appears more than once.");

        var leaves = (await RequireLeavesAsync(ct)).ToDictionary(l => l.AccountUuid);
        RemoteReferenceData? data = null;
        if (items.Any(i => !string.IsNullOrWhiteSpace(i.QboAccountId)))
            data = await RequireReferenceAsync(connection.Id, ct);

        var rows = new List<AccountMappingUpsert>();
        foreach (var item in items)
        {
            if (!leaves.TryGetValue(item.AccountUuid, out var leaf))
                throw new BadRequestException($"Account {item.AccountUuid} is not a posting (leaf) account of this organization's chart.");

            var id = string.IsNullOrWhiteSpace(item.QboAccountId) ? null : item.QboAccountId.Trim();
            if (id is null)
            {
                rows.Add(new AccountMappingUpsert(leaf.AccountUuid, null, null, null));
                continue;
            }

            var account = data!.Accounts.FirstOrDefault(a => a.Id == id)
                ?? throw new BadRequestException(
                       $"QuickBooks account '{id}' (for {leaf.Code} {leaf.Name}) is not in the reference data. Refresh it, then choose again.");
            if (!account.Active)
                throw new BadRequestException($"QuickBooks account '{account.Name}' (for {leaf.Code} {leaf.Name}) is inactive.");

            rows.Add(new AccountMappingUpsert(leaf.AccountUuid, account.Id, account.Name, "MANUAL"));
        }

        var changed = await Ledger.UpsertMappingsAsync(ExternalSystemTypes.QuickBooksOnline, rows, ct);
        IntegrationAudit.Add(_db, connection.Id, AuditAreas.AccountMapping, "Saved", null,
            new { Changed = changed, Rows = rows.Select(r => new { r.AccountUuid, r.ExternalAccountCode }).ToList() }, userId);
        await _db.SaveChangesAsync(ct);
        await RevalidateBlockedAsync(connection, ct);

        return await GetAsync(ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Letters and digits only, upper case ("Sales - Retail" = "SALES RETAIL" = "salesretail").</summary>
    internal static string Normalized(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
        return sb.ToString();
    }

    private static QboAccountModel ToModel(RemoteAccount a, int mapped) => new()
    {
        Id = a.Id, Name = a.Name, AcctNum = a.AcctNum, Type = a.AccountType, SubType = a.AccountSubType, Active = a.Active, MappedCount = mapped
    };

    private async Task<IReadOnlyList<ChartAccountRef>> RequireLeavesAsync(CancellationToken ct)
    {
        var leaves = await Ledger.ListLeafAccountsAsync(ct);
        if (leaves.Count == 0)
            throw new ConflictException(
                "The general ledger has no chart of accounts for this organization (or is not licensed), so there is nothing to map.");
        return leaves;
    }

    private async Task<RemoteReferenceData> RequireReferenceAsync(int connectionId, CancellationToken ct) =>
        await _reference.GetCachedAsync(connectionId, ct)
        ?? throw new BadRequestException("QuickBooks' accounts have not been loaded yet. Refresh the reference data first.");

    private async Task<IntegrationConnection> RequireConnectionAsync(CancellationToken ct) =>
        await _accessor.GetCurrentAsync(ct)
        ?? throw new ConflictException("Connect QuickBooks first — accounts are mapped to a connected company.");

    private Task<DateTime?> FetchedAtAsync(int connectionId, CancellationToken ct) =>
        _db.ReferenceSnapshots.Where(s => s.ConnectionId == connectionId && s.Kind == ReferenceKinds.Accounts)
            .Select(s => (DateTime?)s.FetchedAt).FirstOrDefaultAsync(ct);
}
