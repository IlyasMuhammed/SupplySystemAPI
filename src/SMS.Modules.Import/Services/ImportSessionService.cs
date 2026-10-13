using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Import.Data;
using SMS.Modules.Import.Domain;
using SMS.Modules.Import.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Import.Services;

public interface IImportSessionService
{
    Task<ImportSessionDetail> UploadAsync(Stream content, string fileName, long length, ImportUploadRequest req, ImportCaller caller, CancellationToken ct = default);
    Task<PaginatedResponse<ImportSessionListItem>> ListAsync(ImportSessionFilter filter, ImportCaller caller, CancellationToken ct = default);
    Task<ImportSessionDetail> GetAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default);
    Task<ImportPreviewModel> PreviewAsync(Guid uuid, int rows, ImportCaller caller, CancellationToken ct = default);
    Task<IReadOnlyList<ImportColumnMappingModel>> GetMappingsAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default);
    Task<ImportSessionDetail> UpdateMappingsAsync(Guid uuid, ImportMappingsRequest req, ImportCaller caller, CancellationToken ct = default);
    Task<IReadOnlyList<ImportPresetModel>> ListPresetsAsync(string? entityType, ImportCaller caller, CancellationToken ct = default);
    Task<ImportSessionDetail> SavePresetAsync(Guid uuid, string? presetName, ImportCaller caller, CancellationToken ct = default);
    Task<ImportValidationResult> ValidateAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default);
    Task<ImportSessionDetail> CancelAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default);
    Task<ImportProgressModel> ProgressAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default);
    Task<PaginatedResponse<ImportErrorRowModel>> ErrorsAsync(Guid uuid, int page, int pageSize, ImportCaller caller, CancellationToken ct = default);
}

/// <summary>A40 C3/C6/C7 — sessions: upload and parse, mappings (auto, manual, presets), validation, cancel, errors.</summary>
internal sealed class ImportSessionService : IImportSessionService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ImportDbContext _db;
    private readonly ImportTargetRegistry _registry;
    private readonly ITenantContext _tenant;
    private readonly ImportOpeningGate _opening;
    private readonly IUserQueryService? _users;

    public ImportSessionService(ImportDbContext db, ImportTargetRegistry registry, ITenantContext tenant, ImportOpeningGate opening,
        IUserQueryService? users = null)
    {
        _db = db; _registry = registry; _tenant = tenant; _opening = opening; _users = users;
    }

    private Guid Org => _tenant.OrganizationId;

    // ── Upload ───────────────────────────────────────────────────────────────

    public async Task<ImportSessionDetail> UploadAsync(Stream content, string fileName, long length, ImportUploadRequest req, ImportCaller caller, CancellationToken ct = default)
    {
        var entry = _registry.Get(req.EntityType);
        var def = entry.Definition;
        await _opening.EnsureAllowedAsync(Org, def, caller, ct);
        var settings = await ImportProfileService.EffectiveAsync(_db, Org, def, ct);

        var strategy = string.IsNullOrWhiteSpace(req.DuplicateStrategy) ? settings.DuplicateStrategy : req.DuplicateStrategy.Trim().ToUpperInvariant();
        if (!ImportDuplicateStrategy.All.Contains(strategy))
            throw new BadRequestException("Duplicate strategy must be SKIP, UPDATE, CREATE_NEW or ERROR.");
        if (def.IsOpeningBalance && strategy != ImportDuplicateStrategy.Error) throw new BadRequestException(ImportMessages.OpeningStrategy);
        if (strategy == ImportDuplicateStrategy.Update && !def.AllowUpdate) throw new BadRequestException(ImportMessages.UpdateNotAllowed);

        // IMP-FILE-08 — one running import per entity type and organization.
        if (await _db.Sessions.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == Org && s.EntityType == def.EntityType
                                                                  && s.Status == ImportSessionStatus.Importing, ct))
            throw new BadRequestException(ImportMessages.AlreadyRunning(def.Name));

        var parsed = ImportFileParser.Parse(content, fileName, length, req.SheetName, settings.MaxRowsPerFile, settings.MaxFileSizeMb);

        var session = new ImportSession
        {
            OrganizationId = Org, EntityType = def.EntityType, FileName = Path.GetFileName(fileName), FileType = parsed.FileType,
            FileSizeBytes = length, SheetName = parsed.SheetName, SheetNamesJson = JsonSerializer.Serialize(parsed.SheetNames, Json),
            HeadersJson = JsonSerializer.Serialize(parsed.Headers, Json), Status = ImportSessionStatus.Uploaded,
            DuplicateStrategy = strategy, Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
            TotalRows = parsed.Rows.Count, CreatedBy = caller.UserId, CreatedAt = DateTime.UtcNow,
            CreatedByName = await NameAsync(caller)
        };
        session.Mappings = await BuildMappingsAsync(entry, parsed.Headers, req.MappingPreset, ct);
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync(ct);

        _db.Rows.AddRange(parsed.Rows.Select(r => new ImportRow
        {
            SessionId = session.Id, OrganizationId = Org, RowNumber = r.RowNumber, RawJson = JsonSerializer.Serialize(r.Values, Json),
            Status = ImportRowStatus.Pending
        }));
        await _db.SaveChangesAsync(ct);
        return Detail(session, entry);
    }

    private async Task<string?> NameAsync(ImportCaller caller)
    {
        if (_users is not null && caller.UserId > 0)
        {
            try
            {
                if (await _users.GetUserAsync(caller.UserId) is { } user && !string.IsNullOrWhiteSpace(user.DisplayName)) return user.DisplayName;
            }
            catch (Exception) { /* a name is cosmetic */ }
        }
        return caller.Name;
    }

    /// <summary>Preset mappings first (by column name, from the most recent session saved under that name for this entity
    /// type), then automatic mapping of the remaining columns to the remaining fields.</summary>
    private async Task<List<ImportColumnMapping>> BuildMappingsAsync(ImportTargetEntry entry, IReadOnlyList<string> headers, string? preset, CancellationToken ct)
    {
        var result = headers.Select((h, i) => new ImportColumnMapping { OrganizationId = Org, SourceColumnIndex = i, SourceColumnName = Truncate(h, 300) }).ToList();
        var usedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(preset))
        {
            var name = preset.Trim();
            var lower = name.ToLower();
            var source = await _db.Sessions.IgnoreQueryFilters().AsNoTracking().Include(s => s.Mappings)
                .Where(s => s.OrganizationId == Org && s.EntityType == entry.Definition.EntityType && s.SavedMappingName != null && s.SavedMappingName.ToLower() == lower)
                .OrderByDescending(s => s.SavedMappingAt).ThenByDescending(s => s.Id).FirstOrDefaultAsync(ct);
            if (source is null) throw new BadRequestException($"Mapping preset '{name}' was not found.");
            var byColumn = source.Mappings.Where(m => m.TargetFieldName is not null && entry.Field(m.TargetFieldName) is not null)
                .GroupBy(m => ImportAutoMapper.Normalize(m.SourceColumnName)).ToDictionary(g => g.Key, g => g.First().TargetFieldName!);
            foreach (var m in result)
            {
                if (!byColumn.TryGetValue(ImportAutoMapper.Normalize(m.SourceColumnName), out var field) || usedFields.Contains(field)) continue;
                m.TargetFieldName = entry.Field(field)!.FieldName;
                m.IsAutoMapped = false;
                m.ConfidenceScore = null;
                usedFields.Add(field);
            }
        }

        var freeColumns = result.Where(m => m.TargetFieldName is null).ToList();
        var freeFields = entry.Fields.Where(f => !usedFields.Contains(f.FieldName)).ToList();
        var auto = ImportAutoMapper.Map(freeColumns.Select(c => c.SourceColumnName).ToList(), freeFields);
        foreach (var match in auto.Where(a => a.FieldName is not null))
        {
            var m = freeColumns[match.ColumnIndex];
            m.TargetFieldName = match.FieldName;
            m.IsAutoMapped = true;
            m.ConfidenceScore = match.Confidence;
        }
        return result;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    // ── Reads ────────────────────────────────────────────────────────────────

    internal async Task<ImportSession> LoadAsync(Guid uuid, ImportCaller caller, bool track, CancellationToken ct)
    {
        IQueryable<ImportSession> q = _db.Sessions.IgnoreQueryFilters().Include(s => s.Mappings);
        if (!track) q = q.AsNoTracking();
        var session = await q.FirstOrDefaultAsync(s => s.Uuid == uuid && s.OrganizationId == Org, ct);
        // IMPORT_UPLOAD sees its own sessions; IMPORT_MANAGE every session of the organization.
        if (session is null || (!caller.CanManageAll && session.CreatedBy != caller.UserId))
            throw new NotFoundException("Import session not found.");
        return session;
    }

    public async Task<PaginatedResponse<ImportSessionListItem>> ListAsync(ImportSessionFilter filter, ImportCaller caller, CancellationToken ct = default)
    {
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, 100);
        var q = _db.Sessions.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == Org);
        if (!caller.CanManageAll) q = q.Where(s => s.CreatedBy == caller.UserId);
        if (!string.IsNullOrWhiteSpace(filter.EntityType)) q = q.Where(s => s.EntityType == filter.EntityType.Trim());
        if (!string.IsNullOrWhiteSpace(filter.Status)) q = q.Where(s => s.Status == filter.Status.Trim());
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PaginatedResponse<ImportSessionListItem>
        {
            Data = rows.Select(s => Fill(new ImportSessionListItem(), s)).ToList(),
            TotalRecords = total, Page = page, PageSize = size, TotalPages = (int)Math.Ceiling(total / (double)size)
        };
    }

    public async Task<ImportSessionDetail> GetAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default)
    {
        var session = await LoadAsync(uuid, caller, false, ct);
        return Detail(session, Entry(session));
    }

    private ImportTargetEntry? Entry(ImportSession s) => _registry.TryGet(s.EntityType, out var e) ? e : null;

    private T Fill<T>(T item, ImportSession s) where T : ImportSessionListItem
    {
        item.Uuid = s.Uuid; item.EntityType = s.EntityType;
        item.EntityName = Entry(s)?.Definition.Name ?? s.EntityType;
        item.FileName = s.FileName; item.Status = s.Status; item.TotalRows = s.TotalRows; item.ValidRows = s.ValidRows;
        item.InvalidRows = s.InvalidRows; item.ImportedRows = s.ImportedRows; item.UpdatedRows = s.UpdatedRows;
        item.SkippedRows = s.SkippedRows; item.ErrorRows = s.ErrorRows; item.CreatedAt = s.CreatedAt; item.CreatedByName = s.CreatedByName;
        return item;
    }

    internal ImportSessionDetail Detail(ImportSession s, ImportTargetEntry? entry)
    {
        var d = Fill(new ImportSessionDetail(), s);
        d.FileType = s.FileType; d.FileSizeBytes = s.FileSizeBytes; d.SheetName = s.SheetName;
        d.SheetNames = JsonSerializer.Deserialize<List<string>>(s.SheetNamesJson, Json) ?? [];
        d.DuplicateStrategy = s.DuplicateStrategy; d.Notes = s.Notes; d.SavedMappingName = s.SavedMappingName;
        d.ValidationCompletedAt = s.ValidationCompletedAt; d.ImportStartedAt = s.ImportStartedAt; d.ImportCompletedAt = s.ImportCompletedAt;
        d.ErrorMessage = s.ErrorMessage;
        d.Mappings = MappingModels(s, entry);
        d.UnmappedColumns = s.Mappings.Where(m => m.TargetFieldName is null).OrderBy(m => m.SourceColumnIndex).Select(m => m.SourceColumnName).ToList();
        var mapped = s.Mappings.Where(m => m.TargetFieldName is not null).Select(m => m.TargetFieldName!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        d.UnmappedRequiredFields = entry?.Fields.Where(f => f.IsRequired && f.DefaultValue is null && !mapped.Contains(f.FieldName))
            .Select(f => f.FieldName).ToList() ?? [];
        return d;
    }

    private static IReadOnlyList<ImportColumnMappingModel> MappingModels(ImportSession s, ImportTargetEntry? entry) =>
        s.Mappings.OrderBy(m => m.SourceColumnIndex).Select(m => new ImportColumnMappingModel
        {
            SourceColumnIndex = m.SourceColumnIndex, SourceColumnName = m.SourceColumnName, TargetFieldName = m.TargetFieldName,
            TargetDisplayName = m.TargetFieldName is null ? null : entry?.Field(m.TargetFieldName)?.DisplayName,
            IsAutoMapped = m.IsAutoMapped, ConfidenceScore = m.ConfidenceScore
        }).ToList();

    public async Task<IReadOnlyList<ImportColumnMappingModel>> GetMappingsAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default)
    {
        var session = await LoadAsync(uuid, caller, false, ct);
        return MappingModels(session, Entry(session));
    }

    public async Task<ImportPreviewModel> PreviewAsync(Guid uuid, int rows, ImportCaller caller, CancellationToken ct = default)
    {
        var session = await LoadAsync(uuid, caller, false, ct);
        var entry = Entry(session);
        var take = Math.Clamp(rows <= 0 ? 20 : rows, 1, 200);
        var mapped = session.Mappings.Where(m => m.TargetFieldName is not null).OrderBy(m => m.SourceColumnIndex).ToList();
        var data = await _db.Rows.IgnoreQueryFilters().AsNoTracking().Where(r => r.SessionId == session.Id)
            .OrderBy(r => r.RowNumber).Take(take).ToListAsync(ct);
        return new ImportPreviewModel
        {
            Columns = mapped.Select(m => new ImportPreviewColumn(m.TargetFieldName!, entry?.Field(m.TargetFieldName!)?.DisplayName ?? m.TargetFieldName!)).ToList(),
            Rows = data.Select(r =>
            {
                var raw = MappedRaw(r, mapped);
                var parsed = r.ValuesJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string?>>(r.ValuesJson, Json);
                var values = mapped.ToDictionary(m => m.TargetFieldName!, m =>
                    parsed is not null && parsed.TryGetValue(m.TargetFieldName!, out var v) ? v : raw.GetValueOrDefault(m.TargetFieldName!));
                var errors = Errors(r);
                return new ImportPreviewRow
                {
                    RowNumber = r.RowNumber, Values = values,
                    Issues = errors.Count == 0 ? null : errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => string.Join(" ", g.Select(e => e.Message)))
                };
            }).ToList()
        };
    }

    internal static Dictionary<string, string?> MappedRaw(ImportRow row, IReadOnlyList<ImportColumnMapping> mapped)
    {
        var raw = JsonSerializer.Deserialize<string?[]>(row.RawJson, Json) ?? [];
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mapped.Where(m => m.TargetFieldName is not null))
            result[m.TargetFieldName!] = m.SourceColumnIndex < raw.Length ? raw[m.SourceColumnIndex] : null;
        return result;
    }

    internal static List<ImportFieldError> Errors(ImportRow row) =>
        row.ErrorsJson is null ? [] : JsonSerializer.Deserialize<List<ImportFieldError>>(row.ErrorsJson, Json) ?? [];

    // ── Mappings ─────────────────────────────────────────────────────────────

    public async Task<ImportSessionDetail> UpdateMappingsAsync(Guid uuid, ImportMappingsRequest req, ImportCaller caller, CancellationToken ct = default)
    {
        var session = await LoadAsync(uuid, caller, true, ct);
        if (session.Status == ImportSessionStatus.Importing || ImportSessionStatus.Finished.Contains(session.Status))
            throw new BadRequestException(ImportMessages.MappingsLocked);
        var entry = _registry.Get(session.EntityType);
        try
        {
            ApplyMappingChanges(session, entry, req?.Mappings ?? []);
            // An incomplete mapping is saved (the wizard saves as the user works); only double mapping (IMP-MAP-03) is
            // refused here. Required / key fields (IMP-MAP-01/02) are enforced by validate.
            CheckNoDoubleMapping(entry, session.Mappings);
        }
        catch (BadRequestException)
        {
            _db.ChangeTracker.Clear(); // a refused change leaves nothing behind in this scope
            throw;
        }
        await ResetValidationAsync(session, ct);
        session.Status = ImportSessionStatus.Mapping;
        await _db.SaveChangesAsync(ct);
        return Detail(session, entry);
    }

    /// <summary>IMP-MAP-03 — no field mapped from two columns.</summary>
    internal static void CheckNoDoubleMapping(ImportTargetEntry entry, IReadOnlyList<ImportColumnMapping> mappings)
    {
        foreach (var g in mappings.Where(m => m.TargetFieldName is not null)
                     .GroupBy(m => m.TargetFieldName!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            throw new BadRequestException(ImportMessages.FieldTwice(entry.Field(g.Key)?.DisplayName ?? g.Key));
    }

    private static void ApplyMappingChanges(ImportSession session, ImportTargetEntry entry, IReadOnlyList<ImportMappingItem> items)
    {

        // IMP-MAP-03 — a column listed twice in the request.
        foreach (var g in items.GroupBy(i => i.SourceColumnIndex).Where(g => g.Count() > 1))
        {
            var name = session.Mappings.FirstOrDefault(m => m.SourceColumnIndex == g.Key)?.SourceColumnName ?? g.Key.ToString();
            throw new BadRequestException(ImportMessages.ColumnTwice(name));
        }

        foreach (var item in items)
        {
            var mapping = session.Mappings.FirstOrDefault(m => m.SourceColumnIndex == item.SourceColumnIndex)
                          ?? throw new BadRequestException(ImportMessages.UnknownColumn(item.SourceColumnIndex));
            string? target = null;
            if (!string.IsNullOrWhiteSpace(item.TargetFieldName))
                target = (entry.Field(item.TargetFieldName.Trim()) ?? throw new BadRequestException(ImportMessages.UnknownField(item.TargetFieldName.Trim()))).FieldName;
            if (string.Equals(mapping.TargetFieldName, target, StringComparison.OrdinalIgnoreCase)) continue;
            mapping.TargetFieldName = target;
            mapping.IsAutoMapped = false;
            mapping.ConfidenceScore = null;
        }
    }

    /// <summary>IMP-MAP-01..03 — required fields mapped, at least one key field mapped (external_id counts), no field twice.</summary>
    internal static void CheckMappingRules(ImportTargetEntry entry, IReadOnlyList<ImportColumnMapping> mappings)
    {
        CheckNoDoubleMapping(entry, mappings);
        var mapped = mappings.Where(m => m.TargetFieldName is not null).ToList();

        var names = mapped.Select(m => m.TargetFieldName!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = entry.Fields.FirstOrDefault(f => f.IsRequired && f.DefaultValue is null && !names.Contains(f.FieldName));
        if (missing is not null) throw new BadRequestException(ImportMessages.MapRequired(missing.DisplayName));

        var keys = entry.Fields.Where(f => f.IsKey).ToList();
        if (keys.Count > 0 && !keys.Any(k => names.Contains(k.FieldName)) && !names.Contains(ImportLimits.ExternalIdField))
            throw new BadRequestException(ImportMessages.MapKey(string.Join(", ", keys.Select(k => k.DisplayName))));
    }

    private async Task ResetValidationAsync(ImportSession session, CancellationToken ct)
    {
        if (session.ValidationCompletedAt is null && session.ValidRows == 0 && session.InvalidRows == 0) return;
        var rows = await _db.Rows.IgnoreQueryFilters().Where(r => r.SessionId == session.Id && r.Status != ImportRowStatus.Pending).ToListAsync(ct);
        foreach (var r in rows)
        {
            r.Status = ImportRowStatus.Pending;
            r.ValuesJson = null;
            r.ErrorsJson = null;
            r.ExistingEntityUuid = null;
            r.ExternalId = null;
        }
        session.ValidRows = session.InvalidRows = 0;
        session.ValidationCompletedAt = null;
    }

    public async Task<IReadOnlyList<ImportPresetModel>> ListPresetsAsync(string? entityType, ImportCaller caller, CancellationToken ct = default)
    {
        var q = _db.Sessions.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == Org && s.SavedMappingName != null);
        if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(s => s.EntityType == entityType.Trim());
        var rows = await q.Select(s => new { s.SavedMappingName, s.SavedMappingAt, s.CreatedAt }).ToListAsync(ct);
        return rows.GroupBy(r => r.SavedMappingName!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ImportPresetModel(g.First().SavedMappingName!, g.Max(x => x.SavedMappingAt ?? x.CreatedAt)))
            .OrderByDescending(p => p.LastUsedAt).ToList();
    }

    public async Task<ImportSessionDetail> SavePresetAsync(Guid uuid, string? presetName, ImportCaller caller, CancellationToken ct = default)
    {
        var name = presetName?.Trim();
        if (string.IsNullOrEmpty(name)) throw new BadRequestException("Enter a preset name.");
        if (name.Length > 100) throw new BadRequestException("A preset name can be at most 100 characters.");
        var session = await LoadAsync(uuid, caller, true, ct);
        session.SavedMappingName = name;
        session.SavedMappingAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Detail(session, Entry(session));
    }

    // ── Validation ───────────────────────────────────────────────────────────

    public async Task<ImportValidationResult> ValidateAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default)
    {
        var session = await LoadAsync(uuid, caller, true, ct);
        // D-39 — an interrupted (FAILED) or cancelled import is re-validated too: only its rows not yet applied take part.
        if (session.Status is not (ImportSessionStatus.Uploaded or ImportSessionStatus.Mapping or ImportSessionStatus.Validated
                                   or ImportSessionStatus.Failed or ImportSessionStatus.Cancelled))
            throw new BadRequestException(ImportMessages.NotRunnable);
        var entry = _registry.Get(session.EntityType);
        await _opening.EnsureAllowedAsync(Org, entry.Definition, caller, ct);
        CheckMappingRules(entry, session.Mappings);

        var previous = session.Status;
        session.Status = ImportSessionStatus.Validating;
        await _db.SaveChangesAsync(ct);
        try
        {
            await RunValidationAsync(session, entry, ct);
        }
        catch
        {
            session.Status = previous == ImportSessionStatus.Validated ? ImportSessionStatus.Mapping : previous;
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        var rows = await _db.Rows.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.SessionId == session.Id && r.Status == ImportRowStatus.Invalid).OrderBy(r => r.RowNumber)
            .Select(r => new { r.RowNumber, r.ErrorsJson }).ToListAsync(ct);
        var all = rows.SelectMany(r => (r.ErrorsJson is null ? [] : JsonSerializer.Deserialize<List<ImportFieldError>>(r.ErrorsJson, Json) ?? [])
            .Select(e => new ImportValidationError(r.RowNumber, e.Field, e.Code, e.Message, e.Value))).ToList();
        return new ImportValidationResult
        {
            Session = Detail(session, entry),
            Errors = all.Take(ImportLimits.ValidationErrorsReturned).ToList(),
            ErrorCountByCode = all.GroupBy(e => e.ErrorCode).ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private async Task RunValidationAsync(ImportSession session, ImportTargetEntry entry, CancellationToken ct)
    {
        var def = entry.Definition;
        var mapped = session.Mappings.Where(m => m.TargetFieldName is not null).ToList();
        // D-39 — rows with an outcome (IMPORTED / UPDATED / SKIPPED / ERROR) keep it; only rows not yet applied are (re)checked.
        // A row applied by the target but not marked before an interruption is still VALID here: the target now finds its
        // record (ExistingEntityUuid), so it is skipped, updated or refused as DUPLICATE_IN_DB — never created twice.
        var rows = await _db.Rows.IgnoreQueryFilters()
            .Where(r => r.SessionId == session.Id &&
                        (r.Status == ImportRowStatus.Pending || r.Status == ImportRowStatus.Valid || r.Status == ImportRowStatus.Invalid))
            .OrderBy(r => r.RowNumber).ToListAsync(ct);

        // 1. Generic checks.
        var results = rows.ToDictionary(r => r.RowNumber, r => ImportGenericValidator.Validate(entry.Fields, MappedRaw(r, mapped)));

        // 2. In-file duplicates (match fields, external id).
        var duplicates = ImportGenericValidator.FindInFileDuplicates(
            rows.Select(r => (r.RowNumber, (IReadOnlyDictionary<string, string?>)results[r.RowNumber].Values)).ToList(), def.MatchFields, entry.Fields);
        foreach (var (rowNumber, errors) in duplicates) results[rowNumber].Errors.AddRange(errors);

        // 3. External id → the record imported earlier (resolved before the target's own match).
        var extIds = results.Values.Select(v => v.Values.GetValueOrDefault(ImportLimits.ExternalIdField)).Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var refs = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in extIds.Chunk(1000))
        {
            var found = await _db.ExternalReferences.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.OrganizationId == Org && x.EntityType == def.EntityType && chunk.Contains(x.ExternalId))
                .Select(x => new { x.ExternalId, x.EntityUuid }).ToListAsync(ct);
            foreach (var f in found) refs[f.ExternalId] = f.EntityUuid;
        }

        // 4. The target's checks for the rows that passed (file order, in chunks).
        var existing = new Dictionary<int, Guid?>();
        var inputs = new List<ImportRowInput>();
        foreach (var r in rows)
        {
            var res = results[r.RowNumber];
            var ext = res.Values.GetValueOrDefault(ImportLimits.ExternalIdField)?.Trim();
            Guid? known = ext is not null && refs.TryGetValue(ext, out var u) ? u : null;
            existing[r.RowNumber] = known;
            if (res.Errors.Count > 0) continue;
            var values = res.Values.Where(kv => !kv.Key.Equals(ImportLimits.ExternalIdField, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            inputs.Add(new ImportRowInput(r.RowNumber, values, known));
        }
        // One call with every generically valid row (files are capped at 50 000 rows), so in-file hierarchy — a parent
        // category anywhere earlier in the file — is visible to the target.
        if (inputs.Count > 0)
        {
            var checks = await entry.Target.ValidateAsync(Org, inputs, ct);
            foreach (var check in checks)
            {
                if (!results.TryGetValue(check.RowNumber, out var res)) continue;
                res.Errors.AddRange(check.Errors);
                // The external reference (resolved first) wins over the target's natural-key match.
                if (check.ExistingEntityUuid is { } match && existing.GetValueOrDefault(check.RowNumber) is null)
                    existing[check.RowNumber] = match;
            }
        }

        // 5. Duplicate in the database: refused when the strategy is ERROR or updates are not allowed.
        var strategy = session.DuplicateStrategy;
        var matchLabel = string.Join(", ", def.MatchFields.Select(m => entry.Field(m)?.DisplayName ?? m));
        int valid = 0, invalid = 0;
        foreach (var r in rows)
        {
            var res = results[r.RowNumber];
            var match = existing[r.RowNumber];
            if (match is not null && res.Errors.Count == 0 &&
                (strategy == ImportDuplicateStrategy.Error || (strategy == ImportDuplicateStrategy.Update && (!def.AllowUpdate || def.IsOpeningBalance))))
                res.Errors.Add(new ImportFieldError(def.MatchFields.FirstOrDefault() ?? ImportLimits.ExternalIdField, ImportErrorCodes.DuplicateInDb,
                    $"A record with the same {(matchLabel.Length > 0 ? matchLabel : "External ID")} already exists.", null));
            // CREATE_NEW cannot create a second record with an existing natural key (targets refuse duplicate codes/SKUs on
            // CREATE, which would roll back a whole batch) — refused per row here instead.
            else if (match is not null && res.Errors.Count == 0 && strategy == ImportDuplicateStrategy.CreateNew)
                res.Errors.Add(new ImportFieldError(def.MatchFields.FirstOrDefault() ?? ImportLimits.ExternalIdField, ImportErrorCodes.DuplicateInDb,
                    $"CREATE_NEW cannot create a duplicate of an existing {(matchLabel.Length > 0 ? matchLabel : "record")}", null));

            r.ValuesJson = JsonSerializer.Serialize(res.Values, Json);
            r.ErrorsJson = res.Errors.Count == 0 ? null : JsonSerializer.Serialize(res.Errors, Json);
            r.ExternalId = res.Values.GetValueOrDefault(ImportLimits.ExternalIdField)?.Trim();
            r.ExistingEntityUuid = match;
            r.EntityUuid = null;
            r.Status = res.Errors.Count == 0 ? ImportRowStatus.Valid : ImportRowStatus.Invalid;
            if (res.Errors.Count == 0) valid++; else invalid++;
        }

        session.ValidRows = valid;
        session.InvalidRows = invalid;
        session.ValidationCompletedAt = DateTime.UtcNow;
        session.Status = ImportSessionStatus.Validated;
        await _db.SaveChangesAsync(ct);
    }

    // ── Cancel, progress, errors ─────────────────────────────────────────────

    public async Task<ImportSessionDetail> CancelAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default)
    {
        var session = await LoadAsync(uuid, caller, true, ct);
        if (ImportSessionStatus.Finished.Contains(session.Status)) throw new BadRequestException(ImportMessages.Finished);
        // A running import stops before its next batch (C8 cancel); batches already applied stay.
        if (session.Status == ImportSessionStatus.Importing && caller.CanManageAll)
        {
            // D-38 — IMPORT_MANAGE forces an IMPORTING session (e.g. stuck after a crash) to the interrupted state.
            ImportRecoveryJob.MarkInterrupted(session, DateTime.UtcNow);
        }
        else
        {
            session.Status = ImportSessionStatus.Cancelled;
            if (session.ImportStartedAt is not null) session.ImportCompletedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        return Detail(session, Entry(session));
    }

    public async Task<ImportProgressModel> ProgressAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default)
    {
        var s = await LoadAsync(uuid, caller, false, ct);
        return new ImportProgressModel(s.Uuid, s.ProcessedRows, s.RowsToProcess > 0 ? s.RowsToProcess : s.ValidRows, s.Status);
    }

    public async Task<PaginatedResponse<ImportErrorRowModel>> ErrorsAsync(Guid uuid, int page, int pageSize, ImportCaller caller, CancellationToken ct = default)
    {
        var session = await LoadAsync(uuid, caller, false, ct);
        var p = Math.Max(1, page);
        var size = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 200);
        var q = _db.Rows.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.SessionId == session.Id && (r.Status == ImportRowStatus.Invalid || r.Status == ImportRowStatus.Error));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(r => r.RowNumber).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var mapped = session.Mappings.Where(m => m.TargetFieldName is not null).ToList();
        return new PaginatedResponse<ImportErrorRowModel>
        {
            Data = rows.Select(r => new ImportErrorRowModel
            {
                RowNumber = r.RowNumber, Status = r.Status, Values = MappedRaw(r, mapped), Errors = Errors(r)
            }).ToList(),
            TotalRecords = total, Page = p, PageSize = size, TotalPages = (int)Math.Ceiling(total / (double)size)
        };
    }
}

/// <summary>
/// A40 C9 / D-7 — opening-balance entity types need IMPORT_OPENING_BALANCE, FEATURE_OPENING_BALANCE_IMPORT on, and the
/// organization's opening balance date set (Finance's <see cref="IOpeningBalanceDateProvider"/>; absent = not set).
/// </summary>
internal sealed class ImportOpeningGate
{
    private readonly IModuleGate? _gate;
    private readonly IOpeningBalanceDateProvider? _date;

    public ImportOpeningGate(IModuleGate? gate = null, IOpeningBalanceDateProvider? date = null)
    {
        _gate = gate; _date = date;
    }

    public async Task EnsureAllowedAsync(Guid org, ImportProfileDefinition def, ImportCaller caller, CancellationToken ct)
    {
        if (!def.IsOpeningBalance) return;
        if (!caller.Has(PermissionCodes.IMPORT_OPENING_BALANCE)) throw new ForbiddenException(ImportMessages.OpeningPermission);
        if (!await _gate.IsOnAsync(org, ModuleCodes.OpeningBalanceImport, ct)) throw new ForbiddenException(ImportMessages.OpeningFeature);
        if (_date is null || await _date.GetAsync(org, ct) is null) throw new BadRequestException(ImportMessages.OpeningDateMissing);
    }
}
