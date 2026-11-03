using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway;
using SMS.Modules.Integration.Gateway.JournalEntries;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Providers.QuickBooks.Mapping;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using Qbo = Intuit.Ipp.Data;

namespace SMS.Modules.Integration.Tests.Sync;

internal sealed class FakeAccountMap : IExternalAccountMapReader
{
    public Dictionary<string, string> Roles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<ExternalAccountMap> GetAsync(string systemType, CancellationToken ct = default) =>
        Task.FromResult(new ExternalAccountMap { ByDefaultKey = new Dictionary<string, string>(Roles, StringComparer.OrdinalIgnoreCase) });
    public Task<IReadOnlyList<ChartAccountRef>> ListLeafAccountsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ChartAccountRef>>([]);
    public Task<int> UpsertMappingsAsync(string systemType, IReadOnlyList<AccountMappingUpsert> rows, CancellationToken ct = default) => Task.FromResult(0);
}

internal sealed class FakeHrmSources : IHrmCostCenterSource, IHrmPersonalIdentifierSource
{
    public List<HrmCostCenterRef> CostCenters { get; } = [];
    public List<string> People { get; } = [];
    public Task<IReadOnlyList<HrmCostCenterRef>> ListAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HrmCostCenterRef>>(CostCenters);
    public Task<IReadOnlyCollection<string>> GetAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult<IReadOnlyCollection<string>>(People);
}

/// <summary>
/// A49 G-1, G-2, G-3, G-5, G-11, G-12, G-14, G-15, G-16 through the real gateway, outbox, ledger and executor, against the repo's scripted
/// QuickBooks fake (<see cref="ScriptedAccountingProvider"/>). <b>None of this has run against a real QuickBooks sandbox:</b> no Intuit keys
/// here, so TS-G1-* are fake-only.
/// </summary>
public class A49JournalEntryTests : IAsyncLifetime
{
    private const string Salary = "DEFAULT_SALARY_EXPENSE", Payable = "DEFAULT_SALARY_PAYABLE", Tax = "DEFAULT_EMPLOYEE_TAX_PAYABLE", Bank = "DEFAULT_BANK";

    private readonly FakeSyncControl _control = new();
    private readonly FakeAccountMap _accounts = new();
    private readonly FakeHrmSources _hrm = new();
    private readonly SyncHarness _h;
    private int _connectionId;

    public A49JournalEntryTests()
    {
        _h = new SyncHarness(configure: s =>
        {
            s.AddSingleton<ISyncControlReader>(_control);
            s.AddSingleton<IExternalAccountMapReader>(_accounts);
            s.AddSingleton<IHrmCostCenterSource>(_hrm);
            s.AddSingleton<IHrmPersonalIdentifierSource>(_hrm);
        });
    }

    public async Task InitializeAsync()
    {
        _connectionId = (await _h.ConnectAsync()).Id;
        _accounts.Roles[Salary] = "60";
        _accounts.Roles[Payable] = "20";
        _accounts.Roles[Tax] = "21";
        _accounts.Roles[Bank] = "10";
        _hrm.CostCenters.Add(new HrmCostCenterRef("HO", "Head Office", true, true));
        foreach (var n in new[] { "OPS", "SALES", "ADMIN", "IT" })
            _hrm.CostCenters.Add(new HrmCostCenterRef(n, n + " Department", true, false));
        await MapAsync("HO", "OPS", "SALES", "ADMIN", "IT");
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private async Task MapAsync(params string[] codes)
    {
        await using var db = _h.DbAs(_h.OrgId);
        foreach (var code in codes)
            db.CostCenterQboMappings.Add(new CostCenterQboMapping
            {
                OrganizationId = _h.OrgId, ConnectionId = _connectionId, CostCenterCode = code, CostCenterName = code,
                QboClassRef = "CL-" + code, QboClassName = code, QboDepartmentRef = code == "OPS" ? "LOC-1" : null
            });
        await db.SaveChangesAsync();
    }

    private static FinancialTransaction Payroll(Guid? id = null, FinDocType type = FinDocType.PayrollRun, string module = PostingSourceModules.Hrm,
        params (string Role, decimal Debit, decimal Credit, string? CostCenter)[] lines)
    {
        var rows = lines.Length > 0 ? lines : [(Salary, 1000m, 0m, "OPS"), (Salary, 500m, 0m, "SALES"), (Payable, 0m, 1400m, "HO"), (Tax, 0m, 100m, null)];
        return new FinancialTransaction
        {
            DocumentType = type, SourceDocumentId = id ?? Guid.NewGuid(), SourceDocumentNumber = "PAY-2026-10", SourceModule = module,
            TransactionDate = new DateTime(2026, 10, 31), CurrencyCode = "PKR", Description = "Payroll October 2026",
            Lines = rows.Select((l, i) => new FinancialTransactionLine
            {
                LineNumber = i + 1, AccountRole = l.Role, Debit = l.Debit, Credit = l.Credit, CostCenter = l.CostCenter, Label = "Ali Khan salary"
            }).ToList(),
            Metadata = new FinancialDocumentMetadata { Properties = new Dictionary<string, string> { [HrmPostingProperties.Period] = "2026-10", [HrmPostingProperties.Headcount] = "142" } }
        };
    }

    private Task<TargetResult> PostAsync(FinancialTransaction tx) =>
        _h.Scoped(sp => sp.GetServices<IFinancialPostingTarget>().Single(t => t.TargetCode == PostingTargetCodes.QuickBooks).PostAsync(tx));

    private Task<TargetResult> VoidAsync(Guid id, DateTime? date = null) =>
        _h.Scoped(sp => sp.GetServices<IFinancialPostingTarget>().Single(t => t.TargetCode == PostingTargetCodes.QuickBooks)
            .VoidAsync(new VoidRequest { SourceDocumentId = id, SourceModule = PostingSourceModules.Hrm, DocumentType = FinDocType.PayrollRun, SourceDocumentNumber = "PAY-2026-10", VoidDate = date }));

    private IEnumerable<RemoteJournalEntry> Sent => _h.Provider.Calls.Where(c => c.Operation == "Create" && c.Kind == SyncKind.JournalEntry).Select(c => (RemoteJournalEntry)c.Entity!);

    // ── TS-G1 (fake, not sandbox) ────────────────────────────────────────────

    [Fact]
    public async Task G1_001_a_payroll_run_reaches_the_fake_company_as_one_balanced_journal_entry_without_employee_data()
    {
        var id = Guid.NewGuid();
        var result = await PostAsync(Payroll(id));
        result.Status.Should().Be(TargetPostingStatus.Queued);

        await _h.DrainAsync();

        var entry = Sent.Single();
        entry.DocNumber.Should().Be("PAY-2026-10");
        entry.PrivateNote.Should().Be("PAY-2026-10, 2026-10, 142 employees");
        entry.TxnDate.Should().Be(new DateTime(2026, 10, 31));
        entry.Lines.Where(l => l.IsDebit).Sum(l => l.Amount).Should().Be(entry.Lines.Where(l => !l.IsDebit).Sum(l => l.Amount)).And.Be(1500m);
        entry.Lines.Select(l => l.Description).Should().OnlyContain(d => d!.EndsWith("Department") || d == "Head Office", "the line's own label named an employee and must not travel");
        (await _h.MapAsync(SyncKind.JournalEntry, id.ToString())).State.Should().Be(SyncState.Synced);
    }

    [Fact]
    public async Task G1_002_five_cost_centers_give_five_classes_and_liabilities_take_the_payroll_class()
    {
        await PostAsync(Payroll(lines:
        [
            (Salary, 100m, 0m, "OPS"), (Salary, 100m, 0m, "SALES"), (Salary, 100m, 0m, "ADMIN"), (Salary, 100m, 0m, "IT"), (Salary, 100m, 0m, "HO"),
            (Payable, 0m, 500m, null)
        ]));
        await _h.DrainAsync();

        var entry = Sent.Single();
        entry.Lines.Where(l => l.IsDebit).Select(l => l.ClassId).Should().BeEquivalentTo(["CL-OPS", "CL-SALES", "CL-ADMIN", "CL-IT", "CL-HO"]);
        entry.Lines.Single(l => !l.IsDebit).ClassId.Should().Be("CL-HO", "BR-G5-003");
        entry.Lines.Single(l => l.ClassId == "CL-OPS").DepartmentId.Should().Be("LOC-1");
        entry.Lines.Single(l => l.ClassId == "CL-OPS").AccountId.Should().Be("60");
    }

    [Fact]
    public async Task G1_003_the_same_posting_twice_creates_one_journal_entry()
    {
        var tx = Payroll();
        await PostAsync(tx);
        await _h.DrainAsync();
        var again = await PostAsync(tx);
        await _h.DrainAsync();

        again.Status.Should().Be(TargetPostingStatus.Posted);
        Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task G1_004_a_failed_call_is_retried_and_an_unknown_outcome_never_posts_twice()
    {
        // The call times out after QuickBooks did create it: the retry must find it, not create another.
        _h.Provider.Enqueue("Create", new ScriptedAccountingProvider.Script(ProviderOutcomeKind.Transient, ApplyAnyway: true));
        var id = Guid.NewGuid();
        await PostAsync(Payroll(id));
        await _h.RunOutboxAsync();

        (await _h.MapAsync(SyncKind.JournalEntry, id.ToString())).RemoteId.Should().BeNull();
        _h.Clock.Advance(TimeSpan.FromHours(1));
        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.JournalEntry, id.ToString());
        map.State.Should().Be(SyncState.Synced);
        _h.Provider.Of(SyncKind.JournalEntry).Should().ContainSingle();
    }

    // ── TS-G2 ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FinDocType.PayrollRun)]
    [InlineData(FinDocType.PayrollPayment)]
    [InlineData(FinDocType.LoanDisbursement)]
    [InlineData(FinDocType.Expense)]
    [InlineData(FinDocType.ExpensePayment)]
    [InlineData(FinDocType.StatutoryRemittance)]
    [InlineData(FinDocType.FinalSettlement)]
    [InlineData(FinDocType.PayrollProvision)]
    public async Task G2_every_hrm_type_routes_to_a_journal_entry_under_its_own_sync_control(FinDocType type)
    {
        var id = Guid.NewGuid();
        (await PostAsync(Payroll(id, type))).Status.Should().Be(TargetPostingStatus.Queued);
        var map = await _h.MapAsync(SyncKind.JournalEntry, id.ToString());
        SyncHolds.TypeOf(map).Should().Be(type.ToString());
    }

    [Fact]
    public async Task G2_an_expense_from_another_module_keeps_the_old_path()
    {
        var result = await PostAsync(Payroll(type: FinDocType.Expense, module: PostingSourceModules.Finance));
        result.Status.Should().Be(TargetPostingStatus.Skipped);
        result.Error.Should().StartWith("NO_DOCUMENT");
    }

    // ── TS-G3, G-5, G-14 ─────────────────────────────────────────────────────

    [Fact]
    public async Task G3_an_unmapped_role_blocks_and_mapping_it_lets_the_entry_through()
    {
        _accounts.Roles.Remove(Tax);
        var id = Guid.NewGuid();
        var result = await PostAsync(Payroll(id));

        result.Status.Should().Be(TargetPostingStatus.Failed);
        result.Error.Should().Contain(Tax);
        (await _h.MapAsync(SyncKind.JournalEntry, id.ToString())).State.Should().Be(SyncState.Blocked);
        _h.Provider.WriteCalls.Should().Be(0);

        _accounts.Roles[Tax] = "21";
        await _h.AsJob(async sp =>
        {
            await sp.GetRequiredService<IBlockedRecordRevalidator>().RevalidateAsync(_connectionId, _h.OrgId, RevalidationTrigger.Periodic);
            return 0;
        });
        await _h.DrainAsync();
        (await _h.MapAsync(SyncKind.JournalEntry, id.ToString())).State.Should().Be(SyncState.Synced);
    }

    [Fact]
    public async Task G5_003_an_unmapped_cost_center_is_refused_and_named_never_unclassified()
    {
        _hrm.CostCenters.Add(new HrmCostCenterRef("NEW", "New Unit", true, false));
        var result = await PostAsync(Payroll(lines: [(Salary, 100m, 0m, "NEW"), (Payable, 0m, 100m, "HO")]));

        result.Status.Should().Be(TargetPostingStatus.Failed);
        result.Error.Should().Contain("NEW");
        Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task G14_001_a_currency_other_than_the_company_home_is_refused()
    {
        var tx = Payroll();
        var usd = new FinancialTransaction
        {
            DocumentType = tx.DocumentType, SourceDocumentId = tx.SourceDocumentId, SourceDocumentNumber = tx.SourceDocumentNumber, SourceModule = tx.SourceModule,
            TransactionDate = tx.TransactionDate, CurrencyCode = "USD", Lines = tx.Lines
        };
        var result = await PostAsync(usd);

        result.Status.Should().Be(TargetPostingStatus.Failed);
        result.Error.Should().Contain("USD").And.Contain("PKR");
        Sent.Should().BeEmpty();
    }

    // ── TS-G11 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task G11_001_a_name_in_the_note_refuses_the_entry_and_stores_nothing()
    {
        _hrm.People.Add("Ali Khan");
        var tx = Payroll(id: Guid.NewGuid());
        var named = new FinancialTransaction
        {
            DocumentType = tx.DocumentType, SourceDocumentId = tx.SourceDocumentId, SourceDocumentNumber = tx.SourceDocumentNumber, SourceModule = tx.SourceModule,
            TransactionDate = tx.TransactionDate, CurrencyCode = "PKR", Lines = tx.Lines, Description = "Advance to ali khan"
        };

        var result = await PostAsync(named);

        result.Status.Should().Be(TargetPostingStatus.Failed);
        result.Error.Should().StartWith("JE_PERSONAL_DATA").And.NotContain("Ali", "the log must not repeat what it refused");
        (await _h.FindMapAsync(SyncKind.JournalEntry, tx.SourceDocumentId.ToString())).Should().BeNull();
    }

    [Fact]
    public async Task G11_a_name_on_a_cost_center_is_caught_too()
    {
        _hrm.People.Add("EMP-0007");
        _hrm.CostCenters.Add(new HrmCostCenterRef("X", "Team of EMP-0007", true, false));
        await MapAsync("X");
        (await PostAsync(Payroll(lines: [(Salary, 100m, 0m, "X"), (Payable, 0m, 100m, "HO")]))).Error.Should().StartWith("JE_PERSONAL_DATA");
    }

    [Theory]
    [InlineData("Salaries for October", "Ali", false)]
    [InlineData("Ali's advance", "Ali", true)]
    [InlineData("advance: ALI KHAN.", "Ali Khan", true)]
    [InlineData("Alina", "Ali", false)]
    public void G11_the_safety_net_matches_whole_words(string text, string name, bool hit) =>
        QuickBooksJournalEntryBuilder.ContainsAny(text, [name]).Should().Be(hit);

    // ── TS-G12 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task G12_001_a_reversal_is_an_opposite_new_entry_and_the_original_stays()
    {
        var id = Guid.NewGuid();
        await PostAsync(Payroll(id));
        await _h.DrainAsync();

        var result = await VoidAsync(id, new DateTime(2026, 11, 5));
        result.Status.Should().Be(TargetPostingStatus.Queued);
        await _h.DrainAsync();

        Sent.Should().HaveCount(2);
        var (original, reversal) = (Sent.First(), Sent.Last());
        reversal.DocNumber.Should().Be("PAY-2026-10-R");
        reversal.TxnDate.Should().Be(new DateTime(2026, 11, 5));
        reversal.Lines.Select(l => (l.IsDebit, l.Amount, l.AccountId, l.ClassId))
            .Should().BeEquivalentTo(original.Lines.Select(l => (!l.IsDebit, l.Amount, l.AccountId, l.ClassId)));

        _h.Provider.CallsOf("Void").Should().Be(0, "BR-G12-001: never a void or a delete");
        (await _h.MapAsync(SyncKind.JournalEntry, id.ToString())).State.Should().Be(SyncState.Synced, "BR-G12-002");
        (await _h.MapAsync(SyncKind.JournalEntry, id + ":void")).State.Should().Be(SyncState.Synced);

        // Retried: nothing new.
        await VoidAsync(id, new DateTime(2026, 11, 5));
        await _h.DrainAsync();
        Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task G12_reversing_what_was_only_held_sends_nothing()
    {
        _control.Types["PayrollRun"] = new SyncControlDecision(false, true);
        var id = Guid.NewGuid();
        (await PostAsync(Payroll(id))).Status.Should().Be(TargetPostingStatus.Held);

        var result = await VoidAsync(id);

        result.Status.Should().Be(TargetPostingStatus.Posted);
        (await _h.MapAsync(SyncKind.JournalEntry, id.ToString())).State.Should().Be(SyncState.Voided);
        _h.Provider.WriteCalls.Should().Be(0);
    }

    [Fact]
    public async Task G12_reversing_a_document_never_seen_is_a_no_op()
    {
        var result = await VoidAsync(Guid.NewGuid());
        result.Status.Should().Be(TargetPostingStatus.Skipped);
        result.Error.Should().StartWith("NEVER_SYNCED");
    }

    // ── TS-G15 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task G15_001_a_new_connection_starts_every_hrm_type_off_and_held()
    {
        await using var db = _h.DbAs(_h.OrgId);
        var connection = await db.Connections.SingleAsync();
        var settings = await db.Settings.SingleAsync();
        await SyncControlDefaults.SeedNewConnectionAsync(db, connection, settings, default);
        await db.SaveChangesAsync();

        foreach (var type in SyncControlDocumentTypes.Hrm)
        {
            var row = await db.SyncControlConfigs.SingleAsync(c => c.DocumentType == type);
            (row.IsEnabled, row.HoldWhenDisabled).Should().Be((false, true), type);
            SyncControlDefaults.Labels.Should().ContainKey(type);
        }
        // The other types keep what they had.
        (await db.SyncControlConfigs.SingleAsync(c => c.DocumentType == SyncControlDocumentTypes.Customer)).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void G15_no_row_never_means_enabled_for_an_hrm_type()
    {
        foreach (var type in SyncControlDocumentTypes.Hrm)
            SyncControlDefaults.Effective(null, null, type).Action.Should().Be(SyncControlAction.Hold, type);
        foreach (var type in SyncControlDocumentTypes.Hrm)
            SyncControlDefaults.Effective(null, new IntegrationSettings(), type).Action.Should().Be(SyncControlAction.Hold, type);
        SyncControlDefaults.Effective(null, null, SyncControlDocumentTypes.Customer).Should().Be(SyncControlDecision.Enabled);
    }

    [Fact]
    public async Task G15_turning_one_hrm_type_on_releases_only_its_own_held_entries()
    {
        _control.Types["PayrollRun"] = new SyncControlDecision(false, true);
        _control.Types["Expense"] = new SyncControlDecision(false, true);
        var run = Guid.NewGuid();
        var claim = Guid.NewGuid();
        (await PostAsync(Payroll(run))).Status.Should().Be(TargetPostingStatus.Held);
        (await PostAsync(Payroll(claim, FinDocType.Expense))).Status.Should().Be(TargetPostingStatus.Held);

        await _h.Scoped(async sp =>
        {
            var releaser = sp.GetRequiredService<IHeldRecordReleaser>();
            (await releaser.CountHeldAsync(_connectionId, "PayrollRun")).Should().Be(1);
            (await releaser.CountHeldAsync(_connectionId, "Expense")).Should().Be(1);
            _control.Types["PayrollRun"] = SyncControlDecision.Enabled;
            await releaser.ReleaseAsync(_connectionId, "PayrollRun");
        });
        await _h.DrainAsync();

        (await _h.MapAsync(SyncKind.JournalEntry, run.ToString())).State.Should().Be(SyncState.Synced);
        (await _h.MapAsync(SyncKind.JournalEntry, claim.ToString())).LastErrorCode.Should().Be("HELD:Expense");
        Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task G15_002_the_accountant_cannot_turn_a_type_on_until_it_is_ready()
    {
        _hrm.CostCenters.Add(new HrmCostCenterRef("NEW", "New Unit", true, false));
        await _h.Scoped(async sp =>
        {
            var service = new SyncControlService(sp.GetRequiredService<SMS.Modules.Integration.Data.IntegrationDbContext>(),
                sp.GetRequiredService<IConnectionAccessor>(), sp, NullLogger<SyncControlService>.Instance);

            var refused = await FluentActions.Invoking(() => service.UpdateAsync("PayrollRun", new UpdateSyncControlRequest { IsEnabled = true, HoldWhenDisabled = true }, 1))
                .Should().ThrowAsync<SMS.Shared.Exceptions.ConflictException>();
            refused.Which.Message.Should().StartWith("HRM_SYNC_NOT_READY").And.Contain("NEW");

            await using var db = _h.DbAs(_h.OrgId);
            db.CostCenterQboMappings.Add(new CostCenterQboMapping { OrganizationId = _h.OrgId, ConnectionId = _connectionId, CostCenterCode = "NEW", CostCenterName = "New Unit", QboClassRef = "CL-NEW" });
            await db.SaveChangesAsync();

            (await service.UpdateAsync("PayrollRun", new UpdateSyncControlRequest { IsEnabled = true, HoldWhenDisabled = true }, 1)).Row.IsEnabled.Should().BeTrue();
        });
    }

    // ── TS-G16 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task G16_001_1200_lines_become_entries_that_each_balance_and_keep_every_total()
    {
        var centers = Enumerable.Range(1, 1197).Select(i => "C" + i).ToList();
        _hrm.CostCenters.AddRange(centers.Select(c => new HrmCostCenterRef(c, "Unit " + c, true, false)));
        await MapAsync([.. centers]);

        var rows = centers.Select((c, i) => (Salary, 100m + i % 7, 0m, (string?)c)).ToList();
        var total = rows.Sum(r => r.Item2);
        var tax = Math.Round(total * 0.0777m, 2);
        rows.Add((Tax, 0m, tax, "HO"));
        rows.Add((Payable, 0m, total - tax, "HO"));

        var id = Guid.NewGuid();
        (await PostAsync(Payroll(id, lines: [.. rows]))).Status.Should().Be(TargetPostingStatus.Queued);
        await _h.DrainAsync(20);

        var entries = Sent.ToList();
        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e.Lines.Count <= QuickBooksJournalEntryBuilder.MaxLinesPerEntry);
        foreach (var e in entries)
            e.Lines.Where(l => l.IsDebit).Sum(l => l.Amount).Should().Be(e.Lines.Where(l => !l.IsDebit).Sum(l => l.Amount));

        var all = entries.SelectMany(e => e.Lines).ToList();
        all.Where(l => l.AccountId == "60").Sum(l => l.Amount).Should().Be(total);
        all.Where(l => l.AccountId == "21").Sum(l => l.Amount).Should().Be(tax);
        all.Where(l => l.AccountId == "20").Sum(l => l.Amount).Should().Be(total - tax);
        all.Where(l => l.AccountId != "60").Should().OnlyContain(l => l.ClassId == "CL-HO", "the liabilities stay on the payroll cost center");
        entries.Select(e => e.DocNumber).Should().BeEquivalentTo(["PAY-2026-10-B1", "PAY-2026-10-B2"]);

        // The posting log asks about the document, and gets the document.
        var status = await _h.Gateway(async g =>
        {
            var s = (await g.GetStatusAsync(SyncKind.JournalEntry, [id.ToString()])).Single();
            s.State.Should().Be(SyncState.Synced);
            return GatewayResult.Accepted(s.State);
        });
        status.Outcome.Should().Be(GatewayOutcome.Accepted);

        // And reversing it reverses both.
        await VoidAsync(id, new DateTime(2026, 11, 5));
        await _h.DrainAsync(20);
        Sent.Should().HaveCount(4);
        Sent.Skip(2).Select(e => e.DocNumber).Should().BeEquivalentTo(["PAY-2026-10-B1-R", "PAY-2026-10-B2-R"]);
    }

    [Fact]
    public async Task G16_a_document_still_on_its_way_shows_its_furthest_behind_batch()
    {
        var lines = Enumerable.Range(1, 1000).Select(i => new JournalEntryPayloadLine { PostingType = JournalEntryPostingTypes.Debit, Amount = 1m, AccountRole = Salary, CostCenterCode = "C" + i, CostCenterName = "C" + i }).ToList();
        lines.Add(new JournalEntryPayloadLine { PostingType = JournalEntryPostingTypes.Credit, Amount = 1000m, AccountRole = Payable, CostCenterCode = "HO", CostCenterName = "HO" });

        var batches = QuickBooksJournalEntryBuilder.Split(lines, "HO", 900);

        batches.Should().HaveCount(2);
        batches.SelectMany(b => b).Where(l => l.PostingType == JournalEntryPostingTypes.Credit).Sum(l => l.Amount).Should().Be(1000m);
    }

    // ── Readiness + map ──────────────────────────────────────────────────────

    [Fact]
    public async Task G3_readiness_names_what_is_missing_and_says_whether_it_sends_now()
    {
        _accounts.Roles.Remove(Tax);
        _hrm.CostCenters.Add(new HrmCostCenterRef("NEW", "New Unit", true, false));
        var needs = new QuickBooksPostingNeeds("PayrollRun", [new(Salary, null), new(Tax, null)], ["OPS", "NEW"], "USD");

        var sent = await _h.Scoped(sp => sp.GetRequiredService<IQuickBooksPostingReadiness>().CheckAsync(needs));
        sent.Connected.Should().BeTrue();
        sent.SendsNow.Should().BeTrue();
        sent.IsReady.Should().BeFalse();
        sent.MissingAccounts.Should().BeEquivalentTo([new QuickBooksAccountNeed(Tax, null)]);
        sent.MissingCostCenters.Should().BeEquivalentTo([new QuickBooksCostCenterGap("NEW", "New Unit")]);
        sent.CurrencyMismatch.Should().Be(new QuickBooksCurrencyMismatch("USD", "PKR"));

        _control.Types["PayrollRun"] = new SyncControlDecision(false, true);
        (await _h.Scoped(sp => sp.GetRequiredService<IQuickBooksPostingReadiness>().CheckAsync(needs))).SendsNow.Should().BeFalse();
    }

    [Fact]
    public async Task An_account_quickbooks_wants_a_party_on_is_refused_with_its_name()
    {
        _h.Reference.Data = new RemoteReferenceData { Accounts = [new RemoteAccount("20", "Accounts Payable (A/P)", "Accounts Payable", null, null, "PKR", true)] };
        var result = await PostAsync(Payroll());
        result.Status.Should().Be(TargetPostingStatus.Failed);
        result.Error.Should().Contain("Accounts Payable (A/P)");
    }

    [Fact]
    public async Task G5_the_map_populates_from_hrm_keeps_the_accountants_class_and_deactivates_what_went()
    {
        await _h.Scoped(async sp =>
        {
            var map = sp.GetRequiredService<ICostCenterQboMappingService>();
            _hrm.CostCenters.Add(new HrmCostCenterRef("NEW", "New Unit", true, false));

            var list = await map.ListAsync();
            list.Single(m => m.CostCenterCode == "NEW").IsMapped.Should().BeFalse();
            list.Single(m => m.CostCenterCode == "OPS").QboClassRef.Should().Be("CL-OPS");
            (await map.GetUnmappedAsync()).Select(m => m.CostCenterCode).Should().Equal("NEW");

            await map.UpdateAsync("new", new UpdateCostCenterMappingRequest { QboClassRef = "CL-N", QboClassName = "N" }, 1);
            (await map.ResolveAsync("NEW"))!.QboClassRef.Should().Be("CL-N");

            _hrm.CostCenters.RemoveAll(c => c.Code == "NEW");
            var result = await map.SyncAsync(1);
            result.Deactivated.Should().Be(1);
            (await map.ResolveAsync("NEW")).Should().BeNull();
        });
    }

    [Fact]
    public async Task G5_another_organizations_connection_sees_none_of_this_map()
    {
        var other = Guid.NewGuid();
        await _h.ConnectAsync(organizationId: other, realmId: "other");
        _h.Tenant.RequestOrganizationId = other;
        await _h.Scoped(async sp =>
        {
            _hrm.CostCenters.Clear();
            (await sp.GetRequiredService<ICostCenterQboMappingService>().ResolveAsync("OPS")).Should().BeNull();
        });
    }

    [Fact]
    public async Task FE2_the_pickers_list_active_classes_and_locations_and_degrade_without_a_connection()
    {
        var map = (IServiceProvider sp) => sp.GetRequiredService<ICostCenterQboMappingService>();

        var unfetched = await _h.Scoped(sp => map(sp).ReferenceAsync());
        (unfetched.Connected, unfetched.Loaded, unfetched.Classes.Count).Should().Be((true, false, 0));

        _h.Reference.Data = new RemoteReferenceData
        {
            Classes = [new RemoteClass("C1", "Ops", "Group:Ops", true), new RemoteClass("C2", "Old", null, false)],
            Departments = [new RemoteDepartment("D1", "Karachi", null, true)],
            Preferences = new RemotePreferences("PKR", false, true, false) { ClassTrackingPerTxnLine = true }
        };
        var loaded = await _h.Scoped(sp => map(sp).ReferenceAsync());
        (loaded.Connected, loaded.Loaded, loaded.TracksClasses).Should().Be((true, true, true));
        loaded.Classes.Select(c => (c.Id, c.Name)).Should().Equal(("C1", "Group:Ops"));
        loaded.Departments.Select(d => d.Id).Should().Equal("D1");

        _h.Tenant.RequestOrganizationId = Guid.NewGuid();
        var none = await _h.Scoped(sp => map(sp).ReferenceAsync());
        (none.Connected, none.Loaded, none.Classes.Count, none.Departments.Count).Should().Be((false, false, 0, 0));
        _h.Tenant.RequestOrganizationId = _h.OrgId;
    }

    // ── E-13: a service invoice ──────────────────────────────────────────────

    private static SalesInvoicePayload ServiceInvoice(string id) => new()
    {
        ExternalId = id, DocNumber = "SINV-" + id, CustomerExternalId = "C-1", TxnDate = TestPayloads.TxnDate, DueDate = TestPayloads.TxnDate, CurrencyCode = "PKR",
        Status = SalesInvoicePayloadStatus.Issued, ExpectedTotal = 100m, PrivateNote = "Service invoice",
        Lines = [new SalesInvoiceLinePayload { LineNo = 1, ItemExternalId = ServiceInvoiceLines.ItemExternalId, Description = "Consulting", Quantity = 1m, UnitPrice = 100m }]
    };

    [Fact]
    public async Task E13_a_service_invoice_waits_with_a_clear_message_then_goes_as_the_default_service_item()
    {
        var defaults = new FakeProviderDefaults();
        await using var h = new SyncHarness(configure: s => { s.AddSingleton<IProviderDefaultsReader>(defaults); s.AddSingleton<ISyncControlReader>(new FakeSyncControl()); });
        var connection = await h.ConnectAsync();
        await using (var db = h.DbAs(h.OrgId))
        {
            db.EntityMaps.Add(new EntityMap { OrganizationId = h.OrgId, ConnectionId = connection.Id, SourceSystem = QuickBooksSourceSystems.Scm, Kind = SyncKind.Customer, ExternalId = "C-1", State = SyncState.Synced, RemoteId = "900", CreatedDate = h.Now });
            await db.SaveChangesAsync();
        }

        var refused = await h.Send(ServiceInvoice("S1"));
        refused.Outcome.Should().Be(GatewayOutcome.Invalid);
        refused.Errors.Single().Code.Should().Be("SERVICE_ITEM_MISSING");
        refused.Errors.Single().Message.Should().Contain("default service item");
        (await h.MapAsync(SyncKind.SalesInvoice, "S1")).State.Should().Be(SyncState.Blocked);
        h.Provider.WriteCalls.Should().Be(0);

        defaults.Values[ProviderDefaultKeys.ServiceItemRef] = "ITEM-9";
        await h.AsJob(async sp =>
        {
            await sp.GetRequiredService<IBlockedRecordRevalidator>().RevalidateAsync(connection.Id, h.OrgId, RevalidationTrigger.Periodic);
            return 0;
        });
        await h.DrainAsync();

        (await h.MapAsync(SyncKind.SalesInvoice, "S1")).State.Should().Be(SyncState.Synced);
        var sent = h.Provider.Calls.Where(c => c.Operation == "Create" && c.Kind == SyncKind.SalesInvoice).Select(c => (RemoteInvoice)c.Entity!).Single();
        sent.Lines.Single().ItemId.Should().Be("ITEM-9");
        h.Provider.Of(SyncKind.Item).Should().BeEmpty("the marker is not an item to create");
        (await h.FindMapAsync(SyncKind.Item, ServiceInvoiceLines.ItemExternalId)).Should().BeNull();
    }

    // ── The SDK object ───────────────────────────────────────────────────────

    [Fact]
    public void The_sdk_object_has_one_posting_line_per_line_with_flags_set()
    {
        var sdk = (Qbo.JournalEntry)QboEntityMapper.ToSdk(new RemoteJournalEntry
        {
            DocNumber = "PAY-1", TxnDate = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Local), PrivateNote = "note", CurrencyCode = "PKR",
            Lines =
            [
                new RemoteJournalEntryLine { IsDebit = true, Amount = 10m, AccountId = "60", ClassId = "C1", DepartmentId = "D1", Description = "Ops" },
                new RemoteJournalEntryLine { IsDebit = false, Amount = 10m, AccountId = "20", ClassId = "C2" }
            ]
        });

        sdk.TxnDateSpecified.Should().BeTrue();
        sdk.TxnDate.Kind.Should().Be(DateTimeKind.Unspecified);
        sdk.Line.Should().HaveCount(2);
        sdk.Line.Should().OnlyContain(l => l.AmountSpecified && l.DetailTypeSpecified && l.DetailType == Qbo.LineDetailTypeEnum.JournalEntryLineDetail);
        var debit = (Qbo.JournalEntryLineDetail)sdk.Line[0].AnyIntuitObject;
        debit.PostingTypeSpecified.Should().BeTrue();
        debit.PostingType.Should().Be(Qbo.PostingTypeEnum.Debit);
        (debit.AccountRef.Value, debit.ClassRef.Value, debit.DepartmentRef.Value).Should().Be(("60", "C1", "D1"));
        ((Qbo.JournalEntryLineDetail)sdk.Line[1].AnyIntuitObject).DepartmentRef.Should().BeNull();

        QboEntityMapper.EntityName(SyncKind.JournalEntry).Should().Be("JournalEntry");
    }

    [Fact]
    public void The_stored_payload_round_trips_and_is_recognised()
    {
        var payload = new JournalEntryPayload { ExternalId = "x", DocNumber = "D", SourceType = "PayrollRun", TxnDate = new DateTime(2026, 1, 2), Lines = [new JournalEntryPayloadLine { Amount = 1.5m, CostCenterCode = "A" }] };
        var back = (JournalEntryPayload)SyncPayloads.Deserialize(SyncKind.JournalEntry, SyncPayloads.Serialize(payload));
        back.Lines.Single().Amount.Should().Be(1.5m);
        SyncPayloads.KindOf(payload).Should().Be(SyncKind.JournalEntry);
        SyncPayloads.DependenciesOf(SyncKind.JournalEntry, payload).Should().BeEmpty();
        SyncPayloads.IsVoidable(SyncKind.JournalEntry).Should().BeFalse();
    }
}
