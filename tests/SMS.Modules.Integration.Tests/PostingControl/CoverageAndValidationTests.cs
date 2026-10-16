using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.PostingControl;

/// <summary>A44 D-27 coverage, dry validation and the new preflight checks — T-50..T-54. Nothing here calls QuickBooks.</summary>
public class CoverageAndValidationTests : IAsyncLifetime
{
    private readonly SyncHarness  _h = PostingControlKit.Harness();
    private IntegrationConnection _connection = null!;

    public async Task InitializeAsync()
    {
        // Accounts that exist in the sample reference data, so preflight passes.
        _connection = await _h.ConnectAsync(s =>
        {
            s.DefaultIncomeAccountId = "1"; s.DefaultExpenseAccountId = "3"; s.FreightExpenseAccountId = "5"; s.DiscountAccountId = "2";
            s.MatchingConfirmedAt = DateTime.UtcNow;
        });
        await PostingControlKit.SeedReferenceAsync(_h, _connection, PostingControlKit.Reference(classTracking: null, departmentTracking: null));
        _h.Source.Has(TestPayloads.Customer());
        _h.Source.Has(TestPayloads.Item());
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<T> Coverage<T>(Func<IQuickBooksCoverageService, Task<T>> call) =>
        _h.Scoped(sp => call(sp.GetRequiredService<IQuickBooksCoverageService>()));

    private Task<ValidationCheckResult> CheckAsync(string kind, string id) =>
        Coverage(c => c.CheckAsync(new ValidationCheckRequest { Kind = kind, ExternalId = id }));

    private async Task StoreAsync(SyncKind kind, string id, string json)
    {
        await using var db = _h.DbAs(_h.OrgId);
        db.EntityMaps.Add(new EntityMap { ConnectionId = _connection.Id, Kind = kind, ExternalId = id, DisplayLabel = id, PayloadJson = json });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task T50_a_record_the_gateway_never_received_is_reported_not_found()
    {
        var result = await CheckAsync("SalesInvoice", "SI-404");

        (result.Found, result.Valid).Should().Be((false, false));
        result.Errors.Should().ContainSingle(e => e.Code == "NOT_RECEIVED");
        _h.Provider.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task T51_validation_reports_every_error_and_every_missing_dependency_at_once()
    {
        await _h.Send(TestPayloads.Invoice(lines: [new TestPayloads.Line("I-1", 1, 100m, Tax: 5m), new TestPayloads.Line("I-2", 1, 50m, Tax: 3m)]));

        var result = await CheckAsync("salesinvoice", "SI-1");

        result.Found.Should().BeTrue();
        result.Valid.Should().BeFalse();
        result.DocumentType.Should().Be("SalesInvoice");
        result.SyncControl.Should().Be("Process");
        string.Join(" ", result.Errors.Select(e => e.Message)).Should().Contain("5%").And.Contain("3%", "both unmapped rates are reported together");
        result.MissingDependencies.Select(d => (d.Kind, d.ExternalId)).Should().Contain([("Customer", "C-1"), ("Item", "I-1")]);
        _h.Provider.WriteCalls.Should().Be(0);
    }

    [Fact]
    public async Task T52_a_sendable_record_is_valid_and_its_POS_type_is_reported()
    {
        // A back-office invoice brings its customer and item into QuickBooks.
        await _h.Send(TestPayloads.Invoice());
        await _h.DrainAsync();
        var pos = TestPayloads.Invoice(id: "POS-1", doc: "INV-P-1");
        pos.SourceType = SalesInvoicePayloadSources.Pos;
        await _h.Send(pos);

        var result = await CheckAsync("SalesInvoice", "POS-1");

        result.Errors.Should().BeEmpty();
        result.MissingDependencies.Should().BeEmpty();
        result.Valid.Should().BeTrue();
        (result.DocumentType, result.SyncControl).Should().Be(("POSSale", "Hold"), "POS sales are off and held by default");
    }

    [Fact]
    public async Task T53_coverage_counts_mapped_pending_and_skipped_records_per_kind()
    {
        await _h.Send(TestPayloads.Customer());
        await _h.Send(TestPayloads.Customer(id: "C-2", name: "Beta"));
        await _h.Send(TestPayloads.Customer(id: "C-3", name: "Gamma"));
        await using (var db = _h.SuperDb())
        {
            var maps = db.EntityMaps.Where(m => m.Kind == SyncKind.Customer).OrderBy(m => m.ExternalId).ToList();
            maps[0].RemoteId = "58";
            db.MatchCandidates.Add(new MatchCandidate { OrganizationId = _h.OrgId, ConnectionId = _connection.Id, EntityMapId = maps[1].Id, Kind = SyncKind.Customer, Decision = MatchDecision.Skip });
            db.RemoteEntities.Add(new RemoteEntityRecord { OrganizationId = _h.OrgId, ConnectionId = _connection.Id, Kind = SyncKind.Customer, RemoteId = "58", Name = "Acme" });
            db.RemoteEntities.Add(new RemoteEntityRecord { OrganizationId = _h.OrgId, ConnectionId = _connection.Id, Kind = SyncKind.Customer, RemoteId = "59", Name = "Other" });
            await db.SaveChangesAsync();
        }

        var model = await Coverage(c => c.GetAsync());

        model.Entities.Single(e => e.Kind == "Customer").Should().BeEquivalentTo(new CoverageKindModel
        {
            Kind = "Customer", Total = 3, Mapped = 1, Skipped = 1, Pending = 1, Failed = 0,
            RemoteImported = 2, RemoteUnlinked = 1, PercentMapped = 33.3m
        });
        model.Entities.Single(e => e.Kind == "Vendor").PercentMapped.Should().Be(100m, "nothing to map");
        model.ConnectionStatus.Should().Be("Live");
        model.Accounts.Blocking.Should().BeFalse("account mappings are information only (D-16)");
    }

    [Fact]
    public async Task T54_blocking_issues_unmapped_tax_used_by_documents_and_bill_payments_without_an_account()
    {
        (await Coverage(c => c.GetAsync())).Ready.Should().BeTrue("a set-up company with nothing stored is ready");

        await _h.Send(TestPayloads.Invoice(lines: [new TestPayloads.Line("I-1", 1, 100m, Tax: 5m)]));
        await StoreAsync(SyncKind.BillPayment, "BP-1", """{"externalId":"BP-1","method":"CHEQUE","amount":10}""");
        await StoreAsync(SyncKind.Payment, "PAY-1", """{"externalId":"PAY-1","method":"CARD","amount":10}""");

        var model = await Coverage(c => c.GetAsync());

        model.Ready.Should().BeFalse();
        model.BlockingIssues.Select(i => i.Code).Should().Contain([PostingReadiness.BankAccounts, "TAX_UNMAPPED"]);
        model.Warnings.Should().Contain(i => i.Code == PostingReadiness.DepositAccounts && i.Message.Contains("CARD"),
            "a payment without a deposit account goes to Undeposited Funds — a warning");
        model.Warnings.Should().ContainSingle(i => i.Code == PostingReadiness.DepositAccounts, "listed once, not again from preflight (QA-3)");
        model.Warnings.Concat(model.BlockingIssues).GroupBy(i => (i.Code, i.DocumentType)).Should().OnlyContain(g => g.Count() == 1);

        // A bank account for the method clears it; turning bill payments off would too.
        await _h.Scoped(sp => sp.GetRequiredService<IQuickBooksProviderDefaultsService>().SaveDefaultsAsync(new SaveProviderDefaultsRequest
        {
            Values = [new ProviderDefaultValueItem { SettingKey = "BankAccountRef.CHEQUE", SettingValue = "6" }]
        }, 7));
        (await Coverage(c => c.GetAsync())).BlockingIssues.Should().NotContain(i => i.Code == PostingReadiness.BankAccounts);

        var preflight = await _h.Scoped(sp => sp.GetRequiredService<IPreflightService>().RunAsync());
        preflight.Checks.Should().Contain(c => c.Code == PreflightCodes.DepositAccounts && c.Status == "Warn");
        preflight.Checks.Should().NotContain(c => c.Code == PreflightCodes.BankAccounts);
    }

    [Fact]
    public async Task Preflight_warns_about_class_and_location_defaults_only_while_the_company_tracks_them()
    {
        await using (var db = _h.SuperDb())
        {
            db.ReferenceSnapshots.RemoveRange(db.ReferenceSnapshots);
            await db.SaveChangesAsync();
        }
        await PostingControlKit.SeedReferenceAsync(_h, _connection, PostingControlKit.Reference(classTracking: true, departmentTracking: false));

        var checks = (await _h.Scoped(sp => sp.GetRequiredService<IPreflightService>().RunAsync())).Checks;

        checks.Should().Contain(c => c.Code == PreflightCodes.ClassDefault && c.Status == "Warn");
        checks.Should().NotContain(c => c.Code == PreflightCodes.DepartmentDefault);
    }
}
