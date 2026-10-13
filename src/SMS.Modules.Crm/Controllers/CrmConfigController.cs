using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Crm.Domain;
using SMS.Modules.Crm.Models;
using SMS.Modules.Crm.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Crm.Controllers;

/// <summary>Shared by the CRM controllers: the caller (organization, user, permissions) of this request.</summary>
public abstract class CrmControllerBase : ControllerBase
{
    private readonly ITenantContext _tenant;

    protected CrmControllerBase(ITenantContext tenant) => _tenant = tenant;

    protected CrmCaller Caller => CrmCaller.From(User, _tenant);
}

/// <summary>A41 API-CONTRACT §2 — CRM configuration and lookups.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Crm)]
public sealed class CrmConfigController : CrmControllerBase
{
    private readonly ILeadSourceService _sources;
    private readonly ICampaignService _campaigns;
    private readonly ISalesTeamService _teams;
    private readonly IPipelineStageService _stages;
    private readonly ILeadApiKeyService _keys;
    private readonly ILeadConversionService _conversion;
    private readonly IOrgUserDirectory? _users;
    private readonly IModuleGate? _gate;

    public CrmConfigController(ITenantContext tenant, ILeadSourceService sources, ICampaignService campaigns, ISalesTeamService teams,
        IPipelineStageService stages, ILeadApiKeyService keys, ILeadConversionService conversion,
        IOrgUserDirectory? users = null, IModuleGate? gate = null) : base(tenant)
    {
        _sources = sources;
        _campaigns = campaigns;
        _teams = teams;
        _stages = stages;
        _keys = keys;
        _conversion = conversion;
        _users = users;
        _gate = gate;
    }

    // ── meta & users ──

    [HttpGet("api/crm/meta")]
    [RequirePermission(P.CRM_VIEW_LEADS, P.CRM_VIEW_OPPORTUNITIES, P.CRM_VIEW_ACTIVITIES, P.CRM_VIEW_CAMPAIGNS, P.CRM_ADMIN)]
    public async Task<IActionResult> Meta(CancellationToken ct) => Ok(ApiResponse<CrmMetaModel>.Ok(new CrmMetaModel
    {
        LeadStatuses = LeadStatuses.All, Ratings = LeadRatings.All, Priorities = LeadPriorities.All, PurchaseTimeframes = PurchaseTimeframes.All,
        ExternalSystems = ExternalSourceSystems.All, OpportunityStatuses = OpportunityStatuses.All, CampaignTypes = CampaignTypes.All,
        CampaignStatuses = CampaignStatuses.All, ActivityTypes = ActivityTypes.All, ContactActivityTypes = ActivityTypes.Contact.ToList(),
        ActivityOutcomes = ActivityOutcomes.All, ParticipantTypes = ParticipantTypes.All, ParticipantRoles = ParticipantRoles.All,
        DocumentTypes = DocumentTypes.All, DocumentDirections = DocumentDirections.All,
        CampaignTrackingEnabled = _gate is null || await _gate.IsEnabledAsync(Caller.OrganizationId, CrmFeatureCodes.CampaignTracking, ct)
    }));

    /// <summary>Active users of the organization (assignment, participants, team members).</summary>
    [HttpGet("api/crm/users")]
    [RequirePermission(P.CRM_VIEW_LEADS, P.CRM_VIEW_OPPORTUNITIES, P.CRM_VIEW_ACTIVITIES, P.CRM_ADMIN)]
    public async Task<IActionResult> Users([FromQuery] string? search, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var users = _users is null ? [] : await _users.SearchAsync(Caller.OrganizationId, search, limit, ct);
        return Ok(ApiResponse<List<CrmUserModel>>.Ok(users.Select(u => new CrmUserModel
        {
            UserId = u.UserId, Name = u.Name, Email = u.Email, IsActive = u.IsActive
        }).ToList()));
    }

    // ── accounts (conversion dialog, activity contact pickers) ──

    [HttpGet("api/crm/accounts/{uuid:guid}/contacts")]
    [RequirePermission(P.CRM_VIEW_LEADS, P.CRM_VIEW_OPPORTUNITIES, P.CRM_VIEW_ACTIVITIES)]
    public async Task<IActionResult> AccountContacts(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<CrmContactModel>>.Ok(await _conversion.AccountContactsAsync(Caller, uuid, ct)));

    // ── lead sources (§14.5) ──

    [HttpGet("api/crm/lead-sources")]
    [RequirePermission(P.CRM_VIEW_LEADS, P.CRM_VIEW_CAMPAIGNS, P.CRM_ADMIN)]
    public async Task<IActionResult> LeadSources([FromQuery] bool includeInactive, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<LeadSourceModel>>.Ok(await _sources.GetAllAsync(Caller, includeInactive, ct)));

    [HttpPost("api/crm/lead-sources")]
    [RequirePermission(P.CRM_ADMIN)]
    public async Task<IActionResult> CreateLeadSource([FromBody] LeadSourceUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<LeadSourceModel>.Ok(await _sources.CreateAsync(Caller, req, ct), "Lead source created."));

    [HttpPut("api/crm/lead-sources/{uuid:guid}")]
    [RequirePermission(P.CRM_ADMIN)]
    public async Task<IActionResult> UpdateLeadSource(Guid uuid, [FromBody] LeadSourceUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<LeadSourceModel>.Ok(await _sources.UpdateAsync(Caller, uuid, req, ct), "Lead source saved."));

    // ── campaigns (§14.4) ──

    [HttpGet("api/crm/campaigns")]
    [RequiresFeature(CrmFeatureCodes.CampaignTracking)]
    [RequirePermission(P.CRM_VIEW_CAMPAIGNS, P.CRM_MANAGE_CAMPAIGNS, P.CRM_VIEW_LEADS)]
    public async Task<IActionResult> Campaigns([FromQuery] CampaignListFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<CampaignModel>>.Ok(await _campaigns.ListAsync(Caller, filter, ct)));

    [HttpGet("api/crm/campaigns/{uuid:guid}")]
    [RequiresFeature(CrmFeatureCodes.CampaignTracking)]
    [RequirePermission(P.CRM_VIEW_CAMPAIGNS, P.CRM_MANAGE_CAMPAIGNS)]
    public async Task<IActionResult> Campaign(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<CampaignModel>.Ok(await _campaigns.GetByIdAsync(Caller, uuid, ct)));

    [HttpPost("api/crm/campaigns")]
    [RequiresFeature(CrmFeatureCodes.CampaignTracking)]
    [RequirePermission(P.CRM_MANAGE_CAMPAIGNS)]
    public async Task<IActionResult> CreateCampaign([FromBody] CampaignUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<CampaignModel>.Ok(await _campaigns.CreateAsync(Caller, req, ct), "Campaign created."));

    [HttpPut("api/crm/campaigns/{uuid:guid}")]
    [RequiresFeature(CrmFeatureCodes.CampaignTracking)]
    [RequirePermission(P.CRM_MANAGE_CAMPAIGNS)]
    public async Task<IActionResult> UpdateCampaign(Guid uuid, [FromBody] CampaignUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<CampaignModel>.Ok(await _campaigns.UpdateAsync(Caller, uuid, req, ct), "Campaign saved."));

    [HttpDelete("api/crm/campaigns/{uuid:guid}")]
    [RequiresFeature(CrmFeatureCodes.CampaignTracking)]
    [RequirePermission(P.CRM_MANAGE_CAMPAIGNS)]
    public async Task<IActionResult> DeleteCampaign(Guid uuid, CancellationToken ct)
    {
        await _campaigns.DeleteAsync(Caller, uuid, ct);
        return Ok(ApiResponse.Ok("Campaign deleted."));
    }

    // ── sales teams (§14.5) ──

    [HttpGet("api/crm/sales-teams")]
    [RequirePermission(P.CRM_VIEW_LEADS, P.CRM_VIEW_OPPORTUNITIES, P.CRM_ADMIN)]
    public async Task<IActionResult> SalesTeams([FromQuery] bool includeInactive, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<SalesTeamModel>>.Ok(await _teams.ListAsync(Caller, includeInactive, ct)));

    [HttpGet("api/crm/sales-teams/{uuid:guid}")]
    [RequirePermission(P.CRM_VIEW_LEADS, P.CRM_VIEW_OPPORTUNITIES, P.CRM_ADMIN)]
    public async Task<IActionResult> SalesTeam(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<SalesTeamModel>.Ok(await _teams.GetByIdAsync(Caller, uuid, ct)));

    [HttpPost("api/crm/sales-teams")]
    [RequirePermission(P.CRM_ADMIN)]
    public async Task<IActionResult> CreateSalesTeam([FromBody] SalesTeamUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<SalesTeamModel>.Ok(await _teams.CreateAsync(Caller, req, ct), "Sales team created."));

    [HttpPut("api/crm/sales-teams/{uuid:guid}")]
    [RequirePermission(P.CRM_ADMIN)]
    public async Task<IActionResult> UpdateSalesTeam(Guid uuid, [FromBody] SalesTeamUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<SalesTeamModel>.Ok(await _teams.UpdateAsync(Caller, uuid, req, ct), "Sales team saved."));

    [HttpPost("api/crm/sales-teams/{uuid:guid}/members")]
    [RequirePermission(P.CRM_ADMIN)]
    public async Task<IActionResult> AddMember(Guid uuid, [FromBody] SalesTeamMemberRequest req, CancellationToken ct) =>
        Ok(ApiResponse<SalesTeamModel>.Ok(await _teams.AddMemberAsync(Caller, uuid, req, ct), "Member added."));

    [HttpDelete("api/crm/sales-teams/{uuid:guid}/members/{userId:int}")]
    [RequirePermission(P.CRM_ADMIN)]
    public async Task<IActionResult> RemoveMember(Guid uuid, int userId, CancellationToken ct) =>
        Ok(ApiResponse<SalesTeamModel>.Ok(await _teams.RemoveMemberAsync(Caller, uuid, userId, ct), "Member removed."));

    // ── pipeline stages (§14.5) ──

    [HttpGet("api/crm/pipeline-stages")]
    [RequirePermission(P.CRM_VIEW_LEADS, P.CRM_VIEW_OPPORTUNITIES, P.CRM_ADMIN)]
    public async Task<IActionResult> PipelineStages([FromQuery] PipelineStageFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PipelineStageModel>>.Ok(await _stages.GetStagesAsync(Caller, filter, ct)));

    [HttpPost("api/crm/pipeline-stages")]
    [RequirePermission(P.CRM_ADMIN)]
    public async Task<IActionResult> CreatePipelineStage([FromBody] PipelineStageUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<PipelineStageModel>.Ok(await _stages.CreateAsync(Caller, req, ct), "Pipeline stage created."));

    [HttpPut("api/crm/pipeline-stages/{uuid:guid}")]
    [RequirePermission(P.CRM_ADMIN)]
    public async Task<IActionResult> UpdatePipelineStage(Guid uuid, [FromBody] PipelineStageUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<PipelineStageModel>.Ok(await _stages.UpdateAsync(Caller, uuid, req, ct), "Pipeline stage saved."));

    [HttpDelete("api/crm/pipeline-stages/{uuid:guid}")]
    [RequirePermission(P.CRM_ADMIN)]
    public async Task<IActionResult> DeactivatePipelineStage(Guid uuid, CancellationToken ct)
    {
        await _stages.DeactivateAsync(Caller, uuid, ct);
        return Ok(ApiResponse.Ok("Pipeline stage deactivated."));
    }

    // ── public API keys (D-16) ──

    [HttpGet("api/crm/api-keys")]
    [RequirePermission(P.CRM_ADMIN)]
    public async Task<IActionResult> ApiKeys(CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<LeadApiKeyModel>>.Ok(await _keys.ListAsync(Caller, ct)));

    [HttpPost("api/crm/api-keys")]
    [RequirePermission(P.CRM_ADMIN)]
    public async Task<IActionResult> CreateApiKey([FromBody] LeadApiKeyUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<LeadApiKeyCreated>.Ok(await _keys.CreateAsync(Caller, req, ct), "API key created. Copy it now: it will not be shown again."));

    [HttpPut("api/crm/api-keys/{uuid:guid}")]
    [RequirePermission(P.CRM_ADMIN)]
    public async Task<IActionResult> UpdateApiKey(Guid uuid, [FromBody] LeadApiKeyUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<LeadApiKeyModel>.Ok(await _keys.UpdateAsync(Caller, uuid, req, ct), "API key saved."));

    [HttpDelete("api/crm/api-keys/{uuid:guid}")]
    [RequirePermission(P.CRM_ADMIN)]
    public async Task<IActionResult> DeleteApiKey(Guid uuid, CancellationToken ct)
    {
        await _keys.DeleteAsync(Caller, uuid, ct);
        return Ok(ApiResponse.Ok("API key deleted."));
    }
}
