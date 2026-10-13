using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A42 §12 / D-21 — accounts mapped to their counterparts in external financial systems (reference data, no connectors).
/// One mapping per (organization, system type, account) — BR-INT-001; leaf + active accounts only — BR-INT-002/003;
/// connection settings never carry credentials — BR-INT-005. CSV import creates or updates per account + system and reports
/// line errors (valid lines are applied); export writes the same columns.
/// </summary>
public interface IExternalMappingService
{
    Task<IReadOnlyList<ExternalMappingModel>> ListAsync(AccountingCaller caller, string? systemType, CancellationToken ct = default);
    Task<ExternalMappingModel> CreateAsync(AccountingCaller caller, CreateExternalMappingRequest request, CancellationToken ct = default);
    Task<ExternalMappingModel> UpdateAsync(AccountingCaller caller, Guid uuid, UpdateExternalMappingRequest request, CancellationToken ct = default);
    Task DeleteAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default);
    Task<MappingImportResult> ImportAsync(AccountingCaller caller, Stream csv, string? systemType, string? systemName, CancellationToken ct = default);
    Task<byte[]> ExportAsync(AccountingCaller caller, string? systemType, CancellationToken ct = default);
}

internal sealed class ExternalMappingService : IExternalMappingService
{
    internal const string LeafMessage = "Only Level 4 accounts can be mapped.";
    internal const string InactiveMessage = "Mapped account must be active.";
    internal const string CredentialsMessage = "Connection settings must not contain credentials.";
    internal static readonly string[] CsvHeader = ["sms_account_code", "external_code", "external_name", "direction"];
    internal const int MaxImportBytes = 5 * 1024 * 1024;

    /// <summary>BR-INT-005 — a key containing any of these (case-insensitive) is a credential.</summary>
    private static readonly string[] SecretKeyParts =
        ["password", "secret", "token", "apikey", "api_key", "clientsecret", "client_secret", "privatekey", "private_key"];

    internal static string DuplicateMessage(string systemType) =>
        $"This account is already mapped for {ExternalSystemTypes.DisplayNames.GetValueOrDefault(systemType, systemType)}.";

    private readonly AccountingDbContext _db;
    private readonly IAccountingInitializer _init;
    private readonly ISetupStatusService _setup;
    private readonly IAccountingEventPublisher _events;

    public ExternalMappingService(AccountingDbContext db, IAccountingInitializer init, ISetupStatusService setup, IAccountingEventPublisher events)
    {
        _db = db;
        _init = init;
        _setup = setup;
        _events = events;
    }

    public async Task<IReadOnlyList<ExternalMappingModel>> ListAsync(AccountingCaller caller, string? systemType, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var type = string.IsNullOrWhiteSpace(systemType) ? null : NormalizeSystemType(systemType);
        return await QueryAsync(caller.OrganizationId, type, ct);
    }

    public async Task<ExternalMappingModel> CreateAsync(AccountingCaller caller, CreateExternalMappingRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);

        var type = NormalizeSystemType(request.SystemType);
        var account = await _db.ChartOfAccounts.IgnoreQueryFilters()
            .SingleOrDefaultAsync(a => a.OrganizationId == org && a.Uuid == request.AccountUuid, ct)
            ?? throw new NotFoundException("Account not found.");
        CheckAccount(account);
        var mapping = new ExternalSystemMapping
        {
            OrganizationId       = org,
            SystemType           = type,
            SystemName           = SystemName(request.SystemName, type),
            AccountId            = account.Id,
            ExternalAccountCode  = ExternalCode(request.ExternalAccountCode),
            ExternalAccountName  = ExternalName(request.ExternalAccountName),
            SyncDirection        = NormalizeDirection(request.SyncDirection),
            ConnectionConfigJson = CheckConnectionConfig(request.ConnectionConfigJson),
            CreatedBy            = caller.UserIdOrNull,
            ModifiedBy           = caller.UserIdOrNull,
        };

        if (await _db.ExternalSystemMappings.IgnoreQueryFilters()
                .AnyAsync(m => m.OrganizationId == org && m.SystemType == type && m.AccountId == account.Id, ct))
            throw new ConflictException(DuplicateMessage(type));

        _db.ExternalSystemMappings.Add(mapping);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race on UX_ExtSysMappings_Sys_Account_Org.
            _db.Entry(mapping).State = EntityState.Detached;
            throw new ConflictException(DuplicateMessage(type));
        }

        await _events.PublishAsync(new ExternalMappingCreatedEvent(org, mapping.Uuid, type, account.Uuid, account.AccountCode,
            mapping.ExternalAccountCode, caller.UserIdOrNull, caller.UserName), ct);
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        return (await QueryAsync(org, null, ct, mapping.Id)).Single();
    }

    public async Task<ExternalMappingModel> UpdateAsync(AccountingCaller caller, Guid uuid, UpdateExternalMappingRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        var mapping = await _db.ExternalSystemMappings.IgnoreQueryFilters().Include(m => m.Account)
            .SingleOrDefaultAsync(m => m.OrganizationId == org && m.Uuid == uuid, ct)
            ?? throw new NotFoundException("Mapping not found.");

        if (request.IsActive && !mapping.IsActive) CheckAccount(mapping.Account); // re-activating needs a valid account
        if (!string.IsNullOrWhiteSpace(request.SystemName)) mapping.SystemName = SystemName(request.SystemName, mapping.SystemType);
        mapping.ExternalAccountCode = ExternalCode(request.ExternalAccountCode);
        mapping.ExternalAccountName = ExternalName(request.ExternalAccountName);
        mapping.SyncDirection = NormalizeDirection(request.SyncDirection);
        mapping.IsActive = request.IsActive;
        if (request.ConnectionConfigJson is not null) mapping.ConnectionConfigJson = CheckConnectionConfig(request.ConnectionConfigJson);
        mapping.ModifiedBy = caller.UserIdOrNull;
        await _db.SaveChangesAsync(ct);

        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        return (await QueryAsync(org, null, ct, mapping.Id)).Single();
    }

    public async Task DeleteAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        var org = caller.OrganizationId;
        var mapping = await _db.ExternalSystemMappings.IgnoreQueryFilters()
            .SingleOrDefaultAsync(m => m.OrganizationId == org && m.Uuid == uuid, ct)
            ?? throw new NotFoundException("Mapping not found.");
        _db.ExternalSystemMappings.Remove(mapping);
        await _db.SaveChangesAsync(ct);
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
    }

    public async Task<MappingImportResult> ImportAsync(AccountingCaller caller, Stream csv, string? systemType, string? systemName, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        ArgumentNullException.ThrowIfNull(csv);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        var type = NormalizeSystemType(systemType);
        var name = SystemName(systemName, type);

        string text;
        using (var reader = new StreamReader(csv, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            var buffer = new char[MaxImportBytes + 1];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
            if (read > MaxImportBytes) throw new BadRequestException("The file is larger than 5 MB.");
            text = new string(buffer, 0, read);
        }

        var rows = MappingCsv.Parse(text);
        if (rows.Count == 0) throw new BadRequestException("The file is empty.");
        var header = rows[0].Fields.Select(h => h.Trim().ToLowerInvariant()).ToList();
        var col = CsvHeader.ToDictionary(h => h, h => header.IndexOf(h));
        if (col["sms_account_code"] < 0 || col["external_code"] < 0)
            throw new BadRequestException($"The file must have the columns {string.Join(",", CsvHeader)}.");

        var accounts = await _db.ChartOfAccounts.IgnoreQueryFilters()
            .Where(a => a.OrganizationId == org && a.IsActive).ToListAsync(ct);
        var byCode = accounts.GroupBy(a => a.AccountCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var existing = await _db.ExternalSystemMappings.IgnoreQueryFilters()
            .Where(m => m.OrganizationId == org && m.SystemType == type).ToDictionaryAsync(m => m.AccountId, ct);

        var result = new MappingImportResult();
        var created = new List<(ExternalSystemMapping Mapping, ChartOfAccount Account)>();
        var seen = new HashSet<int>();
        string Field(MappingCsv.Row row, string column) =>
            col[column] >= 0 && col[column] < row.Fields.Count ? MappingCsv.Unescape(row.Fields[col[column]].Trim()) : string.Empty;

        foreach (var row in rows.Skip(1))
        {
            if (row.Fields.All(string.IsNullOrWhiteSpace)) continue;
            try
            {
                var code = Field(row, "sms_account_code");
                if (code.Length == 0) throw new BadRequestException("The SMS account code is required.");
                if (!byCode.TryGetValue(code, out var account))
                {
                    var inactive = await _db.ChartOfAccounts.IgnoreQueryFilters()
                        .AnyAsync(a => a.OrganizationId == org && a.AccountCode == code && !a.IsActive, ct);
                    throw new BadRequestException(inactive ? InactiveMessage : $"Account {code} does not exist.");
                }
                CheckAccount(account);
                if (!seen.Add(account.Id)) throw new BadRequestException($"Account {account.AccountCode} appears more than once in the file.");
                var externalCode = ExternalCode(Field(row, "external_code"));
                var externalName = ExternalName(Field(row, "external_name"));
                var direction = NormalizeDirection(Field(row, "direction"));

                if (existing.TryGetValue(account.Id, out var mapping))
                {
                    mapping.ExternalAccountCode = externalCode;
                    mapping.ExternalAccountName = externalName;
                    mapping.SyncDirection = direction;
                    mapping.SystemName = name;
                    mapping.ModifiedBy = caller.UserIdOrNull;
                    result.Updated++;
                }
                else
                {
                    mapping = new ExternalSystemMapping
                    {
                        OrganizationId = org, SystemType = type, SystemName = name, AccountId = account.Id,
                        ExternalAccountCode = externalCode, ExternalAccountName = externalName, SyncDirection = direction,
                        CreatedBy = caller.UserIdOrNull, ModifiedBy = caller.UserIdOrNull,
                    };
                    _db.ExternalSystemMappings.Add(mapping);
                    existing[account.Id] = mapping;
                    created.Add((mapping, account));
                    result.Created++;
                }
            }
            catch (BadRequestException ex)
            {
                result.Errors.Add(new MappingImportError { Line = row.Line, Message = ex.Message });
            }
        }

        if (result.Created + result.Updated > 0)
        {
            await _db.InTransactionAsync(() => _db.SaveChangesAsync(ct), ct);
            foreach (var (mapping, account) in created)
                await _events.PublishAsync(new ExternalMappingCreatedEvent(org, mapping.Uuid, type, account.Uuid, account.AccountCode,
                    mapping.ExternalAccountCode, caller.UserIdOrNull, caller.UserName), ct);
            await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        }
        return result;
    }

    public async Task<byte[]> ExportAsync(AccountingCaller caller, string? systemType, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var type = string.IsNullOrWhiteSpace(systemType) ? null : NormalizeSystemType(systemType);
        var mappings = await QueryAsync(caller.OrganizationId, type, ct);
        var sb = new StringBuilder();
        // CRLF on every host (D-58) — AppendLine would write LF on Linux.
        sb.Append(string.Join(",", CsvHeader.Concat(["system_type", "system_name", "is_active"]))).Append("\r\n");
        foreach (var m in mappings)
            sb.Append(string.Join(",", new[]
            {
                m.Account.AccountCode, m.ExternalAccountCode, m.ExternalAccountName ?? "", m.SyncDirection, m.SystemType, m.SystemName,
                m.IsActive ? "true" : "false",
            }.Select(MappingCsv.Escape))).Append("\r\n");
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    // ── rules ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void CheckAccount(ChartOfAccount account)
    {
        if (account.IsGroup || account.Level != 4) throw new BadRequestException(LeafMessage); // BR-INT-002
        if (!account.IsActive) throw new BadRequestException(InactiveMessage);                 // BR-INT-003
    }

    internal static string NormalizeSystemType(string? value)
    {
        var v = value?.Trim().ToUpperInvariant() ?? "";
        return ExternalSystemTypes.All.Contains(v) ? v
            : throw new BadRequestException($"Unknown external system type {value}. Use one of: {string.Join(", ", ExternalSystemTypes.All)}.");
    }

    internal static string NormalizeDirection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return SyncDirections.None;
        return SyncDirections.All.FirstOrDefault(d => string.Equals(d, value.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new BadRequestException($"Unknown sync direction {value}. Use None, Inbound, Outbound or Bidirectional.");
    }

    private static string SystemName(string? value, string type)
    {
        var name = string.IsNullOrWhiteSpace(value) ? ExternalSystemTypes.DisplayNames[type] : value.Trim();
        return name.Length > 200 ? throw new BadRequestException("The system name can be at most 200 characters.") : name;
    }

    private static string ExternalCode(string? value)
    {
        var code = value?.Trim() ?? "";
        if (code.Length == 0) throw new BadRequestException("The external account code is required.");
        return code.Length > 100 ? throw new BadRequestException("The external account code can be at most 100 characters.") : code;
    }

    private static string? ExternalName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return name?.Length > 200 ? throw new BadRequestException("The external account name can be at most 200 characters.") : name;
    }

    /// <summary>BR-INT-005 — valid JSON whose keys (at any depth) never name a credential. Empty → null.</summary>
    internal static string? CheckConnectionConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new BadRequestException("Connection settings must be valid JSON.");
        }
        using (doc)
        {
            if (HasSecretKey(doc.RootElement)) throw new BadRequestException(CredentialsMessage);
        }
        return json.Trim();
    }

    private static bool HasSecretKey(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(p =>
            SecretKeyParts.Any(s => p.Name.Replace("-", "_").Contains(s, StringComparison.OrdinalIgnoreCase)) || HasSecretKey(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasSecretKey),
        _ => false,
    };

    private async Task<List<ExternalMappingModel>> QueryAsync(Guid org, string? type, CancellationToken ct, int? id = null) =>
        await _db.ExternalSystemMappings.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.OrganizationId == org && (type == null || m.SystemType == type) && (id == null || m.Id == id))
            .OrderBy(m => m.SystemType).ThenBy(m => m.Account.AccountCode)
            .Select(m => new ExternalMappingModel
            {
                Uuid                 = m.Uuid,
                SystemType           = m.SystemType,
                SystemName           = m.SystemName,
                Account              = new MappingAccountModel { Uuid = m.Account.Uuid, AccountCode = m.Account.AccountCode, AccountName = m.Account.AccountName },
                ExternalAccountCode  = m.ExternalAccountCode,
                ExternalAccountName  = m.ExternalAccountName,
                SyncDirection        = m.SyncDirection,
                IsActive             = m.IsActive,
                LastSyncedAt         = m.LastSyncedAt,
                SyncStatus           = m.SyncStatus,
                SyncErrorMessage     = m.SyncErrorMessage,
                ConnectionConfigJson = m.ConnectionConfigJson,
            })
            .ToListAsync(ct);
}

/// <summary>Minimal RFC 4180 CSV reader/writer for the mapping import/export (quoted fields, doubled quotes, CRLF/LF).</summary>
internal static class MappingCsv
{
    internal sealed record Row(int Line, List<string> Fields);

    public static List<Row> Parse(string text)
    {
        var rows = new List<Row>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var rowStart = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    rows.Add(new Row(rowStart, fields));
                    fields = [];
                    line++;
                    rowStart = line;
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            rows.Add(new Row(rowStart, fields));
        }
        if (rows.Count > 0 && rows[0].Fields.Count > 0 && rows[0].Fields[0].StartsWith('﻿'))
            rows[0].Fields[0] = rows[0].Fields[0][1..];
        return rows;
    }

    public static string Escape(string value)
    {
        // Formula-injection guard for spreadsheets opening the export.
        if (value.Length > 0 && "=+-@".Contains(value[0]) && !decimal.TryParse(value, out _)) value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>The reverse of the formula guard in <see cref="Escape"/>, so an export imports back unchanged (D-58).</summary>
    public static string Unescape(string value) =>
        value.Length > 1 && value[0] == '\'' && "=+-@".Contains(value[1]) ? value[1..] : value;
}
