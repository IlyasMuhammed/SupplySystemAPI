using System.Text.Json;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Tests.Connections;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Setup;

internal static class SetupTestKit
{
    /// <summary>Stores reference snapshots the way the service does, without a provider call.</summary>
    public static async Task SeedReferenceAsync(ConnectionsHarness h, IntegrationConnection connection, RemoteReferenceData? data = null)
    {
        data ??= SampleReference.Data();
        await using var db = h.OpenAs(connection.OrganizationId);

        void Put(string kind, object? value) => db.ReferenceSnapshots.Add(new ReferenceSnapshot
        {
            OrganizationId = connection.OrganizationId,
            ConnectionId   = connection.Id,
            Kind           = kind,
            Json           = JsonSerializer.Serialize(value, ReferenceDataService.Json),
            FetchedAt      = DateTime.UtcNow
        });

        Put(ReferenceKinds.Accounts,    data.Accounts);
        Put(ReferenceKinds.TaxCodes,    data.TaxCodes);
        Put(ReferenceKinds.Terms,       data.Terms);
        Put(ReferenceKinds.Currencies,  data.Currencies);
        Put(ReferenceKinds.Preferences, data.Preferences);
        Put(ReferenceKinds.CompanyInfo, data.CompanyInfo);
        await db.SaveChangesAsync();
    }

    /// <summary>A payload stored on an EntityMap the way the gateway would.</summary>
    public static async Task SeedPayloadAsync(
        ConnectionsHarness h, IntegrationConnection connection, SyncKind kind, string externalId, object payload,
        JsonSerializerOptions? options = null)
    {
        await using var db = h.OpenAs(connection.OrganizationId);
        db.EntityMaps.Add(new EntityMap
        {
            OrganizationId = connection.OrganizationId,
            ConnectionId   = connection.Id,
            Kind           = kind,
            ExternalId     = externalId,
            DisplayLabel   = externalId,
            PayloadJson    = JsonSerializer.Serialize(payload, payload.GetType(), options ?? new JsonSerializerOptions())
        });
        await db.SaveChangesAsync();
    }

    public static SalesInvoicePayload Invoice(string currency = "PKR", params decimal[] taxRates) => new()
    {
        ExternalId         = Guid.NewGuid().ToString(),
        DocNumber          = "INV-1",
        CustomerExternalId = "c-1",
        TxnDate            = DateTime.UtcNow.Date,
        CurrencyCode       = currency,
        Lines              = taxRates.Select((r, i) => new SalesInvoiceLinePayload
        {
            LineNo = i + 1, ItemExternalId = "v-1", Quantity = 1, UnitPrice = 100, TaxPercent = r
        }).ToList()
    };

    public static BillPayload Bill(string currency = "PKR", params decimal?[] taxRates) => new()
    {
        ExternalId       = Guid.NewGuid().ToString(),
        DocNumber        = "SUP-1",
        VendorExternalId = "v-1",
        TxnDate          = DateTime.UtcNow.Date,
        CurrencyCode     = currency,
        Lines            = taxRates.Select((r, i) => new BillLinePayload { LineNo = i + 1, Amount = 50, TaxPercent = r }).ToList()
    };
}

/// <summary>A base-currency resolver that says whatever the test needs.</summary>
internal sealed class FixedBaseCurrency : IBaseCurrencyResolver
{
    private readonly BaseCurrencyInfo _info;
    public FixedBaseCurrency(bool configured, string? code) => _info = new BaseCurrencyInfo(configured, code);
    public Task<BaseCurrencyInfo> ResolveAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult(_info);
}
