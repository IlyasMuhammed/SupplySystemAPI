using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using SMS.Modules.Finance.Models;
using SMS.Modules.Integration.Jobs;
using SMS.Modules.Integration.Models;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Suppliers.Models;
using SMS.Shared.Integration.QuickBooks;
using SMS.Shared.Pagination;
using Xunit;

namespace SMS.Integration.Tests.QuickBooks;

/// <summary>
/// QBI-34: the whole QuickBooks journey through the real API host — an admin connects, sets up, loads in
/// dry run, matches what the accountant already entered, goes live; SCM's own screens then create and
/// change records and QuickBooks receives exactly what it should, once. Only Intuit is faked.
/// <para>Run on its own: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~QuickBooksJourneyTests</c>.</para>
/// </summary>
public sealed class QuickBooksJourneyTests : IClassFixture<QuickBooksWebApplicationFactory>
{
    private const string Base  = "/api/integrations/quickbooks";
    private const string Realm = "9130000000000001";

    private readonly QuickBooksWebApplicationFactory _f;
    private readonly HttpClient _admin;
    private readonly string _marker = $"QBI-{Guid.NewGuid():N}"[..12];

    public QuickBooksJourneyTests(QuickBooksWebApplicationFactory factory)
    {
        _f     = factory;
        _admin = factory.CreateAdminClient();
    }

    [Fact]
    public async Task Connect_set_up_dry_run_match_go_live_and_sync_through_the_real_host()
    {
        // ── 1. The module's own migration ran on startup ─────────────────────────────
        (await Scalar<int>("SELECT COUNT(*) AS N FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'integration'"))
            .Should().Be(14);
        (await _f.QueryAsync("SELECT MigrationId FROM __EFMigrationsHistory WHERE MigrationId LIKE '%QBI_InitialIntegrationSchema'"))
            .Should().ContainSingle("the Integration module migrates itself on API start");

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(_f.AdminAccessToken);
        token.Claims.Select(c => c.Value).Should().Contain(["INTEGRATION_VIEW", "INTEGRATION_MANAGE", "INTEGRATION_SYNC"],
            "the permission seed grants the new codes to System Admin");

        // ── 2. Connect: consent URL → anonymous callback → connected ─────────────────
        var before = await Get<ConnectionStatusModel>("/connection");
        before.AppConfigured.Should().BeTrue();
        before.IsConnected.Should().BeFalse();

        var state = await ConnectAsync("first-code");

        var replay = await CallbackAsync("replayed-code", state);
        replay.Should().Contain("reason=state_used", "a state token is single-use");
        _f.Auth.ExchangedCodes.Should().NotContain("replayed-code");

        var connectionJson = await (await _admin.GetAsync($"{Base}/connection")).Content.ReadAsStringAsync();
        connectionJson.Should().NotContain("AT-first-code").And.NotContain("RT-first-code", "tokens never leave the vault");
        var connected = await Get<ConnectionStatusModel>("/connection");
        connected.Status.Should().Be("NeedsSetup");
        connected.RealmId.Should().Be(Realm);
        connected.CompanyName.Should().Be("E2E Sandbox Co", "company facts are loaded straight after connecting");
        connected.HomeCurrencyCode.Should().Be("PKR");
        (await _f.QueryAsync("SELECT EncryptedAccessToken FROM integration.Connections")).Single()["EncryptedAccessToken"]!
            .ToString().Should().NotContain("AT-first-code", "stored encrypted");

        // ── 3. Setup: reference data, settings (with a refusal), tax mappings, mode gating ──
        var reference = await Get<ReferenceDataModel>("/reference");
        reference.Accounts.Select(a => a.Id).Should().Contain(["1", "2", "3", "4"]);

        var wrongAccount = await _admin.PutAsJsonAsync($"{Base}/settings", Settings(income: "3"));
        wrongAccount.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an expense account cannot be the income account");

        (await Put<IntegrationSettingsModel>("/settings", Settings())).DefaultIncomeAccountId.Should().Be("1");
        await Put<List<TaxCodeMappingModel>>("/tax-mappings", new SaveTaxCodeMappingsRequest
        {
            Mappings = [new() { TaxPercent = 0m, QboTaxCodeId = "NON" }, new() { TaxPercent = 17m, QboTaxCodeId = "STD" }]
        });

        var liveTooEarly = await _admin.PostAsJsonAsync($"{Base}/mode", new SetModeRequest { Mode = "Live" });
        liveTooEarly.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Live needs matching confirmed first");
        (await Post<IntegrationSettingsModel>("/mode", new SetModeRequest { Mode = "DryRun" })).Mode.Should().Be("DryRun");

        // ── 4. SCM's own screens create records; the triggers reach the gateway ───────
        var existing = _f.Company.Seed(SyncKind.Customer, $"{_marker} Existing Customer");
        var customer = await CreatePartnerAsync($"{_marker} Existing Customer", customer: true);
        var vendor   = await CreatePartnerAsync($"{_marker} Steel Vendor", vendor: true);
        var item     = await CreateProductAsync($"{_marker} Widget");

        (await Map(customer)).Should().Match<MapRow>(m => m.Kind == "Customer" && m.State == "NotSynced" && m.SourceSystem == "SCM");
        (await Map(vendor)).Should().Match<MapRow>(m => m.Kind == "Vendor" && m.State == "NotSynced");
        (await Map(item)).Should().Match<MapRow>(m => m.Kind == "Item" && m.State == "NotSynced",
            "under 'only when referenced' a master record is held, not sent");

        // A supplier invoice raised and approved in Finance becomes a bill that waits for its vendor.
        var docNumber = $"B-{Guid.NewGuid():N}"[..10];
        var invoiceUuid = await PostScm<Guid>("/api/finance/invoices", new CreateInvoiceRequest
        {
            SupplierInvoiceNo = docNumber, SupplierId = vendor,
            InvoiceDate = DateTime.UtcNow.Date, ReceivedDate = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(30),
            Currency = "PKR", Subtotal = 500m, TaxAmount = 0m
        });
        var approve = await _admin.PostAsJsonAsync($"/api/finance/invoices/{invoiceUuid}/approve", new ApproveInvoiceRequest());
        approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());

        (await Map(invoiceUuid)).Should().Match<MapRow>(m => m.Kind == "Bill" && m.State == "WaitingOnDependency");
        _f.Company.Writes.Should().BeEmpty("nothing has been sent yet");

        // ── 5. Dry run: the vendor is pulled in, everything is built and validated, nothing is sent ──
        await RunDependenciesAndOutboxAsync(rounds: 3);

        (await Map(vendor)).State.Should().Be("DryRunOk");
        (await Map(invoiceUuid)).State.Should().Be("DryRunOk");
        _f.Company.Writes.Should().BeEmpty("dry run never writes to QuickBooks");

        // ── 6. Matching: the accountant's existing customer is adopted, not duplicated ──
        var scan = await Post<MatchScanResultModel>("/matching/Customer/scan", null);
        scan.Exact.Should().BeGreaterThanOrEqualTo(1);
        var candidate = (await Get<List<MatchCandidateModel>>("/matching/Customer"))
            .Single(c => c.ExternalId == customer.ToString());
        candidate.Confidence.Should().Be("Exact");
        candidate.RemoteId.Should().Be(existing.Id);

        await Post<List<MatchCandidateModel>>("/matching/Customer/confirm", new ConfirmMatchesRequest
        {
            Decisions = [new MatchDecisionItem { CandidateId = candidate.Id, Decision = "Link" }]
        });
        (await Map(customer)).Should().Match<MapRow>(m => m.RemoteId == existing.Id && m.State == "Synced" && m.LinkOrigin == "Adopted");

        await Post<IntegrationSettingsModel>("/matching/complete", new ConfirmMatchingCompleteRequest { Confirmed = true });
        var preflight = await Get<PreflightResultModel>("/preflight");
        preflight.CanGoLive.Should().BeTrue(string.Join("; ", preflight.Checks.Where(c => c.Status == "Fail").Select(c => $"{c.Code}: {c.Message}")));

        (await Post<IntegrationSettingsModel>("/mode", new SetModeRequest { Mode = "Live" })).Mode.Should().Be("Live");
        (await Get<ConnectionStatusModel>("/connection")).Status.Should().Be("Live");

        // ── 7. Live: the vendor, then the bill against it — once each ─────────────────
        await RunDependenciesAndOutboxAsync(rounds: 4);

        var vendorMap = await Map(vendor);
        var billMap   = await Map(invoiceUuid);
        vendorMap.State.Should().Be("Synced");
        billMap.State.Should().Be("Synced");

        var creates = _f.Company.Writes.Where(c => c.Operation == "Create").ToList();
        creates.Select(c => c.Kind).Should().BeEquivalentTo([SyncKind.Vendor, SyncKind.Bill],
            "the adopted customer is never re-created and the unreferenced item is never sent");
        var bill = creates.Single(c => c.Kind == SyncKind.Bill).Entity
            .Should().BeOfType<SMS.Modules.Integration.Core.Providers.RemoteBill>().Subject;
        bill.VendorId.Should().Be(vendorMap.RemoteId);
        bill.DocNumber.Should().Be(docNumber);
        bill.Lines.Sum(l => l.Amount).Should().Be(500m);
        bill.Lines.Should().OnlyContain(l => l.AccountId == "3", "a line with no item posts to the default expense account");
        _f.Company.Records(SyncKind.Customer).Should().ContainSingle(r => r.Name == $"{_marker} Existing Customer");

        _f.Company.ResetCalls();
        await RunDependenciesAndOutboxAsync(rounds: 2);
        _f.Company.Writes.Should().BeEmpty("an unchanged record is a fingerprint no-op");

        // ── 8. An edit in SCM becomes a sparse update with the current SyncToken ──────
        await RenamePartnerAsync(vendor, $"{_marker} Steel Vendor Renamed");
        (await Map(vendor)).State.Should().Be("Pending");
        await RunDependenciesAndOutboxAsync(rounds: 2);

        var update = _f.Company.Writes.Should().ContainSingle().Subject;
        update.Operation.Should().Be("Update");
        update.Kind.Should().Be(SyncKind.Vendor);
        update.SyncToken.Should().Be("0");
        update.Name.Should().Be($"{_marker} Steel Vendor Renamed");

        // ── 9. The grant is revoked inside QuickBooks: stop, suspend, never burn retries ──
        _f.Company.ResetCalls();
        _f.Company.RevokeOnNextWrite = true;
        await RenamePartnerAsync(vendor, $"{_marker} Steel Vendor Final");
        await RunDependenciesAndOutboxAsync(rounds: 2);

        (await Get<ConnectionStatusModel>("/connection")).Status.Should().Be("Revoked");
        (await Scalar<int>("SELECT COUNT(*) AS N FROM integration.Outbox WHERE Status = 'Suspended'")).Should().BeGreaterThan(0);
        _f.Company.Writes.Should().ContainSingle("one refused call, no retries against a revoked grant");

        // ── 10. Reconnecting the same company resumes everything ──────────────────────
        await ConnectAsync("second-code");
        (await Get<ConnectionStatusModel>("/connection")).Status.Should().Be("Live", "matching was confirmed for this company");
        (await Scalar<int>("SELECT COUNT(*) AS N FROM integration.Outbox WHERE Status = 'Suspended'")).Should().Be(0);

        _f.Company.ResetCalls();
        await RunDependenciesAndOutboxAsync(rounds: 2);
        _f.Company.Writes.Should().ContainSingle(c => c.Operation == "Update" && c.Name == $"{_marker} Steel Vendor Final");
        (await Map(vendor)).State.Should().Be("Synced");

        // ── 11. Dashboard, logs and badges ────────────────────────────────────────────
        var summary = await Get<SyncSummaryModel>("/sync/summary");
        summary.Mode.Should().Be("Live");
        summary.Kinds.Should().Contain(k => k.Kind == "Vendor");

        var items = await Get<PaginatedResponse<SyncItemModel>>("/sync/items?kind=Vendor&page=1&pageSize=10");
        var vendorItem = items.Data.Should().ContainSingle(i => i.ExternalId == vendor.ToString()).Subject;
        vendorItem.RemoteId.Should().Be(vendorMap.RemoteId);
        vendorItem.DeepLink.Should().NotBeNullOrEmpty();

        var logJson = await (await _admin.GetAsync($"{Base}/sync/items/{vendorItem.Id}/log")).Content.ReadAsStringAsync();
        logJson.Should().Contain("Create");
        foreach (var secret in new[] { "AT-first-code", "RT-first-code", "AT-second-code", "RT-second-code" })
            logJson.Should().NotContain(secret);

        var lookup = await Post<StatusLookupResult>("/status/lookup", new StatusLookupRequest { Kind = "Bill", ExternalIds = [invoiceUuid.ToString()] });
        lookup.Items.Should().ContainSingle().Which.State.Should().Be(SyncState.Synced);

        var stored = await _f.QueryAsync("SELECT RequestJson, ResponseJson FROM integration.SyncLog");
        stored.Should().NotBeEmpty();
        stored.SelectMany(r => r.Values).OfType<string>()
            .Should().NotContain(v => v.Contains("AT-first-code") || v.Contains("AT-second-code"), "no token reaches the sync log");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private sealed record MapRow(string Kind, string State, string SourceSystem, string? RemoteId, string? LinkOrigin);

    private async Task<MapRow> Map(Guid externalId)
    {
        var row = (await _f.QueryAsync(
                "SELECT Kind, State, SourceSystem, RemoteId, LinkOrigin FROM integration.EntityMaps WHERE ExternalId = @id",
                ("@id", externalId.ToString())))
            .Should().ContainSingle($"exactly one map for {externalId}").Subject;
        return new MapRow((string)row["Kind"]!, (string)row["State"]!, (string)row["SourceSystem"]!,
            row["RemoteId"] as string, row["LinkOrigin"] as string);
    }

    private async Task<T> Scalar<T>(string sql) =>
        (T)Convert.ChangeType((await _f.QueryAsync(sql)).Single().Values.Single()!, typeof(T));

    private async Task<string> ConnectAsync(string code)
    {
        var consent = await Post<ConnectResponse>("/connect", null);
        var state = Uri.UnescapeDataString(new Uri(consent.ConsentUrl).Query.TrimStart('?').Split('&')
            .Single(p => p.StartsWith("state=")).Substring("state=".Length));

        var location = await CallbackAsync(code, state);
        location.Should().Be(QuickBooksWebApplicationFactory.ReturnUrl + "?result=connected");
        return state;
    }

    private async Task<string> CallbackAsync(string code, string state)
    {
        using var anonymous = _f.CreateAnonymousClient();
        var response = await anonymous.GetAsync(
            $"{Base}/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}&realmId={Realm}");
        ((int)response.StatusCode).Should().BeOneOf(new[] { 301, 302, 303, 307 }, "the callback always redirects the browser");
        return response.Headers.Location!.ToString();
    }

    private static UpdateIntegrationSettingsRequest Settings(string income = "1") => new()
    {
        DefaultIncomeAccountId  = income,
        DefaultExpenseAccountId = "3",
        FreightExpenseAccountId = "4",
        DiscountAccountId       = "2",
        ItemTypeDefault         = "NonInventory",
        PartnerScope            = "OnlyWhenReferenced"
    };

    private async Task<Guid> CreatePartnerAsync(string name, bool customer = false, bool vendor = false) =>
        await PostScm<Guid>("/api/partners", new BusinessPartnerModel
        {
            PartnerCode = $"Q{Guid.NewGuid():N}"[..10], CompanyName = name, IsCustomer = customer, IsVendor = vendor
        });

    private async Task RenamePartnerAsync(Guid uuid, string name)
    {
        var partner = await _f.ReadResultAsync<BusinessPartnerModel>(await _admin.GetAsync($"/api/partners/{uuid}"));
        partner.CompanyName = name;
        var put = await _admin.PutAsJsonAsync($"/api/partners/{uuid}", partner);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
    }

    private async Task<Guid> CreateProductAsync(string name)
    {
        var created = await PostScm<JsonElement>("/api/products",
            new CreateProductRequest { Name = name, PurchasePrice = 10m, SellingPrice = 15m });
        var detail = await _f.ReadResultAsync<ProductDetailModel>(
            await _admin.GetAsync($"/api/products/{created.GetProperty("id").GetInt32()}"));
        return detail.Variants.Single().Uuid;
    }

    private async Task RunDependenciesAndOutboxAsync(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            await _f.RunInScopeAsync<DependencyJob>(job => job.RunOnceAsync());
            await _f.RunInScopeAsync<SyncOutboxJob>(job => job.RunOnceAsync());
        }
    }

    private async Task<T> Get<T>(string path) => await _f.ReadResultAsync<T>(await _admin.GetAsync(Base + path));

    private async Task<T> Post<T>(string path, object? body) =>
        await _f.ReadResultAsync<T>(body is null ? await _admin.PostAsync(Base + path, null) : await _admin.PostAsJsonAsync(Base + path, body));

    private async Task<T> Put<T>(string path, object body) => await _f.ReadResultAsync<T>(await _admin.PutAsJsonAsync(Base + path, body));

    private async Task<T> PostScm<T>(string path, object body) => await _f.ReadResultAsync<T>(await _admin.PostAsJsonAsync(path, body));
}
