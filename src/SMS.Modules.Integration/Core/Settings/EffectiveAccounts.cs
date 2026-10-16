using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Integration.Core.Settings;

/// <summary>One account in effect and where it came from: <see cref="EffectiveAccounts.FromSettings"/> or <see cref="EffectiveAccounts.FromLedger"/>.</summary>
internal sealed record EffectiveAccount(string? AccountId, string? Source)
{
    public static readonly EffectiveAccount None = new(null, null);
}

/// <summary>The four settings accounts as QuickBooks documents use them (D-16).</summary>
internal sealed record EffectiveAccounts(
    EffectiveAccount Income, EffectiveAccount Expense, EffectiveAccount FreightExpense, EffectiveAccount Discount)
{
    public const string FromSettings = "Settings";
    public const string FromLedger   = "GeneralLedger";
}

/// <summary>
/// A44 D-16, for the QuickBooks object builder (QBO) and preflight: the settings' default income / expense / freight /
/// discount account, or — where that setting is empty — the QuickBooks account the general ledger's matching default is
/// mapped to (A42 ExternalSystemMappings, through <see cref="IExternalAccountMapReader"/>):
/// income ← DEFAULT_REVENUE; expense ← DEFAULT_COGS, else DEFAULT_PURCHASE; freight ← DEFAULT_PURCHASE, else the expense
/// account in effect; discount ← DEFAULT_DISCOUNT_GIVEN. The settings accounts stay authoritative. A mapped account is
/// used only when the reference snapshot has it active and of a type QuickBooks accepts there (when no snapshot is
/// loaded it is passed through and validation catches it). Ambient organization; scoped (the map is read once per scope).
/// </summary>
internal interface IEffectiveAccountsResolver
{
    Task<EffectiveAccounts> ResolveAsync(IntegrationSettings settings, CancellationToken ct = default);
}

internal sealed class EffectiveAccountsResolver : IEffectiveAccountsResolver
{
    public const string DefaultRevenue       = "DEFAULT_REVENUE";
    public const string DefaultCogs          = "DEFAULT_COGS";
    public const string DefaultPurchase      = "DEFAULT_PURCHASE";
    public const string DefaultDiscountGiven = "DEFAULT_DISCOUNT_GIVEN";

    // Same allowed types as the settings screen (IntegrationSettingsService), compared normalized.
    internal static readonly string[] IncomeTypes   = ["income", "otherincome"];
    internal static readonly string[] ExpenseTypes  = ["expense", "otherexpense", "costofgoodssold"];
    internal static readonly string[] DiscountTypes = ["income", "expense", "otherincome", "otherexpense"];

    private readonly IServiceProvider     _services;
    private readonly IReferenceDataReader _reference;
    private ExternalAccountMap?           _map;

    public EffectiveAccountsResolver(IServiceProvider services, IReferenceDataReader reference)
    {
        _services  = services;
        _reference = reference;
    }

    public async Task<EffectiveAccounts> ResolveAsync(IntegrationSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        var income   = Clean(settings.DefaultIncomeAccountId);
        var expense  = Clean(settings.DefaultExpenseAccountId);
        var freight  = Clean(settings.FreightExpenseAccountId);
        var discount = Clean(settings.DiscountAccountId);

        if (income is not null && expense is not null && freight is not null && discount is not null)
            return new(Set(income), Set(expense), Set(freight), Set(discount));

        var map  = await MapAsync(ct);
        var data = settings.ConnectionId == 0 ? null : await _reference.GetCachedAsync(settings.ConnectionId, ct);

        string? Mapped(string[] types, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!map.ByDefaultKey.TryGetValue(key, out var id) || string.IsNullOrWhiteSpace(id)) continue;
                id = id.Trim();
                if (data is null) return id;
                var account = data.Accounts.FirstOrDefault(a => a.Id == id);
                if (account is { Active: true } && types.Contains(IntegrationSettingsService.NormalizeAccountType(account.AccountType)))
                    return id;
            }
            return null;
        }

        var effectiveExpense = expense is not null ? Set(expense) : Ledger(Mapped(ExpenseTypes, DefaultCogs, DefaultPurchase));

        return new EffectiveAccounts(
            income   is not null ? Set(income)   : Ledger(Mapped(IncomeTypes, DefaultRevenue)),
            effectiveExpense,
            freight  is not null ? Set(freight)  : Ledger(Mapped(ExpenseTypes, DefaultPurchase)) is { AccountId: not null } f ? f : effectiveExpense,
            discount is not null ? Set(discount) : Ledger(Mapped(DiscountTypes, DefaultDiscountGiven)));
    }

    private async Task<ExternalAccountMap> MapAsync(CancellationToken ct)
    {
        if (_map is not null) return _map;
        var reader = _services.GetService<IExternalAccountMapReader>();
        try
        {
            _map = reader is null ? new ExternalAccountMap() : await reader.GetAsync(ExternalSystemTypes.QuickBooksOnline, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The general ledger is optional: without it the settings accounts are all there is.
            _map = new ExternalAccountMap();
        }
        return _map;
    }

    private static EffectiveAccount Set(string id) => new(id, EffectiveAccounts.FromSettings);
    private static EffectiveAccount Ledger(string? id) => id is null ? EffectiveAccount.None : new(id, EffectiveAccounts.FromLedger);
}
