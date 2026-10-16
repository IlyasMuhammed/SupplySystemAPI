using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>
/// A44 D-50: the sync-control decision while CTRL's <c>integration.SyncControlConfigs</c> reader is not registered —
/// read from the six AutoPush* switches exactly as the gateway did before A44, so behaviour is identical. Registered
/// with TryAdd: CTRL's reader replaces it (register it with AddScoped, or TryAdd before AddIntegrationGateway).
/// <list type="bullet">
/// <item>Customer / Vendor / Item / SalesInvoice / PurchaseBill: their own switch; off = Skip.</item>
/// <item>POSSale / POSReturn: "Sync POS sales" — on (with sales invoices on) = Process; off = Hold (sales invoices on)
/// or Skip (sales invoices off), as PosSales did.</item>
/// <item>The A44 kinds have no switch of their own here: Payment / CreditNote follow sales invoices; BillPayment /
/// VendorCredit follow bills.</item>
/// </list>
/// </summary>
internal sealed class LegacySyncControlReader : ISyncControlReader
{
    private readonly IntegrationDbContext _db;

    public LegacySyncControlReader(IntegrationDbContext db) => _db = db;

    public async Task<SyncControlDecision> CheckAsync(int connectionId, string documentType, CancellationToken ct = default)
    {
        var settings = await SettingsAsync(connectionId, ct);
        return settings is null ? SyncControlDecision.Enabled : Decide(settings, documentType);
    }

    public async Task<IReadOnlyDictionary<string, SyncControlDecision>> GetAllAsync(int connectionId, CancellationToken ct = default)
    {
        var settings = await SettingsAsync(connectionId, ct);
        return SyncControlDocumentTypes.All.ToDictionary(
            t => t, t => settings is null ? SyncControlDecision.Enabled : Decide(settings, t), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A settings row created (or changed) in this unit of work wins over the stored one.</summary>
    private async Task<IntegrationSettings?> SettingsAsync(int connectionId, CancellationToken ct) =>
        _db.Settings.Local.FirstOrDefault(s => s.ConnectionId == connectionId)
        ?? await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.ConnectionId == connectionId, ct);

    public static SyncControlDecision Decide(IntegrationSettings s, string documentType) => documentType switch
    {
        SyncControlDocumentTypes.Customer     => new(s.AutoPushCustomers, false),
        SyncControlDocumentTypes.Vendor       => new(s.AutoPushVendors, false),
        SyncControlDocumentTypes.Item         => new(s.AutoPushItems, false),
        SyncControlDocumentTypes.SalesInvoice => new(s.AutoPushSalesInvoices, false),
        SyncControlDocumentTypes.CreditNote   => new(s.AutoPushSalesInvoices, false),
        SyncControlDocumentTypes.Payment      => new(s.AutoPushSalesInvoices, false),
        SyncControlDocumentTypes.PosSale      => new(s.AutoPushSalesInvoices && s.AutoPushPosSales, s.AutoPushSalesInvoices),
        SyncControlDocumentTypes.PosReturn    => new(s.AutoPushSalesInvoices && s.AutoPushPosSales, s.AutoPushSalesInvoices),
        SyncControlDocumentTypes.PurchaseBill => new(s.AutoPushBills, false),
        SyncControlDocumentTypes.BillPayment  => new(s.AutoPushBills, false),
        SyncControlDocumentTypes.VendorCredit => new(s.AutoPushBills, false),
        _                                     => SyncControlDecision.Enabled
    };
}

/// <summary>Until CTRL's provider-defaults reader is registered: no defaults (nothing extra is sent).</summary>
internal sealed class EmptyProviderDefaultsReader : IProviderDefaultsReader
{
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    public Task<IReadOnlyDictionary<string, string>> GetAsync(int connectionId, string documentType, CancellationToken ct = default) =>
        Task.FromResult(None);
}

/// <summary>
/// The effective D-15 provider defaults of one document type, with the D-16 account fallbacks (A42 CoA → QuickBooks
/// account mappings of DEFAULT_CASH / DEFAULT_BANK / DEFAULT_PURCHASE). Everything is a QuickBooks id or null (= not
/// sent). <b>Class and Department are taken as the reader gives them</b>: the reader returns them only when the company's
/// preferences track classes / locations (D-55).
/// </summary>
internal sealed class DocumentDefaults
{
    public static readonly DocumentDefaults None = new(new Dictionary<string, string>(), new Dictionary<string, string>());

    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly IReadOnlyDictionary<string, string> _accountsByDefaultKey;

    public DocumentDefaults(IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, string> accountsByDefaultKey)
    {
        _values               = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        _accountsByDefaultKey = accountsByDefaultKey;
    }

    public string? ClassId           => Get(ProviderDefaultKeys.ClassRef);
    public string? DepartmentId      => Get(ProviderDefaultKeys.DepartmentRef);
    public string? SalesTermId       => Get(ProviderDefaultKeys.SalesTermRef);
    public string? PosCustomerId     => Get(ProviderDefaultKeys.PosDefaultCustomerRef);

    public string? PaymentMethodId(string? method) => ForMethod(ProviderDefaultKeys.PaymentMethodRef, method);

    /// <summary>Customer receipts. Null: QuickBooks puts them in Undeposited Funds, its own default.</summary>
    public string? DepositToAccountId(string? method) => ForMethod(ProviderDefaultKeys.DepositToAccountRef, method) ?? CashOrBank(method);

    /// <summary>Bill payments: the bank (or, for CARD, credit-card) account paid from.</summary>
    public string? BankAccountId(string? method) => ForMethod(ProviderDefaultKeys.BankAccountRef, method) ?? CashOrBank(method);

    /// <summary>Refund receipts: the account refunds are paid from (the method's own, else the general one).</summary>
    public string? RefundFromAccountId(string? method) =>
        ForMethod(ProviderDefaultKeys.RefundFromAccountRef, method) ?? Get(ProviderDefaultKeys.RefundFromAccountRef)
        ?? ForMethod(ProviderDefaultKeys.DepositToAccountRef, method) ?? CashOrBank(method);

    /// <summary>D-16: an A42 default key's mapped QuickBooks account (e.g. DEFAULT_PURCHASE), or null.</summary>
    public string? MappedAccount(string defaultKey) =>
        _accountsByDefaultKey.TryGetValue(defaultKey, out var id) && !string.IsNullOrWhiteSpace(id) ? id.Trim() : null;

    private string? CashOrBank(string? method) =>
        MappedAccount(string.Equals(method?.Trim(), PaymentMethodCodes.Cash, StringComparison.OrdinalIgnoreCase) ? "DEFAULT_CASH" : "DEFAULT_BANK");

    private string? ForMethod(string key, string? method) =>
        string.IsNullOrWhiteSpace(method) ? null : Get(ProviderDefaultKeys.ForMethod(key, method));

    private string? Get(string key) =>
        _values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>Loads <see cref="DocumentDefaults"/>. Never throws: a reader failure means "no defaults" (logged).</summary>
internal interface IDocumentDefaultsSource
{
    Task<DocumentDefaults> GetAsync(int connectionId, string documentType, CancellationToken ct = default);
}

internal sealed class DocumentDefaultsSource : IDocumentDefaultsSource
{
    private readonly IProviderDefaultsReader         _reader;
    private readonly IServiceProvider                _services;
    private readonly ILogger<DocumentDefaultsSource> _logger;
    private IReadOnlyDictionary<string, string>?     _accounts;

    public DocumentDefaultsSource(IProviderDefaultsReader reader, IServiceProvider services, ILogger<DocumentDefaultsSource> logger)
    {
        _reader   = reader;
        _services = services;
        _logger   = logger;
    }

    public async Task<DocumentDefaults> GetAsync(int connectionId, string documentType, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, string> values;
        try
        {
            values = await _reader.GetAsync(connectionId, documentType, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "QuickBooks connection {ConnectionId}: provider defaults for {DocumentType} could not be read; none are applied.",
                connectionId, documentType);
            values = new Dictionary<string, string>();
        }

        return new DocumentDefaults(values, await AccountsAsync(ct));
    }

    /// <summary>The A42 mappings, once per scope. Empty without Accounting (or when it fails).</summary>
    private async Task<IReadOnlyDictionary<string, string>> AccountsAsync(CancellationToken ct)
    {
        if (_accounts is not null) return _accounts;

        var reader = _services.GetService<IExternalAccountMapReader>();
        if (reader is null) return _accounts = new Dictionary<string, string>();
        try
        {
            return _accounts = (await reader.GetAsync(ExternalSystemTypes.QuickBooksOnline, ct)).ByDefaultKey;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "QuickBooks: the chart-of-accounts mappings could not be read; account fallbacks are not applied.");
            return _accounts = new Dictionary<string, string>();
        }
    }
}
