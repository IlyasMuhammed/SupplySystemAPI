using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Import.Domain;
using SMS.Modules.Import.Models;
using SMS.Modules.Import.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Import.Tests;

/// <summary>A40 D-38 / D-39 — interrupted imports are recovered (stale IMPORTING → FAILED, or a manager's forced cancel),
/// and a re-run after an interruption re-validates only the rows not yet applied, so nothing is created twice.</summary>
public class RecoveryTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly TimeProvider Later = new FixedClock(DateTimeOffset.UtcNow + ImportLimits.StaleAfter + TimeSpan.FromMinutes(1));

    private static async Task SetStatusAsync(ImportTestHost host, Guid uuid, string status, string? jobId = null)
    {
        await using var other = host.NewDb();
        var s = await other.Sessions.SingleAsync(x => x.Uuid == uuid);
        s.Status = status;
        s.JobId = jobId;
        await other.SaveChangesAsync();
    }

    private static async Task<ImportSession> ReadAsync(ImportTestHost host, Guid uuid)
    {
        await using var other = host.NewDb();
        return await other.Sessions.AsNoTracking().SingleAsync(x => x.Uuid == uuid);
    }

    [Fact]
    public async Task Stale_importing_sessions_without_a_live_job_become_failed_interrupted()
    {
        var host = new ImportTestHost(FakeTarget.Products(), FakeTarget.Products("UNIT", 1), FakeTarget.Products("WAREHOUSE", 4),
            FakeTarget.Products("TAX_CODE", 6), FakeTarget.Products("CUSTOMER", 7));
        var inline = await host.UploadCsvAsync(ImportTestHost.Csv(1));
        var deadJob = await host.UploadCsvAsync(ImportTestHost.Csv(1), "UNIT");
        var liveJob = await host.UploadCsvAsync(ImportTestHost.Csv(1), "WAREHOUSE");
        var unknown = await host.UploadCsvAsync(ImportTestHost.Csv(1), "TAX_CODE");
        var fresh = await host.UploadCsvAsync(ImportTestHost.Csv(1), "CUSTOMER");
        await SetStatusAsync(host, inline.Uuid, ImportSessionStatus.Importing);
        await SetStatusAsync(host, deadJob.Uuid, ImportSessionStatus.Importing, "job-dead");
        await SetStatusAsync(host, liveJob.Uuid, ImportSessionStatus.Importing, "job-live");
        await SetStatusAsync(host, unknown.Uuid, ImportSessionStatus.Importing, "job-unknown");
        await SetStatusAsync(host, fresh.Uuid, ImportSessionStatus.Importing);
        host.Scheduler.Alive["job-live"] = true;
        host.Scheduler.Alive["job-unknown"] = null; // storage unreachable

        // Within 30 minutes of the last batch nothing is touched.
        (await new ImportRecoveryJob(host.NewDb(), host.Scheduler).RunAsync()).Should().Be(0);

        // 31 minutes later — but "fresh" saved a batch just now (its ModifiedAt moves with every batch save).
        await SetStatusAsync(host, fresh.Uuid, ImportSessionStatus.Importing);
        var freshRow = await ReadAsync(host, fresh.Uuid);
        var recovered = await new ImportRecoveryJob(host.NewDb(), host.Scheduler,
            new FixedClock(new DateTimeOffset(freshRow.ModifiedAt, TimeSpan.Zero) + TimeSpan.FromMinutes(29))).RunAsync();
        recovered.Should().Be(0, "every session's last save is still within 30 minutes");

        recovered = await new ImportRecoveryJob(host.NewDb(), host.Scheduler, Later).RunAsync();
        recovered.Should().Be(3); // inline, dead job — and "fresh" too, its last save is now also > 30 min ago
        foreach (var uuid in new[] { inline.Uuid, deadJob.Uuid, fresh.Uuid })
            (await ReadAsync(host, uuid)).Should().Match<ImportSession>(s =>
                s.Status == ImportSessionStatus.Failed && s.ErrorMessage == ImportMessages.Interrupted && s.ImportCompletedAt != null);
        (await ReadAsync(host, liveJob.Uuid)).Status.Should().Be(ImportSessionStatus.Importing, "a queued/running job is alive");
        (await ReadAsync(host, unknown.Uuid)).Status.Should().Be(ImportSessionStatus.Importing, "unknown job state: wait");
        ImportMessages.Interrupted.Should().Be("Import interrupted — re-validate and re-run the remaining rows.");

        // The entity type is free again.
        var next = await host.UploadCsvAsync(ImportTestHost.Csv(1));
        await host.Sessions.ValidateAsync(next.Uuid, ImportTestHost.Caller());
        (await host.Execution.ExecuteAsync(next.Uuid, ImportTestHost.Caller())).Session.Status.Should().Be(ImportSessionStatus.Completed);
    }

    [Fact]
    public async Task Uploaded_sessions_untouched_for_7_days_are_cancelled_others_stay()
    {
        var host = new ImportTestHost(FakeTarget.Products());
        var abandoned = await host.UploadCsvAsync(ImportTestHost.Csv(1));
        var mapping = await host.UploadCsvAsync(ImportTestHost.Csv(1));
        await host.Sessions.UpdateMappingsAsync(mapping.Uuid, new() { Mappings = [new() { SourceColumnIndex = 2, TargetFieldName = null }] }, ImportTestHost.Caller());
        var validated = await host.UploadCsvAsync(ImportTestHost.Csv(1));
        await host.Sessions.ValidateAsync(validated.Uuid, ImportTestHost.Caller());

        (await new ImportRecoveryJob(host.NewDb(), host.Scheduler).CancelAbandonedAsync(DateTime.UtcNow.AddDays(6))).Should().Be(0);

        await new ImportRecoveryJob(host.NewDb(), host.Scheduler, new FixedClock(DateTimeOffset.UtcNow + TimeSpan.FromDays(8))).RunAsync();

        (await ReadAsync(host, abandoned.Uuid)).Should().Match<ImportSession>(s =>
            s.Status == ImportSessionStatus.Cancelled && s.ErrorMessage == ImportMessages.Abandoned);
        (await ReadAsync(host, mapping.Uuid)).Status.Should().Be(ImportSessionStatus.Mapping);
        (await ReadAsync(host, validated.Uuid)).Status.Should().Be(ImportSessionStatus.Validated);
    }

    [Fact]
    public async Task A_manager_cancel_forces_an_importing_session_to_failed_interrupted_others_cancel()
    {
        var host = new ImportTestHost(FakeTarget.Products());
        var s = await host.UploadCsvAsync(ImportTestHost.Csv(1), caller: ImportTestHost.Caller(7));
        await SetStatusAsync(host, s.Uuid, ImportSessionStatus.Importing);
        host.Db.ChangeTracker.Clear(); // a new request scope

        var forced = await host.Sessions.CancelAsync(s.Uuid, ImportTestHost.Caller(9, PermissionCodes.IMPORT_MANAGE));
        forced.Status.Should().Be(ImportSessionStatus.Failed);
        forced.ErrorMessage.Should().Be(ImportMessages.Interrupted);

        var other = await host.UploadCsvAsync(ImportTestHost.Csv(1), caller: ImportTestHost.Caller(7));
        await SetStatusAsync(host, other.Uuid, ImportSessionStatus.Importing);
        host.Db.ChangeTracker.Clear();
        (await host.Sessions.CancelAsync(other.Uuid, ImportTestHost.Caller(7))).Status.Should().Be(ImportSessionStatus.Cancelled);
    }

    [Fact]
    public async Task A_running_loop_stops_when_recovery_or_a_forced_cancel_fails_the_session()
    {
        var target = FakeTarget.Products();
        ImportTestHost? hostRef = null;
        Guid uuid = default;
        target.OnApply = rows =>
        {
            if (target.ApplyCalls.Count == 1)
            {
                using var db = hostRef!.NewDb();
                ImportRecoveryJob.MarkInterrupted(db.Sessions.Single(x => x.Uuid == uuid), DateTime.UtcNow);
                db.SaveChanges();
            }
            return rows.Select(r => new ImportRowOutcome(r.RowNumber, "IMPORTED", Guid.NewGuid())).ToList();
        };
        var host = new ImportTestHost(target);
        hostRef = host;
        var s = await host.UploadCsvAsync(ImportTestHost.Csv(300));
        uuid = s.Uuid;
        await host.Sessions.ValidateAsync(s.Uuid, ImportTestHost.Caller());

        var d = (await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller())).Session;

        target.ApplyCalls.Should().HaveCount(1);
        (d.Status, d.ErrorMessage, d.ImportedRows).Should().Be((ImportSessionStatus.Failed, ImportMessages.Interrupted, 100));
    }

    [Theory]
    [InlineData("SKIP", ImportSessionStatus.Completed, 150, 100, 0)]
    [InlineData("ERROR", ImportSessionStatus.CompletedWithErrors, 150, 0, 100)]
    [InlineData("CREATE_NEW", ImportSessionStatus.CompletedWithErrors, 150, 0, 100)]
    [InlineData("UPDATE", ImportSessionStatus.Completed, 150, 0, 0)]
    public async Task A_rerun_after_an_interruption_applies_only_the_remaining_rows_and_never_duplicates(
        string strategy, string finalStatus, int imported, int skipped, int invalid)
    {
        // Batch 1 commits and is marked; batch 2 is committed by the target but the process dies before marking it.
        var target = FakeTarget.Products();
        var crashed = false;
        target.OnApply = rows =>
        {
            var outcomes = new List<ImportRowOutcome>();
            foreach (var r in rows)
            {
                Guid uuid;
                if (r.Action == "UPDATE") uuid = r.ExistingEntityUuid!.Value;
                else
                {
                    var code = r.Values["code"]!;
                    target.Existing.ContainsKey(code).Should().BeFalse($"{code} must never be created twice");
                    uuid = Guid.NewGuid();
                    target.Existing[code] = uuid;
                }
                target.Store[uuid] = r.Values.ToDictionary(k => k.Key, k => k.Value);
                outcomes.Add(new ImportRowOutcome(r.RowNumber, r.Action == "UPDATE" ? "UPDATED" : "IMPORTED", uuid));
            }
            if (!crashed && target.ApplyCalls.Count == 2)
            {
                crashed = true;
                throw new OperationCanceledException("process killed"); // after the target's commit, before our marking
            }
            return outcomes;
        };
        var host = new ImportTestHost(target);
        var s = await host.UploadCsvAsync(ImportTestHost.Csv(250), strategy: strategy);
        await host.Sessions.ValidateAsync(s.Uuid, ImportTestHost.Caller());
        await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller());

        // Batch 1's outcome and counters were committed before batch 2 started.
        await using (var db = host.NewDb())
        {
            (await db.Rows.CountAsync(r => r.Status == ImportRowStatus.Imported)).Should().Be(100);
            (await db.Rows.CountAsync(r => r.Status == ImportRowStatus.Valid)).Should().Be(150);
            (await db.Sessions.SingleAsync()).ImportedRows.Should().Be(100);
        }
        // The process died: the session is left IMPORTING until recovery.
        await SetStatusAsync(host, s.Uuid, ImportSessionStatus.Importing);
        (await new ImportRecoveryJob(host.NewDb(), host.Scheduler, Later).RunAsync()).Should().Be(1);
        host.Db.ChangeTracker.Clear();

        await FluentActions.Awaiting(() => host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller()))
            .Should().ThrowAsync<BadRequestException>().WithMessage(ImportMessages.ValidateFirst);

        var revalidated = await host.Sessions.ValidateAsync(s.Uuid, ImportTestHost.Caller());
        target.ValidateCalls.Last().Select(r => r.RowNumber).Should().BeEquivalentTo(Enumerable.Range(102, 150),
            "rows already marked IMPORTED are not re-validated");
        revalidated.Session.ImportedRows.Should().Be(100);
        revalidated.Session.InvalidRows.Should().Be(invalid);
        if (invalid > 0) revalidated.ErrorCountByCode.Should().Equal(new Dictionary<string, int> { ["DUPLICATE_IN_DB"] = 100 });

        var d = (await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller())).Session;

        d.Status.Should().Be(finalStatus);
        (d.ImportedRows, d.SkippedRows).Should().Be((imported, skipped));
        if (strategy == "UPDATE") d.UpdatedRows.Should().Be(100);
        target.Existing.Should().HaveCount(250, "each code exists exactly once");
        (await host.Db.Rows.AsNoTracking().CountAsync(r => r.Status == ImportRowStatus.Valid)).Should().Be(0);
    }

    [Fact]
    public async Task Opening_balances_rerun_refuses_the_applied_rows_as_duplicates()
    {
        var target = FakeTarget.Products("OPENING_STOCK", 11, opening: true);
        var caller = ImportTestHost.Caller(7, PermissionCodes.IMPORT_UPLOAD, PermissionCodes.IMPORT_EXECUTE, PermissionCodes.IMPORT_OPENING_BALANCE);
        var host = new ImportTestHost(target);
        var s = await host.UploadCsvAsync(ImportTestHost.Csv(3), "OPENING_STOCK", caller);
        await host.Sessions.ValidateAsync(s.Uuid, caller);
        // Applied by the target, never marked (crash), then recovered.
        foreach (var code in new[] { "P-0001", "P-0002" }) target.Existing[code] = Guid.NewGuid();
        await SetStatusAsync(host, s.Uuid, ImportSessionStatus.Failed);
        host.Db.ChangeTracker.Clear();

        var r = await host.Sessions.ValidateAsync(s.Uuid, caller);

        r.Session.ValidRows.Should().Be(1);
        r.ErrorCountByCode.Should().Equal(new Dictionary<string, int> { ["DUPLICATE_IN_DB"] = 2 });
    }
}
