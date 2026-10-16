using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Reference;

internal sealed record ReadinessIssue(string Code, string Message, string? DocumentType);

/// <summary>
/// A44 D-27 / D-68 — the provider-default checks shared by preflight and coverage. Reads only what is stored: the
/// sync-control state, the provider defaults (as the builder will get them), the general ledger's DEFAULT_CASH /
/// DEFAULT_BANK mappings (D-16 fallback) and the payment methods of the payments the gateway holds.
/// </summary>
internal static class PostingReadiness
{
    public const string DepositAccounts = "DEPOSIT_ACCOUNTS";
    public const string BankAccounts    = "BANK_ACCOUNTS";
    public const string ClassDefault    = "CLASS_DEFAULT";
    public const string DepartmentDef   = "DEPARTMENT_DEFAULT";

    public static async Task<List<ReadinessIssue>> CheckAsync(
        IServiceProvider services, IntegrationDbContext db, int connectionId, IntegrationSettings settings,
        RemoteReferenceData? data, CancellationToken ct)
    {
        var issues   = new List<ReadinessIssue>();
        var control  = await DecisionsAsync(services, db, connectionId, settings, ct);
        var defaults = services.GetService<IProviderDefaultsReader>();
        var ledger   = await LedgerAsync(services, ct);

        string? Fallback(string method) =>
            ledger.ByDefaultKey.TryGetValue(string.Equals(method, PaymentMethodCodes.Cash, StringComparison.OrdinalIgnoreCase)
                ? "DEFAULT_CASH" : "DEFAULT_BANK", out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;

        async Task<List<string>> MissingAsync(SyncKind kind, string documentType, string key)
        {
            var methods = await MethodsInUseAsync(db, connectionId, kind, ct);
            if (methods.Count == 0) return [];
            var values = defaults is null
                ? new Dictionary<string, string>()
                : await defaults.GetAsync(connectionId, documentType, ct);
            return methods.Where(m => !values.ContainsKey(ProviderDefaultKeys.ForMethod(key, m)) && Fallback(m) is null).ToList();
        }

        if (control[SyncControlDocumentTypes.Payment].IsEnabled)
        {
            var missing = await MissingAsync(SyncKind.Payment, SyncControlDocumentTypes.Payment, ProviderDefaultKeys.DepositToAccountRef);
            if (missing.Count > 0)
                issues.Add(new(DepositAccounts,
                    $"Customer payments by {string.Join(", ", missing)} have no deposit account (Defaults tab, or the general ledger's "
                  + "cash / bank account mapped to QuickBooks), so QuickBooks puts them in Undeposited Funds.",
                    SyncControlDocumentTypes.Payment));
        }

        if (control[SyncControlDocumentTypes.BillPayment].IsEnabled)
        {
            var missing = await MissingAsync(SyncKind.BillPayment, SyncControlDocumentTypes.BillPayment, ProviderDefaultKeys.BankAccountRef);
            if (missing.Count > 0)
                issues.Add(new(BankAccounts,
                    $"Bill payments by {string.Join(", ", missing)} have no account to pay from (Defaults tab, or the general ledger's "
                  + "cash / bank account mapped to QuickBooks). They are refused until one is chosen.",
                    SyncControlDocumentTypes.BillPayment));
        }

        var stored = await db.ProviderDefaults.AsNoTracking()
            .Where(d => d.ConnectionId == connectionId)
            .Select(d => d.SettingKey)
            .ToListAsync(ct);
        bool Has(string key) => stored.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

        if (data?.Preferences?.ClassTrackingEnabled == true && !Has(ProviderDefaultKeys.ClassRef))
            issues.Add(new(ClassDefault,
                "The QuickBooks company tracks classes, but no default class is chosen — documents go without one.", null));
        if (data?.Preferences?.TrackDepartments == true && !Has(ProviderDefaultKeys.DepartmentRef))
            issues.Add(new(DepartmentDef,
                $"The QuickBooks company tracks {(data.Preferences.DepartmentTerminology ?? "location").ToLowerInvariant()}s, but no default "
              + "is chosen — documents go without one.", null));

        return issues;
    }

    /// <summary>Every type's decision, through the registered reader when there is one.</summary>
    public static async Task<IReadOnlyDictionary<string, SyncControlDecision>> DecisionsAsync(
        IServiceProvider services, IntegrationDbContext db, int connectionId, IntegrationSettings? settings, CancellationToken ct)
    {
        if (services.GetService<ISyncControlReader>() is { } reader)
            return await reader.GetAllAsync(connectionId, ct);

        var rows = await db.SyncControlConfigs.AsNoTracking().Where(c => c.ConnectionId == connectionId).ToListAsync(ct);
        return SyncControlDocumentTypes.All.ToDictionary(
            t => t,
            t => SyncControlDefaults.Effective(rows.FirstOrDefault(r => string.Equals(r.DocumentType, t, StringComparison.OrdinalIgnoreCase)), settings, t),
            StringComparer.Ordinal);
    }

    public static async Task<ExternalAccountMap> LedgerAsync(IServiceProvider services, CancellationToken ct)
    {
        if (services.GetService<IExternalAccountMapReader>() is not { } reader) return new ExternalAccountMap();
        try
        {
            return await reader.GetAsync(ExternalSystemTypes.QuickBooksOnline, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ExternalAccountMap();
        }
    }

    /// <summary>The payment methods (upper case) of the stored payloads of a kind that are not voided.</summary>
    private static async Task<List<string>> MethodsInUseAsync(IntegrationDbContext db, int connectionId, SyncKind kind, CancellationToken ct)
    {
        var payloads = await db.EntityMaps.AsNoTracking()
            .Where(m => m.ConnectionId == connectionId && m.Kind == kind && m.PayloadJson != null && m.State != SyncState.Voided)
            .Select(m => m.PayloadJson!)
            .ToListAsync(ct);

        var methods = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var json in payloads)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (string.Equals(p.Name, "method", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                        methods.Add(p.Value.GetString()!.Trim().ToUpperInvariant());
            }
            catch (JsonException)
            {
                // A payload that no longer parses says nothing about methods.
            }
        }
        return methods.ToList();
    }
}
