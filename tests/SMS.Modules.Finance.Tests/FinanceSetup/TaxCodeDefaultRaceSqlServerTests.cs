using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Exceptions;
using Xunit;

using static SMS.Modules.Finance.Tests.FinanceSetup.SetupDesk;

namespace SMS.Modules.Finance.Tests.FinanceSetup;

/// <summary>
/// "At most one default tax code per side" cannot be an index (a BOTH code covers two sides), so the service
/// keeps it — which only holds if two saves of the same organization's codes never decide on the same stale
/// picture. Each test makes the race happen for certain: every save, right after reading the organization's
/// codes, waits (up to a few seconds) until the other save has read them too. Without serialization both read
/// "no default yet" and both write one; with it the second save cannot read until the first has committed,
/// so the wait simply runs out and the second save sees the first one's default.
/// </summary>
public class TaxCodeDefaultRaceSqlServerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    /// <summary>
    /// These tests are about who reads what, not about the lock's timeout: a database slowed down by other
    /// suites running at the same time must not turn the second save's wait into a 409.
    /// </summary>
    private static TaxCodeService Patient(SMS.Modules.Finance.Data.FinanceDbContext db, SMS.Modules.Demand.Data.DemandDbContext demand) =>
        new(db, demand) { LockTimeoutMilliseconds = 120_000 };

    [FinanceSqlServerFact]
    public async Task A_save_that_cannot_get_the_lock_in_time_is_a_409_and_writes_nothing()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var org = Guid.NewGuid();

        // Another save of the same organization's codes is in progress: it holds the lock in an open transaction.
        await using var holder = new Microsoft.Data.SqlClient.SqlConnection(harness.ConnectionString);
        await holder.OpenAsync();
        await using var held = (Microsoft.Data.SqlClient.SqlTransaction)await holder.BeginTransactionAsync();
        await using (var take = holder.CreateCommand())
        {
            take.Transaction = held;
            take.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction'; SELECT @r;";
            take.Parameters.AddWithValue("@resource", TaxCodeService.LockResource(org));
            ((int)(await take.ExecuteScalarAsync())!).Should().BeGreaterThanOrEqualTo(0);
        }

        await using (var db = harness.NewContext(org))
        await using (var demand = harness.NewDemandContext(org))
        {
            var impatient = new TaxCodeService(db, demand) { LockTimeoutMilliseconds = 300 };
            var act = () => impatient.CreateAsync(Code("GST17", 17m, "SALES", isDefault: true), 1);
            (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*Someone else is saving*Try again*");
        }

        // Another organization is not held up by it.
        var other = Guid.NewGuid();
        await using (var db = harness.NewContext(other))
        await using (var demand = harness.NewDemandContext(other))
            (await new TaxCodeService(db, demand) { LockTimeoutMilliseconds = 300 }.CreateAsync(Code("GST17", 17m), 1)).TaxCode.Code.Should().Be("GST17");

        await held.RollbackAsync();
        await using var check = harness.NewContext(org);
        (await check.TaxCodes.CountAsync()).Should().Be(0);
    }

    [FinanceSqlServerFact]
    public async Task Two_codes_created_as_the_sales_default_at_the_same_moment_leave_exactly_one_default()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var org     = Guid.NewGuid();
        var meeting = new Rendezvous(Patience);

        async Task<TaxCodeSaved> Create(string code)
        {
            await using var db     = harness.NewContext(org, new MeetAfterReadingTheCodes(meeting));
            await using var demand = harness.NewDemandContext(org);
            return await Patient(db, demand).CreateAsync(Code(code, 17m, "SALES", isDefault: true), 1);
        }

        var saved = await Task.WhenAll(Task.Run(() => Create("S1")), Task.Run(() => Create("S2")));

        await using var check = harness.NewContext(org);
        (await check.TaxCodes.CountAsync()).Should().Be(2);
        (await check.TaxCodes.Where(t => t.IsDefault).Select(t => t.Code).ToListAsync())
            .Should().ContainSingle("one save must see the other's default and take it over, not add a second one");
        saved.Count(s => s.Message.Contains("is no longer the default")).Should().Be(1, "the second save took the default from the first");
        meeting.SomeoneWaitedInVain.Should().BeTrue("the second save could not read the codes while the first was saving");

        await using var lookupDb = harness.NewContext(org);
        (await new TaxCodeLookup(lookupDb).GetDefaultAsync("SALES")).Should().NotBeNull();
    }

    [FinanceSqlServerFact]
    public async Task Two_existing_codes_made_the_default_at_the_same_moment_leave_exactly_one_default()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true);
        var org = Guid.NewGuid();

        TaxCodeModel s1, b1;
        await using (var db = harness.NewContext(org))
        await using (var demand = harness.NewDemandContext(org))
        {
            var svc = new TaxCodeService(db, demand);
            s1 = (await svc.CreateAsync(Code("S1", 17m, "SALES"), 1)).TaxCode;
            b1 = (await svc.CreateAsync(Code("B1", 16m, "BOTH"), 1)).TaxCode;
        }

        var meeting = new Rendezvous(Patience);
        async Task<TaxCodeSaved> MakeDefault(TaxCodeModel code)
        {
            await using var db     = harness.NewContext(org, new MeetAfterReadingTheCodes(meeting));
            await using var demand = harness.NewDemandContext(org);
            var req = From(code);
            req.IsDefault = true;
            return await Patient(db, demand).UpdateAsync(code.Uuid, req, 1);
        }

        await Task.WhenAll(Task.Run(() => MakeDefault(s1)), Task.Run(() => MakeDefault(b1)));

        await using var check = harness.NewContext(org);
        (await check.TaxCodes.CountAsync(t => t.IsDefault)).Should().Be(1, "S1 (sales) and B1 (sales and purchases) compete for the sales side");
        meeting.SomeoneWaitedInVain.Should().BeTrue();
    }

    [FinanceSqlServerFact]
    public async Task Saves_in_two_organizations_do_not_wait_for_each_other()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var meeting = new Rendezvous(TimeSpan.FromSeconds(30));

        async Task<TaxCodeSaved> Create(Guid org)
        {
            await using var db     = harness.NewContext(org, new MeetAfterReadingTheCodes(meeting));
            await using var demand = harness.NewDemandContext(org);
            return await new TaxCodeService(db, demand).CreateAsync(Code("GST17", 17m, "SALES", isDefault: true), 1);
        }

        var saved = await Task.WhenAll(Task.Run(() => Create(Guid.NewGuid())), Task.Run(() => Create(Guid.NewGuid())));

        meeting.SomeoneWaitedInVain.Should().BeFalse("each organization's codes are serialized on their own, so both read at once");
        saved.Should().OnlyContain(s => s.TaxCode.IsDefault);
    }

    [FinanceSqlServerFact]
    public async Task A_save_inside_a_transaction_the_caller_already_opened_joins_it()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true);
        var org = Guid.NewGuid();

        await using (var db = harness.NewContext(org))
        await using (var demand = harness.NewDemandContext(org))
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await new TaxCodeService(db, demand).CreateAsync(Code("GST17", 17m, "SALES", isDefault: true), 1);
            await tx.RollbackAsync();
        }

        await using var check = harness.NewContext(org);
        (await check.TaxCodes.CountAsync()).Should().Be(0, "the caller's rollback took the save with it");
    }

    [FinanceSqlServerFact]
    public async Task A_lost_race_for_a_code_is_still_a_409_under_the_retrying_strategy_production_uses()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var org = Guid.NewGuid();

        await using (var db = harness.NewContext(org))
        await using (var demand = harness.NewDemandContext(org))
            await new TaxCodeService(db, demand).CreateAsync(Code("GST17", 17m, "SALES"), 1);

        await using (var db = harness.NewContext(org))
        await using (var demand = harness.NewDemandContext(org))
        {
            var act = () => new TaxCodeService(db, demand).CreateAsync(Code("gst17", 18m, "SALES"), 1);
            (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*already a tax code GST17*");
        }

        await using (var db = harness.NewContext(org))
        await using (var demand = harness.NewDemandContext(org))
        {
            var outcome = await new TaxCodeService(db, demand).CreateFromRatesInUseAsync(1);
            outcome.Result.Created.Should().BeEmpty("no sale order uses a rate yet");
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    /// <summary>Two parties; each waits for the other at most <c>patience</c>.</summary>
    private sealed class Rendezvous
    {
        private readonly TimeSpan _patience;
        private readonly TaskCompletionSource _everyone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Rendezvous(TimeSpan patience) => _patience = patience;

        /// <summary>A party gave up waiting: the other could not get as far as reading while it waited.</summary>
        public bool SomeoneWaitedInVain { get; private set; }

        public async Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= 2) _everyone.TrySetResult();
            var first = await Task.WhenAny(_everyone.Task, Task.Delay(_patience));
            if (first != _everyone.Task) SomeoneWaitedInVain = true;
        }
    }

    /// <summary>After this context's first read of the tax-code table has run, meets the other save.</summary>
    private sealed class MeetAfterReadingTheCodes : DbCommandInterceptor
    {
        private readonly Rendezvous _meeting;
        private bool _met;

        public MeetAfterReadingTheCodes(Rendezvous meeting) => _meeting = meeting;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            if (!_met && command.CommandText.Contains("FROM [finance].[tax_codes]", StringComparison.OrdinalIgnoreCase))
            {
                _met = true;
                await _meeting.ArriveAsync();
            }
            return result;
        }
    }
}
