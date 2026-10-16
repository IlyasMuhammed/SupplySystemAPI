using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway.Validation;
using SMS.Modules.Integration.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Reference;

/// <summary>The Coverage tab and the dry validation of one record (A44 D-27). Nothing here calls QuickBooks.</summary>
public interface IQuickBooksCoverageService
{
    Task<CoverageModel> GetAsync(CancellationToken ct = default);
    Task<ValidationCheckResult> CheckAsync(ValidationCheckRequest request, CancellationToken ct = default);
}

/// <summary>
/// Readiness from what is stored: the connection, preflight, the sync-control state, how many of the customers / vendors /
/// items SCM has sent are linked to QuickBooks, tax-code mappings, the general ledger's account mappings (information
/// only — QuickBooks documents do not need them, D-16) and the provider defaults the enabled document types need.
/// <see cref="CoverageModel.Ready"/> = no blocking issue. Gateway-side services (validator, dependencies) are resolved
/// when used.
/// </summary>
internal sealed class CoverageService : IQuickBooksCoverageService
{
    private readonly IntegrationDbContext _db;
    private readonly IConnectionAccessor  _accessor;
    private readonly ReferenceDataService _reference;
    private readonly IPreflightService    _preflight;
    private readonly IServiceProvider     _services;

    public CoverageService(
        IntegrationDbContext db, IConnectionAccessor accessor, ReferenceDataService reference, IPreflightService preflight,
        IServiceProvider services)
    {
        _db        = db;
        _accessor  = accessor;
        _reference = reference;
        _preflight = preflight;
        _services  = services;
    }

    // ── Coverage ────────────────────────────────────────────────────────────────────────────

    public async Task<CoverageModel> GetAsync(CancellationToken ct = default)
    {
        var model      = new CoverageModel();
        var connection = await _accessor.GetCurrentAsync(ct);

        if (connection is null)
        {
            model.BlockingIssues.Add(Issue("NOT_CONNECTED", "Connection", "QuickBooks is not connected.", tab: "connection"));
            return model;
        }

        model.ConnectionStatus = connection.Status.ToString();
        if (!connection.IsUsable())
            model.BlockingIssues.Add(Issue("NOT_CONNECTED", "Connection",
                ConnectionPersistence.DescribeUnusable(connection.Status), tab: "connection"));

        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.ConnectionId == connection.Id, ct)
                    ?? new IntegrationSettings { ConnectionId = connection.Id };
        model.Mode = settings.Mode.ToString();

        var (data, fetchedAt) = await _reference.LoadAsync(connection.Id, ct);
        model.ReferenceFetchedAt = fetchedAt;

        // Preflight: its failures block (they block Live), its warnings are warnings. Its connection row is ours already.
        // The posting-defaults checks are reported once, by the defaults section below (with their document type and tab).
        string[] reportedBelow = [PreflightCodes.Connection, PreflightCodes.DepositAccounts, PreflightCodes.BankAccounts,
                                  PreflightCodes.ClassDefault, PreflightCodes.DepartmentDefault];
        var preflight = await _preflight.RunAsync(ct);
        foreach (var check in preflight.Checks.Where(c => !reportedBelow.Contains(c.Code) && c.Status != "Pass"))
        {
            var issue = Issue(check.Code, "Preflight", $"{check.Title}: {check.Message}", tab: "setup");
            (check.Status == "Fail" ? model.BlockingIssues : model.Warnings).Add(issue);
        }

        var control = await PostingReadiness.DecisionsAsync(_services, _db, connection.Id, settings, ct);
        await EntitiesAsync(model, connection.Id, control, ct);
        await TaxAsync(model, connection.Id, ct);
        await AccountsAsync(model, ct);
        await DefaultsAsync(model, connection.Id, settings, data, control, ct);

        // QA-3: one row per issue — and a blocking issue is not repeated as a warning.
        static List<CoverageIssueModel> Distinct(IEnumerable<CoverageIssueModel> issues) =>
            issues.GroupBy(i => (i.Code, i.DocumentType)).Select(g => g.First()).ToList();
        model.BlockingIssues = Distinct(model.BlockingIssues);
        model.Warnings       = Distinct(model.Warnings.Where(w => !model.BlockingIssues.Any(b => b.Code == w.Code && b.DocumentType == w.DocumentType)));

        model.Ready = model.BlockingIssues.Count == 0;
        return model;
    }

    private async Task EntitiesAsync(
        CoverageModel model, int connectionId, IReadOnlyDictionary<string, SyncControlDecision> control, CancellationToken ct)
    {
        var maps = await _db.EntityMaps.AsNoTracking()
            .Where(m => m.ConnectionId == connectionId && m.SourceSystem == QuickBooksSourceSystems.Scm
                     && (m.Kind == SyncKind.Customer || m.Kind == SyncKind.Vendor || m.Kind == SyncKind.Item) && m.PayloadJson != null)
            .Select(m => new { m.Id, m.Kind, m.RemoteId, m.State })
            .ToListAsync(ct);
        var skipped = (await _db.MatchCandidates.AsNoTracking()
                .Where(c => c.ConnectionId == connectionId && c.Decision == MatchDecision.Skip)
                .Select(c => c.EntityMapId).ToListAsync(ct))
            .ToHashSet();
        var remote = await _db.RemoteEntities.AsNoTracking()
            .Where(r => r.ConnectionId == connectionId)
            .GroupBy(r => r.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var remoteIds = await _db.RemoteEntities.AsNoTracking()
            .Where(r => r.ConnectionId == connectionId)
            .Select(r => new { r.Kind, r.RemoteId })
            .ToListAsync(ct);

        foreach (var kind in new[] { SyncKind.Customer, SyncKind.Vendor, SyncKind.Item })
        {
            var ofKind = maps.Where(m => m.Kind == kind).ToList();
            var linkedRemote = ofKind.Where(m => m.RemoteId != null).Select(m => m.RemoteId!).ToHashSet(StringComparer.Ordinal);
            var row = new CoverageKindModel
            {
                Kind           = kind.ToString(),
                Total          = ofKind.Count,
                Mapped         = ofKind.Count(m => m.RemoteId != null),
                Skipped        = ofKind.Count(m => m.RemoteId == null && skipped.Contains(m.Id)),
                Failed         = ofKind.Count(m => m.RemoteId == null && !skipped.Contains(m.Id)
                                                && m.State is SyncState.Failed or SyncState.Blocked or SyncState.NeedsResolution),
                RemoteImported = remote.Where(r => r.Kind == kind).Sum(r => r.Count),
                RemoteUnlinked = remoteIds.Count(r => r.Kind == kind && !linkedRemote.Contains(r.RemoteId))
            };
            row.Pending       = row.Total - row.Mapped - row.Skipped;
            row.PercentMapped = row.Total == 0 ? 100m : Math.Round(100m * row.Mapped / row.Total, 1);
            model.Entities.Add(row);

            var type = SyncControlDocumentTypes.For(kind, null);
            if (row.Failed > 0 && control[type].IsEnabled)
                model.Warnings.Add(Issue("ENTITIES_FAILED", kind.ToString(),
                    $"{row.Failed} {SyncControlDefaults.Labels[type].ToLowerInvariant()} could not be sent — the documents that use them wait.",
                    type, "sync"));
        }
    }

    private async Task TaxAsync(CoverageModel model, int connectionId, CancellationToken ct)
    {
        if (_services.GetService<IIntegrationSettingsService>() is not { } settings) return;

        var rows = await settings.GetTaxMappingsAsync(ct);
        model.TaxCodes.Total    = rows.Count;
        model.TaxCodes.Mapped   = rows.Count(r => r.QboTaxCodeId is not null);
        model.TaxCodes.Unmapped = rows.Where(r => r.QboTaxCodeId is null)
            .Select(r => r.SourceTaxCode ?? PreflightService.FormatRate(r.TaxPercent)).ToList();

        // Only codes / rates documents actually carry block anything (the validator refuses those documents).
        var used = rows.Where(r => r.QboTaxCodeId is null && r.TimesSeen > 0)
            .Select(r => r.SourceTaxCode ?? PreflightService.FormatRate(r.TaxPercent)).ToList();
        if (used.Count > 0)
            model.BlockingIssues.Add(Issue("TAX_UNMAPPED", "Tax",
                $"Tax codes used by stored documents have no QuickBooks mapping: {string.Join(", ", used)}. Those documents are refused.",
                tab: "tax"));
        else if (model.TaxCodes.Unmapped.Count > 0)
            model.Warnings.Add(Issue("TAX_UNMAPPED", "Tax",
                $"{model.TaxCodes.Unmapped.Count} SMS tax code(s) are not mapped yet (no stored document uses them).", tab: "tax"));
    }

    private async Task AccountsAsync(CoverageModel model, CancellationToken ct)
    {
        var ledger = _services.GetService<IExternalAccountMapReader>();
        if (ledger is null) return;
        try
        {
            var leaves = await ledger.ListLeafAccountsAsync(ct);
            var map    = await ledger.GetAsync(ExternalSystemTypes.QuickBooksOnline, ct);
            model.Accounts.GlAvailable = leaves.Count > 0;
            model.Accounts.Total       = leaves.Count(l => l.IsActive);
            model.Accounts.Mapped      = leaves.Count(l => l.IsActive && map.ByAccountCode.ContainsKey(l.Code));
            model.Accounts.Unmapped    = model.Accounts.Total - model.Accounts.Mapped;
            if (model.Accounts.GlAvailable && model.Accounts.Unmapped > 0)
                model.Warnings.Add(Issue("ACCOUNTS_UNMAPPED", "Accounts",
                    $"{model.Accounts.Unmapped} active general-ledger account(s) are not mapped to QuickBooks. QuickBooks documents do not "
                  + "need them; mapping keeps the two charts comparable and fills empty settings accounts.", tab: "accounts"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The general ledger is optional.
        }
    }

    private async Task DefaultsAsync(
        CoverageModel model, int connectionId, IntegrationSettings settings, Providers.RemoteReferenceData? data,
        IReadOnlyDictionary<string, SyncControlDecision> control, CancellationToken ct)
    {
        var issues = await PostingReadiness.CheckAsync(_services, _db, connectionId, settings, data, ct);
        var stored = await _db.ProviderDefaults.AsNoTracking().CountAsync(d => d.ConnectionId == connectionId, ct);

        model.ProviderDefaults.Set      = stored;
        model.ProviderDefaults.Required = issues.Count + stored;

        foreach (var issue in issues)
        {
            var row = Issue(issue.Code, "Defaults", issue.Message, issue.DocumentType, "defaults");
            model.ProviderDefaults.Missing.Add(row);
            // Bill payments are refused without an account; the others only fall back (Undeposited Funds, no class).
            (issue.Code == PostingReadiness.BankAccounts ? model.BlockingIssues : model.Warnings).Add(row);
        }

        var held = control.Where(c => c.Value.Action == SyncControlAction.Hold).Select(c => c.Key).ToList();
        foreach (var type in held)
        {
            var count = await HeldRecords.CountAsync(_services, _db, connectionId, type, ct);
            if (count > 0)
                model.Warnings.Add(Issue("HELD", "SyncControl",
                    $"{count} {SyncControlDefaults.Labels[type].ToLowerInvariant()} are held while the type is off.", type, "sync-control"));
        }
    }

    // ── Validation check ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the gateway's validator over the payload the gateway holds for one record and lists the dependencies that are
    /// not in QuickBooks yet — what would stop it going now. Read-only: nothing is queued, saved or sent.
    /// </summary>
    public async Task<ValidationCheckResult> CheckAsync(ValidationCheckRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind       = SyncKindNames.Parse(request.Kind);
        var externalId = request.ExternalId?.Trim();
        if (string.IsNullOrEmpty(externalId)) throw new BadRequestException("Give the record's externalId.");
        var source     = string.IsNullOrWhiteSpace(request.SourceSystem) ? QuickBooksSourceSystems.Scm : request.SourceSystem.Trim();

        var connection = await _accessor.GetCurrentAsync(ct) ?? throw new ConflictException("QuickBooks is not connected.");
        var result     = new ValidationCheckResult { Kind = kind.ToString(), ExternalId = externalId };

        var map = await _db.EntityMaps.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ConnectionId == connection.Id && m.SourceSystem == source && m.Kind == kind && m.ExternalId == externalId, ct);
        if (map?.PayloadJson is null)
        {
            result.Errors.Add(new ValidationErrorModel
            {
                Field = "externalId", Code = "NOT_RECEIVED",
                Message = "The gateway holds no data for this record yet. Use Push now on the Sync tab to have SCM send it."
            });
            result.State = map?.State.ToString();
            return result;
        }

        result.Found        = true;
        result.State        = map.State.ToString();
        result.DisplayLabel = map.DisplayLabel;

        object payload;
        try
        {
            payload = SyncPayloads.Deserialize(kind, map.PayloadJson);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentOutOfRangeException or NotSupportedException)
        {
            result.Errors.Add(new ValidationErrorModel { Field = "payload", Code = "UNREADABLE", Message = "The stored data cannot be read: " + ex.Message });
            return result;
        }

        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.ConnectionId == connection.Id, ct)
                    ?? new IntegrationSettings { ConnectionId = connection.Id };

        result.DocumentType = SyncControlDocumentTypes.For(kind, (payload as SalesInvoicePayload)?.SourceType ?? SourceTypeOf(map.PayloadJson));
        var decisions = await PostingReadiness.DecisionsAsync(_services, _db, connection.Id, settings, ct);
        result.SyncControl = decisions.TryGetValue(result.DocumentType, out var decision) ? decision.Action.ToString() : SyncControlAction.Process.ToString();

        if (_services.GetService<IPayloadValidator>() is { } validator)
        {
            var validation = await validator.ValidateAsync(kind, payload, map, connection, settings, ct);
            result.Errors.AddRange(validation.Errors.Select(e => new ValidationErrorModel { Field = e.Field, Code = e.Code, Message = e.Message }));
            result.Warnings.AddRange(validation.Warnings);
        }
        else
        {
            result.Warnings.Add("The gateway's validator is not available in this host; only dependencies were checked.");
        }

        var dependencies = SyncPayloads.DependenciesOf(kind, payload);
        if (dependencies.Count > 0 && _services.GetService<ISyncDependencies>() is { } deps)
        {
            var missing = await deps.FindMissingAsync(map, dependencies, settings.Mode == SyncMode.DryRun, ct);
            foreach (var d in missing)
            {
                var dep = await _db.EntityMaps.AsNoTracking()
                    .Where(m => m.ConnectionId == connection.Id && m.SourceSystem == source && m.Kind == d.Kind && m.ExternalId == d.ExternalId)
                    .Select(m => new { m.DisplayLabel, m.State })
                    .FirstOrDefaultAsync(ct);
                result.MissingDependencies.Add(new ValidationDependencyModel
                {
                    Kind = d.Kind.ToString(), ExternalId = d.ExternalId, Label = dep?.DisplayLabel, State = dep?.State.ToString()
                });
            }
        }

        result.Valid = result.Errors.Count == 0 && result.MissingDependencies.Count == 0;
        return result;
    }

    /// <summary>The payload's sourceType, read without knowing its type (POS documents follow POSSale / POSReturn).</summary>
    private static string? SourceTypeOf(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, "sourceType", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return p.Value.GetString();
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return null;
    }

    private static CoverageIssueModel Issue(string code, string area, string message, string? documentType = null, string? tab = null) =>
        new() { Code = code, Area = area, Message = message, DocumentType = documentType, Tab = tab };
}
