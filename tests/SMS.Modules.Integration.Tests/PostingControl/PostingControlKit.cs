using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Matching;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Tests.Connections;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Modules.Integration.Tests.Setup;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Integration.Tests.PostingControl;

// A44 CTRL tests: the gateway + sync engine harness plus everything CTRL registers (the module's own
// AddIntegrationSettingsReaders and admin services), with the reference data read from stored snapshots (as in the app),
// a fake general ledger and a fake held-record releaser.

internal static class PostingControlKit
{
    public static SyncHarness Harness(FakeAccountMap? ledger = null, FakeReleaser? releaser = null, Action<IServiceCollection>? extra = null,
                                      string? sqlConnectionString = null) =>
        new(sqlConnectionString: sqlConnectionString, configure: s =>
        {
            s.AddScoped<ReferenceDataService>();
            s.RemoveAll<IReferenceDataReader>();
            s.AddScoped<IReferenceDataReader>(sp => sp.GetRequiredService<ReferenceDataService>());
            s.AddScoped<IPreflightService, PreflightService>();
            s.AddSingleton<IBaseCurrencyResolver>(new FixedBaseCurrency(true, "PKR"));
            s.AddScoped<IIntegrationSettingsService, IntegrationSettingsService>();
            s.AddIntegrationSettingsReaders();
            s.AddScoped<IQuickBooksAccountMappingService, AccountMappingService>();
            s.AddScoped<IQuickBooksRemoteEntityService, RemoteEntityService>();
            s.AddScoped<IQuickBooksCoverageService, CoverageService>();
            if (ledger is not null) s.AddSingleton<IExternalAccountMapReader>(ledger);
            if (releaser is not null) s.AddSingleton<IHeldRecordReleaser>(releaser);
            extra?.Invoke(s);
        });

    public static async Task SeedReferenceAsync(SyncHarness h, IntegrationConnection connection, RemoteReferenceData data)
    {
        await using var db = h.DbAs(h.OrgId);

        void Put(string kind, object? value) => db.ReferenceSnapshots.Add(new ReferenceSnapshot
        {
            OrganizationId = h.OrgId,
            ConnectionId   = connection.Id,
            Kind           = kind,
            Json           = JsonSerializer.Serialize(value, ReferenceDataService.Json),
            FetchedAt      = h.Now
        });

        Put(ReferenceKinds.Accounts,       data.Accounts);
        Put(ReferenceKinds.TaxCodes,       data.TaxCodes);
        Put(ReferenceKinds.Terms,          data.Terms);
        Put(ReferenceKinds.Currencies,     data.Currencies);
        Put(ReferenceKinds.Preferences,    data.Preferences);
        Put(ReferenceKinds.CompanyInfo,    data.CompanyInfo);
        Put(ReferenceKinds.Classes,        data.Classes);
        Put(ReferenceKinds.Departments,    data.Departments);
        Put(ReferenceKinds.PaymentMethods, data.PaymentMethods);
        await db.SaveChangesAsync();
    }

    /// <summary>The sample company plus classes, locations and payment methods (tracking on unless said otherwise).</summary>
    public static RemoteReferenceData Reference(bool? classTracking = true, bool? departmentTracking = true)
    {
        var s = SampleReference.Data();
        return new RemoteReferenceData
        {
            Accounts       = s.Accounts.Append(new RemoteAccount("9", "Undeposited Funds", "Other Current Asset", null, "Asset", "PKR", true))
                                       .Append(new RemoteAccount("30", "Visa Card", "Credit Card", null, "Liability", "PKR", true))
                                       .ToList(),
            TaxCodes       = s.TaxCodes,
            Terms          = s.Terms,
            Currencies     = s.Currencies,
            Classes        = [new RemoteClass("C1", "Retail", null, true), new RemoteClass("C2", "Old class", null, false)],
            Departments    = [new RemoteDepartment("D1", "Lahore", null, true)],
            PaymentMethods = [new RemotePaymentMethod("PM1", "Cash", "NON_CREDIT_CARD", true), new RemotePaymentMethod("PM2", "Visa", "CREDIT_CARD", true)],
            Preferences    = s.Preferences! with { ClassTrackingPerTxn = classTracking, TrackDepartments = departmentTracking },
            CompanyInfo    = s.CompanyInfo
        };
    }
}

/// <summary>The general ledger's chart and QuickBooks mappings, in memory.</summary>
internal sealed class FakeAccountMap : IExternalAccountMapReader
{
    public List<ChartAccountRef> Leaves { get; } = new();
    /// <summary>Leaf code → (QuickBooks id, name).</summary>
    public Dictionary<string, (string Id, string? Name)> Mapped { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Default key → leaf code.</summary>
    public Dictionary<string, string> Defaults { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentQueue<AccountMappingUpsert> Upserts { get; } = new();

    public ChartAccountRef Leaf(string code, string name, bool active = true)
    {
        var leaf = new ChartAccountRef(Guid.NewGuid(), code, name, "Revenue", active);
        Leaves.Add(leaf);
        return leaf;
    }

    public Task<ExternalAccountMap> GetAsync(string systemType, CancellationToken ct = default)
    {
        var byCode = Mapped.ToDictionary(m => m.Key, m =>
        {
            var leaf = Leaves.First(l => string.Equals(l.Code, m.Key, StringComparison.OrdinalIgnoreCase));
            return new ExternalAccountMapEntry(leaf.AccountUuid, leaf.Code, leaf.Name, m.Value.Id, m.Value.Name);
        }, StringComparer.OrdinalIgnoreCase);
        var byDefault = Defaults.Where(d => Mapped.ContainsKey(d.Value))
            .ToDictionary(d => d.Key, d => Mapped[d.Value].Id, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(new ExternalAccountMap { ByAccountCode = byCode, ByDefaultKey = byDefault });
    }

    public Task<IReadOnlyList<ChartAccountRef>> ListLeafAccountsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ChartAccountRef>>(Leaves.ToList());

    public Task<int> UpsertMappingsAsync(string systemType, IReadOnlyList<AccountMappingUpsert> rows, CancellationToken ct = default)
    {
        var changed = 0;
        foreach (var row in rows)
        {
            Upserts.Enqueue(row);
            var leaf = Leaves.First(l => l.AccountUuid == row.AccountUuid);
            if (row.ExternalAccountCode is null) changed += Mapped.Remove(leaf.Code) ? 1 : 0;
            else { Mapped[leaf.Code] = (row.ExternalAccountCode, row.ExternalAccountName); changed++; }
        }
        return Task.FromResult(changed);
    }
}

/// <summary>Records what sync control asks of the gateway side.</summary>
internal sealed class FakeReleaser : IHeldRecordReleaser
{
    public Dictionary<string, int> Held { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<string> Released { get; } = new();
    public ConcurrentQueue<string> HeldQueued { get; } = new();

    public Task<int> ReleaseAsync(int connectionId, string documentType, CancellationToken ct = default)
    {
        Released.Enqueue(documentType);
        var count = Held.GetValueOrDefault(documentType);
        Held[documentType] = 0;
        return Task.FromResult(count);
    }

    public Task<int> CountHeldAsync(int connectionId, string documentType, CancellationToken ct = default) =>
        Task.FromResult(Held.GetValueOrDefault(documentType));

    public Task<int> HoldQueuedAsync(int connectionId, string documentType, CancellationToken ct = default)
    {
        HeldQueued.Enqueue(documentType);
        return Task.FromResult(2);
    }
}
