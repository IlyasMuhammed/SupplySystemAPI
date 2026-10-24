using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;

namespace SMS.Modules.Accounting.Services;

// A42 Appendix B / D-20 — the 12 GL events, published AFTER the change is committed (never inside the transaction).
// Ids are the public uuids. Every event carries who did it (null for the system) and is written to GlSetupHistory by
// GlSetupHistoryWriter; other handlers can be added in DI (IAccountingEventHandler<TEvent>).

public interface IAccountingEvent
{
    Guid    OrganizationId { get; }
    int?    UserId         { get; }
    string? UserName       { get; }
}

public sealed record CoaTemplateAppliedEvent(Guid OrganizationId, Guid TemplateId, string TemplateCode, string TemplateName, int AccountCount,
    int? UserId = null, string? UserName = null) : IAccountingEvent;

public sealed record CoaUploadedEvent(Guid OrganizationId, string FileName, int AccountCount, string Format,
    int? UserId = null, string? UserName = null) : IAccountingEvent;

public sealed record ChartOfAccountCreatedEvent(Guid OrganizationId, Guid AccountId, string AccountCode, string AccountName, int Level, string RootType,
    int? UserId = null, string? UserName = null) : IAccountingEvent;

public sealed record ChartOfAccountUpdatedEvent(Guid OrganizationId, Guid AccountId, string AccountCode, IReadOnlyList<string> ChangedFields,
    int? UserId = null, string? UserName = null) : IAccountingEvent;

public sealed record ChartOfAccountDeletedEvent(Guid OrganizationId, Guid AccountId, string AccountCode, string AccountName,
    int? UserId = null, string? UserName = null) : IAccountingEvent;

public sealed record FiscalYearCreatedEvent(Guid OrganizationId, Guid FiscalYearId, string FiscalYearName, DateOnly StartDate, DateOnly EndDate,
    int? UserId = null, string? UserName = null) : IAccountingEvent;

public sealed record FiscalPeriodStatusChangedEvent(Guid OrganizationId, Guid PeriodId, string PeriodName, string OldStatus, string NewStatus,
    int? UserId = null, string? UserName = null) : IAccountingEvent;

/// <summary>AccountId null = the default was cleared.</summary>
public sealed record AccountingDefaultAssignedEvent(Guid OrganizationId, string DefaultKey, Guid? AccountId, string? AccountCode,
    int? UserId = null, string? UserName = null) : IAccountingEvent;

/// <summary>Skipped = posted with no entries (D-19).</summary>
public sealed record OpeningBalancePostedEvent(Guid OrganizationId, decimal TotalDebit, decimal TotalCredit, decimal Difference,
    DateOnly? ConversionDate, string? JournalEntryNumber, bool Skipped = false,
    int? UserId = null, string? UserName = null) : IAccountingEvent;

/// <summary>Note: e.g. "Setup reset" (D-17).</summary>
public sealed record CoaSetupStatusChangedEvent(Guid OrganizationId, string OldStatus, string NewStatus, string? Note = null,
    int? UserId = null, string? UserName = null) : IAccountingEvent;

public sealed record CoaSetupCompletedEvent(Guid OrganizationId, DateTime CompletedAt, int? CompletedByUserId, int AccountCount,
    int? UserId = null, string? UserName = null) : IAccountingEvent;

public sealed record ExternalMappingCreatedEvent(Guid OrganizationId, Guid MappingId, string SystemType, Guid AccountId, string AccountCode,
    string ExternalCode, int? UserId = null, string? UserName = null) : IAccountingEvent;

// A47 (A-19) — journal engine events, written to GlSetupHistory like the A42 ones. Published for manual / engine actions
// only (a gateway posting is logged in the posting log, not here).

/// <summary>§9.5 step 4 — a journal entry was posted (manual post, or created and posted).</summary>
public sealed record JournalEntryPostedEvent(Guid OrganizationId, Guid JournalEntryId, string EntryNumber, string JournalTypeCode,
    decimal TotalDebit, DateOnly EntryDate, int? UserId = null, string? UserName = null) : IAccountingEvent;

/// <summary>A47 C7 (RR publishes) — a posted entry was reversed by <paramref name="ReversalEntryNumber"/>.</summary>
public sealed record JournalEntryReversedEvent(Guid OrganizationId, Guid JournalEntryId, string EntryNumber, Guid ReversalEntryId,
    string ReversalEntryNumber, string? Reason, int? UserId = null, string? UserName = null) : IAccountingEvent;

/// <summary>A47 C1 — a journal type was created, changed, deactivated, activated or deleted (Action).</summary>
public sealed record JournalTypeChangedEvent(Guid OrganizationId, Guid JournalTypeId, string Code, string Action,
    int? UserId = null, string? UserName = null) : IAccountingEvent;

/// <summary>A47 C10 (RPT publishes) — the fiscal year was closed with its closing entry (null when nothing to close).</summary>
public sealed record FiscalYearClosedEvent(Guid OrganizationId, Guid FiscalYearId, string FiscalYearName, string? ClosingEntryNumber,
    decimal NetIncome, int? UserId = null, string? UserName = null) : IAccountingEvent;

/// <summary>Handles one kind of accounting event. Register as many as needed in DI.</summary>
public interface IAccountingEventHandler<in TEvent> where TEvent : IAccountingEvent
{
    Task HandleAsync(TEvent evt, CancellationToken ct = default);
}

public interface IAccountingEventPublisher
{
    /// <summary>Runs every handler of the event. A failing handler is logged and never fails the caller.</summary>
    Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : IAccountingEvent;
}

internal sealed class AccountingEventPublisher : IAccountingEventPublisher
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AccountingEventPublisher>? _log;

    public AccountingEventPublisher(IServiceProvider services, ILogger<AccountingEventPublisher>? log = null)
    {
        _services = services;
        _log = log;
    }

    public async Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : IAccountingEvent
    {
        foreach (var handler in _services.GetServices<IAccountingEventHandler<TEvent>>())
        {
            try
            {
                await handler.HandleAsync(evt, ct);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Accounting event handler {Handler} failed for {Event}.", handler.GetType().Name, typeof(TEvent).Name);
            }
        }
    }
}

/// <summary>D-20 — writes every accounting event to GlSetupHistory with a one-line summary.</summary>
internal sealed class GlSetupHistoryWriter :
    IAccountingEventHandler<CoaTemplateAppliedEvent>,
    IAccountingEventHandler<CoaUploadedEvent>,
    IAccountingEventHandler<ChartOfAccountCreatedEvent>,
    IAccountingEventHandler<ChartOfAccountUpdatedEvent>,
    IAccountingEventHandler<ChartOfAccountDeletedEvent>,
    IAccountingEventHandler<FiscalYearCreatedEvent>,
    IAccountingEventHandler<FiscalPeriodStatusChangedEvent>,
    IAccountingEventHandler<AccountingDefaultAssignedEvent>,
    IAccountingEventHandler<OpeningBalancePostedEvent>,
    IAccountingEventHandler<CoaSetupStatusChangedEvent>,
    IAccountingEventHandler<CoaSetupCompletedEvent>,
    IAccountingEventHandler<ExternalMappingCreatedEvent>,
    IAccountingEventHandler<JournalEntryPostedEvent>,
    IAccountingEventHandler<JournalEntryReversedEvent>,
    IAccountingEventHandler<JournalTypeChangedEvent>,
    IAccountingEventHandler<FiscalYearClosedEvent>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AccountingDbContext _db;

    public GlSetupHistoryWriter(AccountingDbContext db) => _db = db;

    private static string Money(decimal value) => value.ToString("#,##0.00", CultureInfo.InvariantCulture);
    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The summary shown on the Setup Status page.</summary>
    internal static string Summarize(IAccountingEvent evt) => evt switch
    {
        CoaTemplateAppliedEvent e        => $"Template {e.TemplateName} ({e.TemplateCode}) applied: {e.AccountCount} accounts created.",
        CoaUploadedEvent { Format: "BLANK" } e => $"Blank chart of accounts started: {e.AccountCount} system accounts created.",
        CoaUploadedEvent e               => $"Chart of accounts uploaded from {e.FileName} ({e.Format.ToUpperInvariant()}): {e.AccountCount} accounts created.",
        ChartOfAccountCreatedEvent e     => $"Account {e.AccountCode} {e.AccountName} added (level {e.Level}, {e.RootType}).",
        ChartOfAccountUpdatedEvent e     => $"Account {e.AccountCode} changed: {string.Join(", ", e.ChangedFields)}.",
        ChartOfAccountDeletedEvent e     => $"Account {e.AccountCode} {e.AccountName} deleted.",
        FiscalYearCreatedEvent e         => $"Fiscal year {e.FiscalYearName} created ({Date(e.StartDate)} to {Date(e.EndDate)}).",
        FiscalPeriodStatusChangedEvent e => $"Period {e.PeriodName}: {e.OldStatus} → {e.NewStatus}.",
        AccountingDefaultAssignedEvent e => e.AccountCode is null
            ? $"Default {e.DefaultKey} cleared."
            : $"Default {e.DefaultKey} set to account {e.AccountCode}.",
        OpeningBalancePostedEvent e      => e.Skipped
            ? "Opening balances skipped (no balances)."
            : $"Opening balances posted{(e.JournalEntryNumber is null ? "" : $" as {e.JournalEntryNumber}")}: debits {Money(e.TotalDebit)}, " +
              $"credits {Money(e.TotalCredit)}{(e.Difference == 0 ? "" : $", difference {Money(e.Difference)} to Opening Balance Equity (3900)")}" +
              $"{(e.ConversionDate is { } d ? $", as of {Date(d)}" : "")}.",
        CoaSetupStatusChangedEvent e     => e.Note is null
            ? $"Setup status {e.OldStatus} → {e.NewStatus}."
            : $"{e.Note}: status {e.OldStatus} → {e.NewStatus}.",
        CoaSetupCompletedEvent e         => $"General ledger setup completed with {e.AccountCount} accounts.",
        ExternalMappingCreatedEvent e    => $"Account {e.AccountCode} mapped to {e.ExternalCode} in {ExternalSystemTypes.DisplayNames.GetValueOrDefault(e.SystemType, e.SystemType)}.",
        JournalEntryPostedEvent e        => $"Journal entry {e.EntryNumber} ({e.JournalTypeCode}) posted: {Money(e.TotalDebit)}, dated {Date(e.EntryDate)}.",
        JournalEntryReversedEvent e      => $"Journal entry {e.EntryNumber} reversed by {e.ReversalEntryNumber}{(string.IsNullOrWhiteSpace(e.Reason) ? "" : $": {e.Reason}")}.",
        JournalTypeChangedEvent e        => $"Journal type {e.Code} {e.Action}.",
        FiscalYearClosedEvent e          => e.ClosingEntryNumber is null
            ? $"Fiscal year {e.FiscalYearName} closed (nothing to transfer)."
            : $"Fiscal year {e.FiscalYearName} closed by {e.ClosingEntryNumber}: net income {Money(e.NetIncome)} to retained earnings.",
        _                                => evt.GetType().Name
    };

    private async Task WriteAsync(IAccountingEvent evt, CancellationToken ct)
    {
        var summary = Summarize(evt);
        _db.GlSetupHistory.Add(new GlSetupHistory
        {
            OrganizationId = evt.OrganizationId,
            EventType      = evt.GetType().Name,
            Summary        = summary.Length > 500 ? summary[..497] + "..." : summary,
            PayloadJson    = JsonSerializer.Serialize(evt, evt.GetType(), Json),
            UserId         = evt.UserId,
            UserName       = evt.UserName,
            At             = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    public Task HandleAsync(CoaTemplateAppliedEvent evt, CancellationToken ct = default)        => WriteAsync(evt, ct);
    public Task HandleAsync(CoaUploadedEvent evt, CancellationToken ct = default)               => WriteAsync(evt, ct);
    public Task HandleAsync(ChartOfAccountCreatedEvent evt, CancellationToken ct = default)     => WriteAsync(evt, ct);
    public Task HandleAsync(ChartOfAccountUpdatedEvent evt, CancellationToken ct = default)     => WriteAsync(evt, ct);
    public Task HandleAsync(ChartOfAccountDeletedEvent evt, CancellationToken ct = default)     => WriteAsync(evt, ct);
    public Task HandleAsync(FiscalYearCreatedEvent evt, CancellationToken ct = default)         => WriteAsync(evt, ct);
    public Task HandleAsync(FiscalPeriodStatusChangedEvent evt, CancellationToken ct = default) => WriteAsync(evt, ct);
    public Task HandleAsync(AccountingDefaultAssignedEvent evt, CancellationToken ct = default) => WriteAsync(evt, ct);
    public Task HandleAsync(OpeningBalancePostedEvent evt, CancellationToken ct = default)      => WriteAsync(evt, ct);
    public Task HandleAsync(CoaSetupStatusChangedEvent evt, CancellationToken ct = default)     => WriteAsync(evt, ct);
    public Task HandleAsync(CoaSetupCompletedEvent evt, CancellationToken ct = default)         => WriteAsync(evt, ct);
    public Task HandleAsync(ExternalMappingCreatedEvent evt, CancellationToken ct = default)    => WriteAsync(evt, ct);
    public Task HandleAsync(JournalEntryPostedEvent evt, CancellationToken ct = default)        => WriteAsync(evt, ct);
    public Task HandleAsync(JournalEntryReversedEvent evt, CancellationToken ct = default)      => WriteAsync(evt, ct);
    public Task HandleAsync(JournalTypeChangedEvent evt, CancellationToken ct = default)        => WriteAsync(evt, ct);
    public Task HandleAsync(FiscalYearClosedEvent evt, CancellationToken ct = default)          => WriteAsync(evt, ct);

    /// <summary>Registers the writer for all 12 A42 + 4 A47 events (one scoped instance).</summary>
    internal static void Register(IServiceCollection services)
    {
        services.AddScoped<GlSetupHistoryWriter>();
        services.AddScoped<IAccountingEventHandler<CoaTemplateAppliedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<CoaUploadedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<ChartOfAccountCreatedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<ChartOfAccountUpdatedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<ChartOfAccountDeletedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<FiscalYearCreatedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<FiscalPeriodStatusChangedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<AccountingDefaultAssignedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<OpeningBalancePostedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<CoaSetupStatusChangedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<CoaSetupCompletedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<ExternalMappingCreatedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<JournalEntryPostedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<JournalEntryReversedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<JournalTypeChangedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
        services.AddScoped<IAccountingEventHandler<FiscalYearClosedEvent>>(sp => sp.GetRequiredService<GlSetupHistoryWriter>());
    }
}
