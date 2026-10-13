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

/// <summary>A40 C8/C9 — batches, duplicate strategies, external-id upsert, batch rollback marking, sync vs background,
/// cancel, final statuses, the opening-balance gate.</summary>
public class ExecutionTests
{
    private static async Task<(ImportTestHost Host, ImportSessionDetail Session)> ValidatedAsync(
        FakeTarget target, string csv, string? strategy = null, ImportCaller? caller = null)
    {
        var host = new ImportTestHost(target);
        var s = await host.UploadCsvAsync(csv, target.Definition.EntityType, caller, strategy);
        await host.Sessions.ValidateAsync(s.Uuid, caller ?? ImportTestHost.Caller());
        return (host, s);
    }

    [Fact]
    public async Task Up_to_500_valid_rows_run_inline_in_batches_of_100_with_progress()
    {
        var target = FakeTarget.Products();
        var (host, s) = await ValidatedAsync(target, ImportTestHost.Csv(250));

        var result = await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller());

        result.IsBackground.Should().BeFalse();
        result.Session.Status.Should().Be(ImportSessionStatus.Completed);
        result.Session.ImportedRows.Should().Be(250);
        result.Session.ImportCompletedAt.Should().NotBeNull();
        target.ApplyCalls.Select(c => c.Count).Should().Equal(100, 100, 50);
        target.ApplyCalls[0][0].Should().Match<ImportApplyRow>(a => a.Action == "CREATE" && a.ExistingEntityUuid == null && a.RowNumber == 2);
        target.ApplyCalls[0][0].Values.Should().NotContainKey("external_id");
        host.Hub.Events.Select(e => e.Processed).Should().Equal(100, 200, 250, 250);
        host.Hub.Events.Last().Status.Should().Be(ImportSessionStatus.Completed);
        (await host.Db.Rows.AsNoTracking().CountAsync(r => r.Status == ImportRowStatus.Imported && r.EntityUuid != null)).Should().Be(250);
        host.Scheduler.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task More_than_500_valid_rows_go_to_the_import_queue_job()
    {
        var target = FakeTarget.Products();
        var (host, s) = await ValidatedAsync(target, ImportTestHost.Csv(501));

        var result = await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller());

        result.IsBackground.Should().BeTrue();
        result.JobId.Should().Be("job-1");
        result.Session.Status.Should().Be(ImportSessionStatus.Importing);
        host.Scheduler.Jobs.Single().Should().Be((ImportTestHost.Org, s.Uuid, 7));
        target.ApplyCalls.Should().BeEmpty();
        (await host.Sessions.ProgressAsync(s.Uuid, ImportTestHost.Caller())).Should().Be(new ImportProgressModel(s.Uuid, 0, 501, "IMPORTING"));

        // The job body (what Hangfire runs) completes it inside the org's tenant scope.
        await new ImportExecutionJob(host.Execution).RunAsync(ImportTestHost.Org, s.Uuid, 7);
        HangfireTenantScope.OrganizationId.Should().BeNull();
        host.Db.ChangeTracker.Clear();
        (await host.Sessions.GetAsync(s.Uuid, ImportTestHost.Caller())).Should().Match<ImportSessionDetail>(d =>
            d.Status == ImportSessionStatus.Completed && d.ImportedRows == 501);
        target.ApplyCalls.Should().HaveCount(6);
    }

    [Fact]
    public void The_job_never_retries_and_runs_on_the_import_queue()
    {
        var method = typeof(ImportExecutionJob).GetMethod(nameof(ImportExecutionJob.RunAsync))!;
        method.GetCustomAttributes(typeof(Hangfire.AutomaticRetryAttribute), false).Cast<Hangfire.AutomaticRetryAttribute>().Single().Attempts.Should().Be(0);
        method.GetCustomAttributes(typeof(Hangfire.QueueAttribute), false).Cast<Hangfire.QueueAttribute>().Single().Queue.Should().Be("import");
    }

    [Fact]
    public async Task Strategies_skip_update_and_create()
    {
        foreach (var (strategy, imported, updated, skipped) in new[] { ("SKIP", 1, 0, 1), ("UPDATE", 1, 1, 0) })
        {
            var target = FakeTarget.Products();
            var existing = Guid.NewGuid();
            target.Existing["P-0001"] = existing;
            var (host, s) = await ValidatedAsync(target, ImportTestHost.Csv(2), strategy);

            var d = (await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller())).Session;

            (d.ImportedRows, d.UpdatedRows, d.SkippedRows).Should().Be((imported, updated, skipped), strategy);
            d.Status.Should().Be(ImportSessionStatus.Completed);
            var rows = await host.Db.Rows.AsNoTracking().OrderBy(r => r.RowNumber).ToListAsync();
            rows[0].EntityUuid.Should().Be(existing);
            if (strategy == "SKIP")
            {
                rows[0].Status.Should().Be(ImportRowStatus.Skipped);
                target.ApplyCalls.Single().Should().ContainSingle(a => a.RowNumber == 3);
            }
            else
            {
                rows[0].Status.Should().Be(ImportRowStatus.Updated);
                target.ApplyCalls.Single()[0].Should().Match<ImportApplyRow>(a => a.Action == "UPDATE" && a.ExistingEntityUuid == existing);
            }
        }
    }

    [Fact]
    public async Task A_skipped_outcome_from_the_target_counts_as_skipped()
    {
        var target = FakeTarget.Products();
        target.OnApply = rows => rows.Select(r => new ImportRowOutcome(r.RowNumber, r.RowNumber == 2 ? "SKIPPED" : "IMPORTED", Guid.NewGuid())).ToList();
        var (host, s) = await ValidatedAsync(target, "ext_id,Product Code,Product Name\nE1,A,a\nE2,B,b\n");

        var d = (await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller())).Session;

        (d.ImportedRows, d.SkippedRows, d.Status).Should().Be((1, 1, ImportSessionStatus.Completed));
        (await host.Db.Rows.AsNoTracking().OrderBy(r => r.RowNumber).Select(r => r.Status).ToListAsync())
            .Should().Equal(ImportRowStatus.Skipped, ImportRowStatus.Imported);
        (await host.Db.ExternalReferences.Select(x => x.ExternalId).ToListAsync()).Should().Equal("E2");
    }

    [Fact]
    public async Task External_ids_are_registered_and_a_reimport_updates_the_same_record()
    {
        var target = FakeTarget.Products();
        var (host, s) = await ValidatedAsync(target, "Legacy ID,Product Code,Product Name\nOLD-1,A,First\nOLD-2,B,Second\n", "UPDATE");
        await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller());

        var refs = await host.Db.ExternalReferences.AsNoTracking().OrderBy(x => x.ExternalId).ToListAsync();
        refs.Select(x => x.ExternalId).Should().Equal("OLD-1", "OLD-2");
        refs.Should().OnlyContain(x => x.OrganizationId == ImportTestHost.Org && x.EntityType == "PRODUCT" && x.LastSessionUuid == s.Uuid);
        var first = refs[0].EntityUuid;

        // Second file: the code changed but the external id is the same → UPDATE of the record it created.
        target.Existing.Clear();
        var again = await host.UploadCsvAsync("Legacy ID,Product Code,Product Name\nOLD-1,A2,Renamed\n", strategy: "UPDATE");
        await host.Sessions.ValidateAsync(again.Uuid, ImportTestHost.Caller());
        var d = (await host.Execution.ExecuteAsync(again.Uuid, ImportTestHost.Caller())).Session;

        d.UpdatedRows.Should().Be(1);
        target.ApplyCalls.Last().Single().Should().Match<ImportApplyRow>(a => a.Action == "UPDATE" && a.ExistingEntityUuid == first);
        (await host.Db.ExternalReferences.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_failing_batch_is_marked_error_with_the_row_message_and_the_import_continues()
    {
        var target = FakeTarget.Products();
        var calls = 0;
        target.OnApply = rows =>
        {
            if (++calls == 2) throw new UnprocessableEntityException($"Row {rows[5].RowNumber}: Product code 'X' is already used (CODE_IN_USE, field code).");
            return rows.Select(r => new ImportRowOutcome(r.RowNumber, "IMPORTED", Guid.NewGuid())).ToList();
        };
        var (host, s) = await ValidatedAsync(target, ImportTestHost.Csv(250));

        var d = (await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller())).Session;

        d.Status.Should().Be(ImportSessionStatus.CompletedWithErrors);
        (d.ImportedRows, d.ErrorRows).Should().Be((150, 100));
        var failed = await host.Db.Rows.AsNoTracking().Where(r => r.Status == ImportRowStatus.Error).OrderBy(r => r.RowNumber).ToListAsync();
        failed.Should().HaveCount(100);
        var culprit = failed[5];
        culprit.RowNumber.Should().Be(107);
        culprit.ErrorsJson.Should().Contain("IMPORT_ERROR").And.Contain("Row 107: Product code");
        failed[0].ErrorsJson.Should().Contain("Batch rolled back because of row 107.");

        var report = await host.Sessions.ErrorsAsync(s.Uuid, 1, 200, ImportTestHost.Caller());
        report.TotalRecords.Should().Be(100);
    }

    [Fact]
    public async Task A_batch_error_without_a_row_number_marks_every_row_with_the_message_and_all_failing_is_failed()
    {
        var target = FakeTarget.Products();
        target.OnApply = _ => throw new BadRequestException("Database unavailable.");
        var (host, s) = await ValidatedAsync(target, ImportTestHost.Csv(3));

        var d = (await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller())).Session;

        d.Status.Should().Be(ImportSessionStatus.Failed);
        d.ErrorRows.Should().Be(3);
        (await host.Db.Rows.AsNoTracking().Select(r => r.ErrorsJson).ToListAsync()).Should().OnlyContain(e => e!.Contains("Database unavailable."));
        ImportExecutionService.FailingRow("Row 12: bad").Should().Be(12);
        ImportExecutionService.FailingRow("bad").Should().BeNull();
    }

    [Fact]
    public async Task Invalid_rows_make_the_result_completed_with_errors()
    {
        var (host, s) = await ValidatedAsync(FakeTarget.Products(), "Product Code,Product Name\nA,a\nB,\n");
        var d = (await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller())).Session;
        (d.Status, d.ImportedRows, d.InvalidRows).Should().Be((ImportSessionStatus.CompletedWithErrors, 1, 1));
    }

    [Fact]
    public async Task Execute_preconditions()
    {
        var host = new ImportTestHost(FakeTarget.Products());
        var s = await host.UploadCsvAsync("Product Code,Product Name\nA,\n");
        await FluentActions.Awaiting(() => host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller()))
            .Should().ThrowAsync<BadRequestException>().WithMessage(ImportMessages.ValidateFirst);
        await host.Sessions.ValidateAsync(s.Uuid, ImportTestHost.Caller());
        await FluentActions.Awaiting(() => host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller()))
            .Should().ThrowAsync<BadRequestException>().WithMessage(ImportMessages.NoValidRows);

        var other = await host.UploadCsvAsync(ImportTestHost.Csv(1));
        await host.Sessions.ValidateAsync(other.Uuid, ImportTestHost.Caller());
        var running = await host.Db.Sessions.FirstAsync(x => x.Uuid == s.Uuid);
        running.Status = ImportSessionStatus.Importing;
        await host.Db.SaveChangesAsync();
        await FluentActions.Awaiting(() => host.Execution.ExecuteAsync(other.Uuid, ImportTestHost.Caller()))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Another Product import is already running.");
    }

    [Fact]
    public async Task Cancel_stops_future_batches_and_keeps_applied_ones()
    {
        var target = FakeTarget.Products();
        ImportTestHost? hostRef = null;
        Guid uuid = default;
        var calls = 0;
        target.OnApply = rows =>
        {
            if (++calls == 2)
            {
                // The cancel endpoint, in another request (its own DbContext), while batch 2 is being applied.
                using var other = hostRef!.NewDb();
                other.Sessions.Single(x => x.Uuid == uuid).Status = ImportSessionStatus.Cancelled;
                other.SaveChanges();
            }
            return rows.Select(r => new ImportRowOutcome(r.RowNumber, "IMPORTED", Guid.NewGuid())).ToList();
        };
        var (host, s) = await ValidatedAsync(target, ImportTestHost.Csv(400));
        hostRef = host;
        uuid = s.Uuid;

        var d = (await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller())).Session;

        target.ApplyCalls.Should().HaveCount(2);
        d.Status.Should().Be(ImportSessionStatus.Cancelled);
        d.ImportedRows.Should().Be(200);
        (await host.Db.Rows.AsNoTracking().CountAsync(r => r.Status == ImportRowStatus.Valid)).Should().Be(200);
    }

    [Fact]
    public async Task Cancel_before_the_start_and_after_the_end()
    {
        var host = new ImportTestHost(FakeTarget.Products());
        var s = await host.UploadCsvAsync(ImportTestHost.Csv(1));
        (await host.Sessions.CancelAsync(s.Uuid, ImportTestHost.Caller())).Status.Should().Be(ImportSessionStatus.Cancelled);
        await FluentActions.Awaiting(() => host.Sessions.CancelAsync(s.Uuid, ImportTestHost.Caller()))
            .Should().ThrowAsync<BadRequestException>().WithMessage(ImportMessages.Finished);
    }

    // ── Opening balances (C9 / D-7) ──────────────────────────────────────────

    private static readonly ImportCaller OpeningCaller = ImportTestHost.Caller(7,
        PermissionCodes.IMPORT_UPLOAD, PermissionCodes.IMPORT_EXECUTE, PermissionCodes.IMPORT_OPENING_BALANCE);

    [Fact]
    public async Task Opening_balance_types_need_the_permission_the_feature_and_the_date()
    {
        var target = FakeTarget.Products("OPENING_STOCK", 11, opening: true);
        var host = new ImportTestHost(target);

        await FluentActions.Awaiting(() => host.UploadCsvAsync(ImportTestHost.Csv(1), "OPENING_STOCK", ImportTestHost.Caller()))
            .Should().ThrowAsync<ForbiddenException>().WithMessage(ImportMessages.OpeningPermission);

        host.Gate.Off.Add(ModuleCodes.OpeningBalanceImport);
        await FluentActions.Awaiting(() => host.UploadCsvAsync(ImportTestHost.Csv(1), "OPENING_STOCK", OpeningCaller))
            .Should().ThrowAsync<ForbiddenException>().WithMessage(ImportMessages.OpeningFeature);
        host.Gate.Off.Clear();

        host.OpeningDate.Date = null;
        await FluentActions.Awaiting(() => host.UploadCsvAsync(ImportTestHost.Csv(1), "OPENING_STOCK", OpeningCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Set the opening balance date in Finance settings first.");

        // No Finance provider registered at all = not set.
        var bare = new ImportSessionService(host.Db, host.Registry, host.Tenant, new ImportOpeningGate(host.Gate, null));
        await FluentActions.Awaiting(() => bare.UploadAsync(new MemoryStream("A\n1\n"u8.ToArray()), "a.csv", 4,
                new ImportUploadRequest { EntityType = "OPENING_STOCK" }, OpeningCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage(ImportMessages.OpeningDateMissing);

        host.OpeningDate.Date = new DateOnly(2026, 1, 1);
        var s = await host.UploadCsvAsync(ImportTestHost.Csv(1), "OPENING_STOCK", OpeningCaller);
        s.DuplicateStrategy.Should().Be("ERROR", "IMP-OB-06");
        await FluentActions.Awaiting(() => host.UploadCsvAsync(ImportTestHost.Csv(1), "OPENING_STOCK", OpeningCaller, "UPDATE"))
            .Should().ThrowAsync<BadRequestException>().WithMessage(ImportMessages.OpeningStrategy);

        await host.Sessions.ValidateAsync(s.Uuid, OpeningCaller);
        host.OpeningDate.Date = null; // cleared again before the import runs
        await FluentActions.Awaiting(() => host.Execution.ExecuteAsync(s.Uuid, OpeningCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage(ImportMessages.OpeningDateMissing);
        host.OpeningDate.Date = new DateOnly(2026, 1, 1);
        (await host.Execution.ExecuteAsync(s.Uuid, OpeningCaller)).Session.Status.Should().Be(ImportSessionStatus.Completed);
    }

    [Fact]
    public async Task Opening_balance_duplicates_are_always_errors()
    {
        var target = FakeTarget.Products("OPENING_STOCK", 11, opening: true);
        target.Existing["P-0001"] = Guid.NewGuid();
        var host = new ImportTestHost(target);
        var s = await host.UploadCsvAsync(ImportTestHost.Csv(2), "OPENING_STOCK", OpeningCaller);
        var r = await host.Sessions.ValidateAsync(s.Uuid, OpeningCaller);
        r.Errors.Single().ErrorCode.Should().Be("DUPLICATE_IN_DB");
        r.Session.ValidRows.Should().Be(1);
    }
}
