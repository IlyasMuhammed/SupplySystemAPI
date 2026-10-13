using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Lookups.Domain;
using SMS.Modules.Lookups.Import;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Lookups.Tests;

/// <summary>A40 (TINV) — UNIT import target: units of measure are Lookups "uom" values named "Name (CODE)".</summary>
public class UnitImportTargetTests
{
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly string _dbName = Guid.NewGuid().ToString();
    private Guid _typeId;

    // A super admin's context: the target must scope by the organization it is given.
    private LookupsDbContext Db() =>
        new(new DbContextOptionsBuilder<LookupsDbContext>().UseInMemoryDatabase(_dbName).Options,
            new StaticTenantContext { OrganizationId = Guid.NewGuid(), IsSuperAdmin = true });

    private async Task<LookupsDbContext> SeededAsync()
    {
        var db = Db();
        var type = new LookupType { Id = Guid.NewGuid(), Slug = "uom", Name = "Unit of Measure", IsActive = true };
        _typeId = type.Id;
        db.LookupTypes.Add(type);
        db.LookupValues.AddRange(
            new LookupValue { Id = Guid.NewGuid(), TypeId = type.Id, DisplayName = "Piece (PCS)", IsGlobal = true, SortOrder = 1 },
            new LookupValue { Id = Guid.NewGuid(), TypeId = type.Id, DisplayName = "Kilogram (KG)", IsGlobal = true, SortOrder = 2 },
            new LookupValue { Id = Guid.NewGuid(), TypeId = type.Id, DisplayName = "Drum (DRM)", IsGlobal = false, OrganizationId = _org, SortOrder = 3 },
            new LookupValue { Id = Guid.NewGuid(), TypeId = type.Id, DisplayName = "Bale (BALE)", IsGlobal = false, OrganizationId = _other, SortOrder = 4 });
        await db.SaveChangesAsync();
        return db;
    }

    private static ImportRowInput Row(int n, string? code, string? name) =>
        new(n, new Dictionary<string, string?> { ["code"] = code, ["name"] = name }, null);

    private static ImportApplyRow Apply(int n, string action, string? code, string? name, Guid? existing = null) =>
        new(n, new Dictionary<string, string?> { ["code"] = code, ["name"] = name }, action, existing);

    [Fact]
    public void It_describes_the_unit_profile()
    {
        var d = new UnitImportTarget(Db()).Describe();
        (d.EntityType, d.ImportOrder, d.IsOpeningBalance).Should().Be((ImportEntityTypes.Unit, 1, false));
        d.MatchFields.Should().Equal("code");
        d.Fields.Select(f => (f.FieldName, f.IsRequired, f.IsKey)).Should().Equal(("code", true, true), ("name", true, false));
    }

    [Fact]
    public async Task The_units_list_is_the_global_catalog_plus_the_organizations_own_codes()
    {
        await using var db = await SeededAsync();
        var lists = await new UnitImportTarget(db).GetTemplateListsAsync(_org);
        lists.Single().Key.Should().Be("units");
        lists.Single().Values.Should().Equal("PCS", "KG", "DRM");
    }

    [Fact]
    public async Task Validation_matches_codes_and_flags_duplicates_and_missing_names()
    {
        await using var db = await SeededAsync();
        var checks = await new UnitImportTarget(db).ValidateAsync(_org,
        [
            Row(2, "pcs", "Piece"),        // global unit → existing
            Row(3, "DRM", null),           // own unit, name may be empty on a match
            Row(4, "BOX", "Box"),          // new
            Row(5, "box", "Box again"),    // in-file duplicate
            Row(6, "BALE", null),          // another organization's unit is not ours: new, needs a name
            Row(7, "B@D", "Bad"),
            Row(8, "TUB", "Drum (DRM)")    // display name collides with an existing unit
        ]);

        checks[0].ExistingEntityUuid.Should().NotBeNull();
        checks[0].Errors.Should().BeEmpty();
        checks[1].ExistingEntityUuid.Should().NotBeNull();
        checks[1].Errors.Should().BeEmpty();
        checks[2].ExistingEntityUuid.Should().BeNull();
        checks[2].Errors.Should().BeEmpty();
        checks[3].Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_IN_FILE");
        checks[4].ExistingEntityUuid.Should().BeNull();
        checks[4].Errors.Should().ContainSingle(e => e.Code == "REQUIRED" && e.Field == "name");
        checks[5].Errors.Should().ContainSingle(e => e.Code == "PATTERN_MISMATCH");
        checks[6].Errors.Should().BeEmpty("'Drum (DRM)' with code TUB becomes 'Drum (DRM) (TUB)'");
    }

    [Fact]
    public async Task Apply_creates_org_units_updates_own_ones_and_never_changes_a_global_unit()
    {
        await using var db = await SeededAsync();
        var target = new UnitImportTarget(db);
        var checks = await target.ValidateAsync(_org, [Row(2, "PCS", "Pieces"), Row(3, "DRM", "Steel drum")]);

        var outcomes = await target.ApplyAsync(_org, 1,
        [
            Apply(2, "UPDATE", "PCS", "Pieces", checks[0].ExistingEntityUuid),
            Apply(3, "UPDATE", "DRM", "Steel drum", checks[1].ExistingEntityUuid),
            Apply(4, "CREATE", "box", "Box")
        ]);

        outcomes.Select(o => o.Status).Should().Equal("SKIPPED", "UPDATED", "IMPORTED");
        await using var fresh = Db();
        var values = await fresh.LookupValues.IgnoreQueryFilters().Where(v => v.TypeId == _typeId).ToListAsync();
        values.Single(v => v.Id == checks[0].ExistingEntityUuid).DisplayName.Should().Be("Piece (PCS)");
        values.Single(v => v.Id == checks[1].ExistingEntityUuid).DisplayName.Should().Be("Steel drum (DRM)");
        var box = values.Single(v => v.Id == outcomes[2].EntityUuid);
        (box.DisplayName, box.IsGlobal, box.OrganizationId, box.IsActive).Should().Be(("Box (BOX)", false, (Guid?)_org, true));

        // A null name on UPDATE keeps the value.
        await new UnitImportTarget(fresh).ApplyAsync(_org, 1, [Apply(2, "UPDATE", "BOX", null, box.Id)]);
        await using var again = Db();
        (await again.LookupValues.IgnoreQueryFilters().SingleAsync(v => v.Id == box.Id)).DisplayName.Should().Be("Box (BOX)");
        (await new UnitImportTarget(again).GetTemplateListsAsync(_org)).Single().Values.Should().Contain("BOX");
        (await new UnitImportTarget(again).GetTemplateListsAsync(_other)).Single().Values.Should().NotContain("BOX").And.Contain("BALE");
    }

    [Fact]
    public async Task A_bad_row_fails_the_whole_batch()
    {
        await using var db = await SeededAsync();
        var act = () => new UnitImportTarget(db).ApplyAsync(_org, 1,
            [Apply(2, "CREATE", "NEW1", "New one"), Apply(3, "CREATE", "PCS", "Piece")]);

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*Row 3*");
        await using var fresh = Db();
        (await fresh.LookupValues.IgnoreQueryFilters().AnyAsync(v => v.DisplayName.Contains("NEW1"))).Should().BeFalse();
    }

    [Theory]
    [InlineData("Piece (PCS)", "PCS")]
    [InlineData("Kilogram (kg)", "KG")]
    [InlineData("Dozen", "DOZEN")]
    public void The_code_is_the_trailing_parenthesis(string displayName, string code) =>
        UnitImportTarget.CodeOf(displayName).Should().Be(code);
}
