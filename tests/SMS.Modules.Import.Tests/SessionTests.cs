using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Import.Domain;
using SMS.Modules.Import.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Import.Tests;

/// <summary>A40 C3/C6/C7 — upload, auto mapping, presets, IMP-MAP-01..03, validation, visibility, IMP-FILE-08.</summary>
public class SessionTests
{
    [Fact]
    public async Task Upload_parses_stores_rows_as_json_and_auto_maps()
    {
        var host = new ImportTestHost(FakeTarget.Products());
        var s = await host.UploadCsvAsync("Product Code,product_name,Price,Notes\nP-1,One,1.5,x\nP-2,Two,2,y\n");

        s.Status.Should().Be(ImportSessionStatus.Uploaded);
        s.TotalRows.Should().Be(2);
        s.FileType.Should().Be("CSV");
        s.DuplicateStrategy.Should().Be("SKIP");
        s.Mappings.Select(m => m.TargetFieldName).Should().Equal("code", "name", "price", null);
        s.Mappings.Select(m => m.ConfidenceScore).Should().Equal(1.00m, 0.95m, 1.00m, null);
        s.Mappings.Take(3).Should().OnlyContain(m => m.IsAutoMapped);
        s.Mappings[1].TargetDisplayName.Should().Be("Product Name");
        s.UnmappedColumns.Should().Equal("Notes");
        s.UnmappedRequiredFields.Should().BeEmpty();
        (await host.Db.Rows.CountAsync()).Should().Be(2);
        (await host.Db.Rows.OrderBy(r => r.RowNumber).FirstAsync()).RawJson.Should().Be("[\"P-1\",\"One\",\"1.5\",\"x\"]");
    }

    [Fact]
    public async Task Unknown_entity_is_404_and_bad_strategy_400()
    {
        var host = new ImportTestHost(FakeTarget.Products());
        await FluentActions.Awaiting(() => host.UploadCsvAsync("A\n1\n", "NOPE")).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => host.UploadCsvAsync("A\n1\n", strategy: "MERGE")).Should().ThrowAsync<BadRequestException>();
        var noUpdate = new ImportTestHost(FakeTarget.Products(allowUpdate: false));
        await FluentActions.Awaiting(() => noUpdate.UploadCsvAsync("A\n1\n", strategy: "UPDATE")).Should().ThrowAsync<BadRequestException>()
            .WithMessage(ImportMessages.UpdateNotAllowed);
    }

    [Fact]
    public async Task IMP_FILE_08_one_running_import_per_entity_type()
    {
        var host = new ImportTestHost(FakeTarget.Products());
        var s = await host.UploadCsvAsync(ImportTestHost.Csv(1));
        var row = await host.Db.Sessions.SingleAsync();
        row.Status = ImportSessionStatus.Importing;
        await host.Db.SaveChangesAsync();
        await FluentActions.Awaiting(() => host.UploadCsvAsync(ImportTestHost.Csv(1))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("Another Product import is already running.");
    }

    // ── Mapping rules ───────────────────────────────────────────────────────

    [Fact]
    public async Task Mapping_rules_required_key_and_twice()
    {
        var host = new ImportTestHost(FakeTarget.Products());
        var s = await host.UploadCsvAsync("Product Code,Product Name,Other\nP-1,One,z\n");
        var svc = host.Sessions;
        var caller = ImportTestHost.Caller();

        // IMP-MAP-01 — an incomplete mapping is SAVED by the PUT; validate refuses it.
        var incomplete = await svc.UpdateMappingsAsync(s.Uuid, new() { Mappings = [new() { SourceColumnIndex = 1, TargetFieldName = null }] }, caller);
        incomplete.Status.Should().Be(ImportSessionStatus.Mapping);
        incomplete.UnmappedRequiredFields.Should().Equal("name");
        host.Db.ChangeTracker.Clear();
        (await host.Db.ColumnMappings.SingleAsync(m => m.SourceColumnIndex == 1)).TargetFieldName.Should().BeNull("the PUT was saved");
        await FluentActions.Awaiting(() => svc.ValidateAsync(s.Uuid, caller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Map required field: Product Name.");
        host.Db.ChangeTracker.Clear();
        await svc.UpdateMappingsAsync(s.Uuid, new() { Mappings = [new() { SourceColumnIndex = 1, TargetFieldName = "name" }] }, caller);
        // IMP-MAP-03 — field twice / column twice: the only refusals of a PUT
        await FluentActions.Awaiting(() => svc.UpdateMappingsAsync(s.Uuid, new() { Mappings = [new() { SourceColumnIndex = 2, TargetFieldName = "name" }] }, caller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Field 'Product Name' is mapped twice.");
        await FluentActions.Awaiting(() => svc.UpdateMappingsAsync(s.Uuid, new()
            { Mappings = [new() { SourceColumnIndex = 2, TargetFieldName = "ref" }, new() { SourceColumnIndex = 2, TargetFieldName = "unit" }] }, caller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Column 'Other' is mapped twice.");
        await FluentActions.Awaiting(() => svc.UpdateMappingsAsync(s.Uuid, new() { Mappings = [new() { SourceColumnIndex = 2, TargetFieldName = "nope" }] }, caller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Unknown field 'nope'.");

        var ok = await svc.UpdateMappingsAsync(s.Uuid, new() { Mappings = [new() { SourceColumnIndex = 2, TargetFieldName = "ref" }] }, caller);
        ok.Status.Should().Be(ImportSessionStatus.Mapping);
        ok.Mappings[2].Should().Match<ImportColumnMappingModel>(m => m.TargetFieldName == "ref" && !m.IsAutoMapped && m.ConfidenceScore == null);
    }

    [Fact]
    public async Task IMP_MAP_02_at_least_one_key_field_external_id_counts()
    {
        var def = FakeTarget.Products().Definition;
        var target = new FakeTarget(def with
        {
            Fields = def.Fields.Select(f => f.FieldName == "code" ? f with { IsRequired = false } : f).ToList()
        });
        var host = new ImportTestHost(target);
        var s = await host.UploadCsvAsync("Product Code,Product Name,Legacy ID\nP,N,L\n");
        var caller = ImportTestHost.Caller();
        await host.Sessions.UpdateMappingsAsync(s.Uuid, new()
            { Mappings = [new() { SourceColumnIndex = 0, TargetFieldName = null }, new() { SourceColumnIndex = 2, TargetFieldName = null }] }, caller);
        await FluentActions.Awaiting(() => host.Sessions.ValidateAsync(s.Uuid, caller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Map at least one key field: Product Code.");
        host.Db.ChangeTracker.Clear();
        var ok = await host.Sessions.UpdateMappingsAsync(s.Uuid, new()
            { Mappings = [new() { SourceColumnIndex = 0, TargetFieldName = null }, new() { SourceColumnIndex = 2, TargetFieldName = "external_id" }] }, caller);
        ok.Mappings.Single(m => m.TargetFieldName == "external_id").SourceColumnName.Should().Be("Legacy ID");
        (await host.Sessions.ValidateAsync(s.Uuid, caller)).Session.ValidRows.Should().Be(1);
    }

    [Fact]
    public async Task Only_an_importing_session_blocks_another_upload_or_execute_of_the_entity()
    {
        // Choosing another sheet re-uploads: the earlier sessions stay UPLOADED / MAPPING / VALIDATED and block nothing.
        var host = new ImportTestHost(FakeTarget.Products());
        var uploaded = await host.UploadCsvAsync(ImportTestHost.Csv(1));
        var mapping = await host.UploadCsvAsync(ImportTestHost.Csv(1));
        await host.Sessions.UpdateMappingsAsync(mapping.Uuid, new() { Mappings = [new() { SourceColumnIndex = 2, TargetFieldName = null }] }, ImportTestHost.Caller());
        var validated = await host.UploadCsvAsync(ImportTestHost.Csv(1));
        await host.Sessions.ValidateAsync(validated.Uuid, ImportTestHost.Caller());

        var last = await host.UploadCsvAsync(ImportTestHost.Csv(2, 5));
        await host.Sessions.ValidateAsync(last.Uuid, ImportTestHost.Caller());
        (await host.Execution.ExecuteAsync(last.Uuid, ImportTestHost.Caller())).Session.Status.Should().Be(ImportSessionStatus.Completed);
        (await host.Db.Sessions.AsNoTracking().Select(x => x.Status).ToListAsync())
            .Should().BeEquivalentTo([ImportSessionStatus.Uploaded, ImportSessionStatus.Mapping, ImportSessionStatus.Validated, ImportSessionStatus.Completed]);
    }

    [Fact]
    public async Task Mappings_cannot_change_once_the_import_started_and_a_change_resets_validation()
    {
        var host = new ImportTestHost(FakeTarget.Products());
        var s = await host.UploadCsvAsync(ImportTestHost.Csv(2));
        var caller = ImportTestHost.Caller();
        (await host.Sessions.ValidateAsync(s.Uuid, caller)).Session.ValidRows.Should().Be(2);

        var changed = await host.Sessions.UpdateMappingsAsync(s.Uuid, new() { Mappings = [new() { SourceColumnIndex = 2, TargetFieldName = null }] }, caller);
        changed.ValidRows.Should().Be(0);
        changed.ValidationCompletedAt.Should().BeNull();
        (await host.Db.Rows.AsNoTracking().Select(r => r.Status).Distinct().ToListAsync()).Should().Equal(ImportRowStatus.Pending);

        foreach (var status in new[] { ImportSessionStatus.Importing, ImportSessionStatus.Completed, ImportSessionStatus.Cancelled })
        {
            var row = await host.Db.Sessions.SingleAsync();
            row.Status = status;
            await host.Db.SaveChangesAsync();
            await FluentActions.Awaiting(() => host.Sessions.UpdateMappingsAsync(s.Uuid, new() { Mappings = [] }, caller))
                .Should().ThrowAsync<BadRequestException>().WithMessage("Mappings cannot change once the import has started.");
        }
    }

    [Fact]
    public async Task Presets_save_list_and_load_from_the_most_recent_session()
    {
        var host = new ImportTestHost(FakeTarget.Products());
        var caller = ImportTestHost.Caller();
        var first = await host.UploadCsvAsync("Col A,Col B,Col C\nP,N,1\n");
        await host.Sessions.UpdateMappingsAsync(first.Uuid, new()
        {
            Mappings = [new() { SourceColumnIndex = 0, TargetFieldName = "code" }, new() { SourceColumnIndex = 1, TargetFieldName = "name" },
                        new() { SourceColumnIndex = 2, TargetFieldName = "qty" }]
        }, caller);
        await host.Sessions.SavePresetAsync(first.Uuid, "Legacy ERP", caller);

        var presets = await host.Sessions.ListPresetsAsync("PRODUCT", caller);
        presets.Single().Name.Should().Be("Legacy ERP");

        // Same headers in a different order + an extra one: preset by column NAME, the rest auto-mapped.
        var second = await host.UploadCsvAsync("Col C,Selling Price,Col A,Col B\n1,2,P,N\n", preset: "legacy erp");
        second.Mappings.Select(m => m.TargetFieldName).Should().Equal("qty", "price", "code", "name");
        second.Mappings[0].IsAutoMapped.Should().BeFalse();
        second.Mappings[1].IsAutoMapped.Should().BeTrue();

        await FluentActions.Awaiting(() => host.UploadCsvAsync("A\n1\n", preset: "Nope")).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => host.Sessions.SavePresetAsync(first.Uuid, " ", caller)).Should().ThrowAsync<BadRequestException>();
    }

    // ── Validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Validation_runs_generic_checks_then_the_target_for_passing_rows_only()
    {
        var target = FakeTarget.Products();
        target.OnValidate = rows => rows.Select(r => new ImportRowCheck(r.RowNumber, null,
            r.Values["name"] == "Bad unit" ? [new ImportFieldError("unit", "LOOKUP_NOT_FOUND", "Unit 'KG' does not exist.", "KG")] : [])).ToList();
        var host = new ImportTestHost(target);
        var s = await host.UploadCsvAsync("Product Code,Product Name,Selling Price,Unit\nP-1,Ok,1,PCS\nP-2,,1,PCS\nP-3,Bad unit,1,KG\nP-1,Dup,1,PCS\nP-5,Money,abc,PCS\n");

        var result = await host.Sessions.ValidateAsync(s.Uuid, ImportTestHost.Caller());

        result.Session.Status.Should().Be(ImportSessionStatus.Validated);
        result.Session.ValidRows.Should().Be(1);
        result.Session.InvalidRows.Should().Be(4);
        result.ErrorCountByCode.Should().BeEquivalentTo(new Dictionary<string, int>
            { ["REQUIRED"] = 1, ["LOOKUP_NOT_FOUND"] = 1, ["DUPLICATE_IN_FILE"] = 1, ["INVALID_TYPE"] = 1 });
        result.Errors.Select(e => e.RowNumber).Should().Equal(3, 4, 5, 6);
        target.ValidateCalls.Single().Select(r => r.RowNumber).Should().Equal(2, 4);
        target.ValidateCalls.Single()[0].Values.Should().NotContainKey("external_id");

        var preview = await host.Sessions.PreviewAsync(s.Uuid, 20, ImportTestHost.Caller());
        preview.Columns.Select(c => c.FieldName).Should().Equal("code", "name", "price", "unit");
        preview.Rows[1].Issues!["name"].Should().Be("Product Name is required.");
        preview.Rows[0].Values["price"].Should().Be("1");

        var errors = await host.Sessions.ErrorsAsync(s.Uuid, 1, 2, ImportTestHost.Caller());
        errors.TotalRecords.Should().Be(4);
        errors.Data.Should().HaveCount(2);
        errors.Data[0].Values["code"].Should().Be("P-2");
    }

    [Theory]
    [InlineData("ERROR", true)]
    [InlineData("SKIP", false)]
    [InlineData("UPDATE", false)]
    [InlineData("CREATE_NEW", true)]
    public async Task Duplicate_in_db_for_strategy_error_and_create_new(string strategy, bool refused)
    {
        var target = FakeTarget.Products();
        target.Existing["P-0001"] = Guid.NewGuid();
        var host = new ImportTestHost(target);
        var s = await host.UploadCsvAsync(ImportTestHost.Csv(1), strategy: strategy);
        var r = await host.Sessions.ValidateAsync(s.Uuid, ImportTestHost.Caller());
        r.Session.InvalidRows.Should().Be(refused ? 1 : 0);
        if (refused) r.Errors.Single().Should().Match<ImportValidationError>(e => e.ErrorCode == "DUPLICATE_IN_DB" && e.Message == (strategy == "ERROR"
            ? "A record with the same Product Code already exists."
            : "CREATE_NEW cannot create a duplicate of an existing Product Code"));
        (await host.Db.Rows.SingleAsync()).ExistingEntityUuid.Should().Be(target.Existing["P-0001"]);
    }

    [Fact]
    public async Task All_generically_valid_rows_go_to_the_target_in_one_call_so_in_file_hierarchy_is_visible()
    {
        // A category-like target: "parent" (the price column here) must be a code earlier in the SAME call.
        var target = FakeTarget.Products();
        target.OnValidate = rows =>
        {
            var codes = new HashSet<string>();
            return rows.Select(r =>
            {
                var errors = new List<ImportFieldError>();
                if (r.Values.GetValueOrDefault("ref") is { } parent && !codes.Contains(parent))
                    errors.Add(new("ref", "MISSING_DEPENDENCY", "Parent missing.", parent));
                codes.Add(r.Values["code"]!);
                return new ImportRowCheck(r.RowNumber, null, errors);
            }).ToList();
        };
        var host = new ImportTestHost(target);
        var csv = "Product Code,Product Name,Reference\nAA-1,Root,\n" +
                  string.Concat(Enumerable.Range(2, 6000).Select(i => $"AA-{i},Child {i},{(i == 6001 ? "AA-1" : "")}\n"));
        var s = await host.UploadCsvAsync(csv);

        var r = await host.Sessions.ValidateAsync(s.Uuid, ImportTestHost.Caller());

        target.ValidateCalls.Should().ContainSingle().Which.Should().HaveCount(6001);
        r.Session.InvalidRows.Should().Be(0, "the parent in row 2 is visible to the child 6 000 rows later");
    }

    [Fact]
    public async Task External_id_resolves_before_the_target_match_and_wins()
    {
        var target = FakeTarget.Products();
        var byCode = Guid.NewGuid();
        var byRef = Guid.NewGuid();
        target.Existing["P-1"] = byCode;
        var host = new ImportTestHost(target);
        host.Db.ExternalReferences.Add(new ImportExternalReference
            { OrganizationId = ImportTestHost.Org, EntityType = "PRODUCT", ExternalId = "OLD-9", EntityUuid = byRef, CreatedAt = DateTime.UtcNow });
        // Another organization's reference with the same external id is invisible.
        host.Db.ExternalReferences.Add(new ImportExternalReference
            { OrganizationId = Guid.NewGuid(), EntityType = "PRODUCT", ExternalId = "OLD-1", EntityUuid = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
        await host.Db.SaveChangesAsync();

        var s = await host.UploadCsvAsync("ext_id,Product Code,Product Name\nOLD-9,P-1,N\nOLD-1,P-2,M\n", strategy: "UPDATE");
        await host.Sessions.ValidateAsync(s.Uuid, ImportTestHost.Caller());

        target.ValidateCalls.Single()[0].ExistingEntityUuid.Should().Be(byRef);
        target.ValidateCalls.Single()[1].ExistingEntityUuid.Should().BeNull();
        var rows = await host.Db.Rows.AsNoTracking().OrderBy(r => r.RowNumber).ToListAsync();
        rows[0].ExistingEntityUuid.Should().Be(byRef, "the external reference wins over the code match");
        rows[0].ExternalId.Should().Be("OLD-9");
    }

    [Fact]
    public async Task Sessions_are_visible_to_their_owner_or_to_import_managers()
    {
        var host = new ImportTestHost(FakeTarget.Products());
        var mine = await host.UploadCsvAsync(ImportTestHost.Csv(1), caller: ImportTestHost.Caller(7));
        await host.UploadCsvAsync(ImportTestHost.Csv(1), caller: ImportTestHost.Caller(8));

        (await host.Sessions.ListAsync(new(), ImportTestHost.Caller(7))).TotalRecords.Should().Be(1);
        (await host.Sessions.ListAsync(new(), ImportTestHost.Caller(9, PermissionCodes.IMPORT_MANAGE))).TotalRecords.Should().Be(2);
        await FluentActions.Awaiting(() => host.Sessions.GetAsync(mine.Uuid, ImportTestHost.Caller(8))).Should().ThrowAsync<NotFoundException>();
        (await host.Sessions.GetAsync(mine.Uuid, ImportTestHost.Caller(9, PermissionCodes.IMPORT_MANAGE))).Uuid.Should().Be(mine.Uuid);

        // Another organization's session does not exist for this one.
        host.Tenant.OrganizationId = Guid.NewGuid();
        await FluentActions.Awaiting(() => host.Sessions.GetAsync(mine.Uuid, ImportTestHost.Admin)).Should().ThrowAsync<NotFoundException>();
    }
}
