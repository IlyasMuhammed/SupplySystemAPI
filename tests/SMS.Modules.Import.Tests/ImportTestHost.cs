using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Import.Data;
using SMS.Modules.Import.Hubs;
using SMS.Modules.Import.Models;
using SMS.Modules.Import.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;

namespace SMS.Modules.Import.Tests;

/// <summary>A configurable in-memory IImportTarget: records every call; Validate/Apply behaviour is pluggable.</summary>
internal sealed class FakeTarget : IImportTarget
{
    public ImportProfileDefinition Definition { get; set; }
    public List<ImportTemplateList> Lists { get; } = new();
    public Func<IReadOnlyList<ImportRowInput>, IReadOnlyList<ImportRowCheck>>? OnValidate { get; set; }
    public Func<IReadOnlyList<ImportApplyRow>, IReadOnlyList<ImportRowOutcome>>? OnApply { get; set; }
    public List<IReadOnlyList<ImportRowInput>> ValidateCalls { get; } = new();
    public List<IReadOnlyList<ImportApplyRow>> ApplyCalls { get; } = new();
    /// <summary>Natural key (code) → existing uuid, used by the default Validate.</summary>
    public Dictionary<string, Guid> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<Guid, Dictionary<string, string?>> Store { get; } = new();

    public FakeTarget(ImportProfileDefinition definition) => Definition = definition;

    public ImportProfileDefinition Describe() => Definition;

    public Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImportTemplateList>>(Lists);

    public Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default)
    {
        ValidateCalls.Add(rows);
        if (OnValidate is not null) return Task.FromResult(OnValidate(rows));
        var key = Definition.MatchFields.FirstOrDefault();
        return Task.FromResult<IReadOnlyList<ImportRowCheck>>(rows.Select(r =>
            new ImportRowCheck(r.RowNumber,
                key is not null && r.Values.TryGetValue(key, out var v) && v is not null && Existing.TryGetValue(v, out var u) ? u : null,
                [])).ToList());
    }

    public Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default)
    {
        ApplyCalls.Add(rows);
        if (OnApply is not null) return Task.FromResult(OnApply(rows));
        var key = Definition.MatchFields.FirstOrDefault();
        return Task.FromResult<IReadOnlyList<ImportRowOutcome>>(rows.Select(r =>
        {
            if (r.Action == "UPDATE")
            {
                Store[r.ExistingEntityUuid!.Value] = r.Values.ToDictionary(k => k.Key, k => k.Value);
                return new ImportRowOutcome(r.RowNumber, "UPDATED", r.ExistingEntityUuid!.Value);
            }
            var uuid = Guid.NewGuid();
            Store[uuid] = r.Values.ToDictionary(k => k.Key, k => k.Value);
            if (key is not null && r.Values.GetValueOrDefault(key) is { } code) Existing[code] = uuid;
            return new ImportRowOutcome(r.RowNumber, "IMPORTED", uuid);
        }).ToList());
    }

    /// <summary>A product-like profile: code (key, required), name (required), price (decimal 2), active (bool),
    /// opened (date), email, phone, unit (lookup "units").</summary>
    public static FakeTarget Products(string entityType = "PRODUCT", int order = 3, bool opening = false, bool allowUpdate = true) =>
        new(new ImportProfileDefinition(entityType, opening ? "Opening stock" : "Product", "Products and their default variant.", order, opening,
            ["code"],
            [
                new("code", "Product Code", "Unique code.", ImportFieldTypes.Text, true, true, 1, "P-001", MaxLength: 20, Aliases: ["sku"]),
                new("name", "Product Name", null, ImportFieldTypes.Text, true, false, 2, "Widget", MaxLength: 50),
                new("price", "Selling Price", null, ImportFieldTypes.Decimal, false, false, 3, "12.50", MinValue: 0, MaxValue: 100000, DecimalPlaces: 2),
                new("qty", "Quantity", null, ImportFieldTypes.Integer, false, false, 4, "5", MinValue: 0),
                new("active", "Is Active", null, ImportFieldTypes.Boolean, false, false, 5, "Yes", DefaultValue: "true"),
                new("opened", "Opened On", null, ImportFieldTypes.Date, false, false, 6, "2026-01-31"),
                new("email", "Contact Email", null, ImportFieldTypes.Email, false, false, 7, "a@b.com"),
                new("phone", "Contact Phone", null, ImportFieldTypes.Phone, false, false, 8, "+92 300 1234567"),
                new("unit", "Unit", null, ImportFieldTypes.Lookup, false, false, 9, "PCS", LookupList: "units"),
                new("ref", "Reference", null, ImportFieldTypes.Text, false, false, 10, "AB-12", ValidationRegex: "^[A-Z]{2}-[0-9]+$"),
            ], allowUpdate));
}

internal sealed class FakeGate : IModuleGate
{
    public HashSet<string> Off { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) =>
        Task.FromResult(!Off.Contains(featureCode));
}

internal sealed class FakeOpeningDate : IOpeningBalanceDateProvider
{
    public DateOnly? Date { get; set; } = new DateOnly(2026, 1, 1);
    public Task<DateOnly?> GetAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult(Date);
}

internal sealed class FakeScheduler : IImportJobScheduler
{
    public List<(Guid Org, Guid Session, int User)> Jobs { get; } = new();
    /// <summary>Job id → liveness; unknown ids are dead.</summary>
    public Dictionary<string, bool?> Alive { get; } = new();
    public bool? IsAlive(string jobId) => Alive.TryGetValue(jobId, out var alive) ? alive : false;
    public string Enqueue(Guid organizationId, Guid sessionUuid, int userId)
    {
        Jobs.Add((organizationId, sessionUuid, userId));
        return "job-" + Jobs.Count;
    }
}

internal sealed class FakeHub : IImportHubNotifier
{
    public List<ImportProgressModel> Events { get; } = new();
    public Task ProgressAsync(Guid org, IEnumerable<int> userIds, ImportProgressModel progress)
    {
        Events.Add(progress);
        return Task.CompletedTask;
    }
}

/// <summary>Everything the import services need, over one InMemory database.</summary>
internal sealed class ImportTestHost
{
    public static readonly Guid Org = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public string DbName { get; } = Guid.NewGuid().ToString();
    public StaticTenantContext Tenant { get; } = new() { OrganizationId = Org };
    public ImportDbContext Db { get; }
    public List<IImportTarget> Targets { get; } = new();
    public FakeGate Gate { get; } = new();
    public FakeOpeningDate OpeningDate { get; } = new();
    public FakeScheduler Scheduler { get; } = new();
    public FakeHub Hub { get; } = new();

    public ImportTestHost(params IImportTarget[] targets)
    {
        Targets.AddRange(targets);
        Db = NewDb();
    }

    public ImportDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ImportDbContext>().UseInMemoryDatabase(DbName).Options, Tenant);

    public ImportTargetRegistry Registry => new(Targets);
    public ImportOpeningGate OpeningGate => new(Gate, OpeningDate);
    public ImportSessionService Sessions => new(Db, Registry, Tenant, OpeningGate);
    public ImportProfileService Profiles => new(Db, Registry, Tenant);
    public ImportExecutionService Execution => new(Db, Registry, Tenant, OpeningGate, Sessions, Scheduler, Hub);
    public ImportWorkbookService Workbooks => new(Db, Registry, Tenant, Sessions);

    public static ImportCaller Caller(int userId = 7, params string[] permissions) =>
        new(userId, "user@test", (permissions.Length == 0
            ? new[] { PermissionCodes.IMPORT_PROFILE_VIEW, PermissionCodes.IMPORT_UPLOAD, PermissionCodes.IMPORT_EXECUTE }
            : permissions).ToHashSet(StringComparer.OrdinalIgnoreCase));

    public static readonly ImportCaller Admin = new(1, "admin@test", PermissionCodes.All.ToHashSet(StringComparer.OrdinalIgnoreCase));

    public Task<ImportSessionDetail> UploadCsvAsync(string csv, string entityType = "PRODUCT", ImportCaller? caller = null,
        string? strategy = null, string? preset = null, string fileName = "data.csv")
    {
        var bytes = Encoding.UTF8.GetBytes(csv);
        return Sessions.UploadAsync(new MemoryStream(bytes), fileName, bytes.Length,
            new ImportUploadRequest { EntityType = entityType, DuplicateStrategy = strategy, MappingPreset = preset }, caller ?? Caller());
    }

    public Task<ImportSessionDetail> UploadBytesAsync(byte[] bytes, string fileName, string entityType = "PRODUCT", string? sheet = null,
        ImportCaller? caller = null) =>
        Sessions.UploadAsync(new MemoryStream(bytes), fileName, bytes.Length,
            new ImportUploadRequest { EntityType = entityType, SheetName = sheet }, caller ?? Caller());

    /// <summary>Header "Product Code,Product Name,Selling Price" + one row per code.</summary>
    public static string Csv(int rows, int start = 1) =>
        "Product Code,Product Name,Selling Price\n" +
        string.Concat(Enumerable.Range(start, rows).Select(i => $"P-{i:0000},Item {i},{i}.50\n"));

    public static byte[] Xlsx(Action<XLWorkbook> build)
    {
        using var wb = new XLWorkbook();
        build(wb);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
