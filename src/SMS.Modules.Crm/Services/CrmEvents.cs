using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Shared.Common;

namespace SMS.Modules.Crm.Services;

// A41 Appendix B / D-17 — the 11 CRM events, published after the change is committed. Ids are the public uuids.

public interface ICrmEvent { Guid OrganizationId { get; } }

public sealed record LeadCreatedEvent(Guid LeadId, Guid OrganizationId, Guid? LeadSourceId, Guid? CampaignId) : ICrmEvent;
public sealed record LeadAssignedEvent(Guid LeadId, Guid OrganizationId, int? NewUserId, int? PreviousUserId, string LeadNumber, string Topic) : ICrmEvent;
public sealed record LeadStatusChangedEvent(Guid LeadId, Guid OrganizationId, string OldStatus, string NewStatus, string? Reason) : ICrmEvent;
public sealed record LeadQualifiedEvent(Guid LeadId, Guid OrganizationId) : ICrmEvent;
public sealed record LeadConvertedEvent(Guid LeadId, Guid OpportunityId, Guid AccountId, int ContactId, Guid OrganizationId) : ICrmEvent;
public sealed record LeadDisqualifiedEvent(Guid LeadId, Guid OrganizationId, string Reason) : ICrmEvent;
public sealed record OpportunityCreatedEvent(Guid OpportunityId, Guid LeadId, Guid AccountId, Guid OrganizationId) : ICrmEvent;
public sealed record OpportunityStageChangedEvent(Guid OpportunityId, Guid OrganizationId, Guid? OldStageId, Guid NewStageId) : ICrmEvent;
public sealed record OpportunityWonEvent(Guid OpportunityId, Guid OrganizationId, Guid SaleOrderId, decimal ActualRevenue) : ICrmEvent;
public sealed record OpportunityLostEvent(Guid OpportunityId, Guid OrganizationId, string Reason, string? Competitor) : ICrmEvent;
public sealed record ActivityCreatedEvent(Guid ActivityId, Guid? LeadId, Guid? OpportunityId, string ActivityType, Guid OrganizationId) : ICrmEvent;

/// <summary>Handles one kind of CRM event. Register as many as needed in DI.</summary>
public interface ICrmEventHandler<in TEvent> where TEvent : ICrmEvent
{
    Task HandleAsync(TEvent evt, CancellationToken ct = default);
}

public interface ICrmEventPublisher
{
    /// <summary>Runs every handler of the event. A failing handler is logged and never fails the caller.</summary>
    Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : ICrmEvent;
}

internal sealed class CrmEventPublisher : ICrmEventPublisher
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CrmEventPublisher>? _log;

    public CrmEventPublisher(IServiceProvider services, ILogger<CrmEventPublisher>? log = null)
    {
        _services = services;
        _log = log;
    }

    public async Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : ICrmEvent
    {
        foreach (var handler in _services.GetServices<ICrmEventHandler<TEvent>>())
        {
            try
            {
                await handler.HandleAsync(evt, ct);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "CRM event handler {Handler} failed for {Event}.", handler.GetType().Name, typeof(TEvent).Name);
            }
        }
    }
}

/// <summary>D-17 — tells the new owner of a lead that it is theirs.</summary>
internal sealed class LeadAssignedNotifier : ICrmEventHandler<LeadAssignedEvent>
{
    private readonly INotificationService? _notifications;
    private readonly ITenantContext _tenant;

    public LeadAssignedNotifier(ITenantContext tenant, INotificationService? notifications = null)
    {
        _tenant = tenant;
        _notifications = notifications;
    }

    public async Task HandleAsync(LeadAssignedEvent evt, CancellationToken ct = default)
    {
        if (_notifications is null || evt.NewUserId is not { } userId || userId <= 0) return;
        await _notifications.TryCreateAsync(new NotificationRequest(
            userId, "CRM_LEAD_ASSIGNED", $"Lead {evt.LeadNumber} assigned to you", evt.Topic, "CRM",
            EntityType: "CRM_LEAD", EntityUuid: evt.LeadId.ToString(),
            NavigationUrl: $"/portal/pages/crm/leads/{evt.LeadId}"));
    }
}
