using Microsoft.EntityFrameworkCore;
using SMS.Modules.Crm.Data;
using SMS.Modules.Crm.Domain;
using SMS.Modules.Crm.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Crm.Services;

/// <summary>A41 C6 §8 / D-19 — a QUALIFIED lead becomes an account (customer), a contact person and an opportunity.</summary>
public interface ILeadConversionService
{
    Task<LeadConversionResult> ConvertAsync(CrmCaller caller, Guid leadUuid, LeadConvertRequest options, CancellationToken ct = default);
    /// <summary>§8.4 — the top 5 possible duplicates of the lead's company among the organization's partners, or a search.</summary>
    Task<IReadOnlyList<CrmAccountModel>> AccountMatchesAsync(CrmCaller caller, Guid leadUuid, string? search, CancellationToken ct = default);
    Task<IReadOnlyList<CrmContactModel>> AccountContactsAsync(CrmCaller caller, Guid accountUuid, CancellationToken ct = default);
}

internal sealed class LeadConversionService : ILeadConversionService
{
    internal const string NumberPrefix = "OP";

    private readonly CrmDbContext _db;
    private readonly LeadService _leads;
    private readonly IDocumentNumberGenerator _numbers;
    private readonly CrmDirectory _directory;
    private readonly ICrmEventPublisher _events;

    public LeadConversionService(CrmDbContext db, LeadService leads, IDocumentNumberGenerator numbers, CrmDirectory directory, ICrmEventPublisher events)
    {
        _db = db;
        _leads = leads;
        _numbers = numbers;
        _directory = directory;
        _events = events;
    }

    private ICrmAccountGateway Accounts => _directory.Accounts
        ?? throw new BadRequestException("Customer records are not available in this installation, so leads cannot be converted.");

    public async Task<IReadOnlyList<CrmAccountModel>> AccountMatchesAsync(CrmCaller caller, Guid leadUuid, string? search, CancellationToken ct = default)
    {
        var lead = await _leads.LoadAsync(caller, leadUuid, ct);
        var found = string.IsNullOrWhiteSpace(search)
            ? await Accounts.FindMatchesAsync(caller.OrganizationId, lead.CompanyName, lead.Email, lead.MobilePhone ?? lead.Phone, 5, ct)
            : await Accounts.SearchAsync(caller.OrganizationId, search, 20, ct);
        return found.Select(CrmDirectory.ToModel).ToList();
    }

    public async Task<IReadOnlyList<CrmContactModel>> AccountContactsAsync(CrmCaller caller, Guid accountUuid, CancellationToken ct = default) =>
        (await Accounts.ListContactsAsync(caller.OrganizationId, accountUuid, ct)).Select(CrmDirectory.ToModel).ToList();

    public async Task<LeadConversionResult> ConvertAsync(CrmCaller caller, Guid leadUuid, LeadConvertRequest options, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var lead = await _leads.LoadAsync(caller, leadUuid, ct);
        // 1. BR-05 — only a qualified lead converts.
        if (lead.Status != LeadStatuses.Qualified)
        {
            if (lead.Status == LeadStatuses.Converted) throw new ConflictException("This lead has already been converted.");
            throw new BadRequestException("Only a qualified lead can be converted.");
        }
        LeadService.RequireOwnership(caller, lead);

        var assignee = options.AssignedToUserId is > 0 ? options.AssignedToUserId : lead.AssignedToUserId;
        if (assignee != lead.AssignedToUserId && assignee is { } who)
        {
            caller.Require(PermissionCodes.CRM_ASSIGN_LEAD, "Only a sales manager can give the opportunity to someone else.");
            await _directory.RequireActiveUserAsync(org, who, "opportunity owner", ct);
        }
        var topic = options.Topic is null ? lead.Topic : CrmText.Required(options.Topic, 200, "Topic");
        var revenue = options.EstimatedRevenue is null ? lead.EstimatedRevenue : CrmText.Money(options.EstimatedRevenue, "Estimated revenue");
        if (revenue is not null && lead.CurrencyCode is null && options.EstimatedRevenue is not null)
            throw new BadRequestException("The lead has no currency for the estimated revenue; set it on the lead first.");
        var firstStage = await CrmPipeline.FirstAsync(_db, org, lead.SalesTeamId, StageTypes.Opportunity, ct)
            ?? throw new BadRequestException("The organization has no active opportunity stage. Add one under CRM settings first.");

        // 2. Account: link (it must exist, become a customer) or create.
        Guid accountUuid;
        bool newAccount;
        if (options.ExistingAccountUuid is { } existing)
        {
            var account = (await Accounts.GetAccountsAsync(org, [existing], ct)).GetValueOrDefault(existing)
                ?? throw new BadRequestException("The account to link was not found.");
            if (!account.IsActive) throw new BadRequestException($"{account.Name} is inactive; activate it or choose another account.");
            if (!account.IsCustomer) await Accounts.EnsureCustomerAsync(org, existing, caller.UserId, ct);
            accountUuid = existing;
            newAccount = false;
        }
        else
        {
            if (options.ExistingContactId is not null)
                throw new BadRequestException("An existing contact can only be linked together with its existing account.");
            var individual = string.Equals(lead.CompanyName.Trim(), lead.FullName.Trim(), StringComparison.OrdinalIgnoreCase);
            accountUuid = await Accounts.CreateCustomerAsync(org, new CrmNewAccount(
                lead.CompanyName, individual ? "INDIVIDUAL" : "COMPANY", lead.Phone, lead.MobilePhone, lead.Email, lead.Website,
                lead.AddressLine1, lead.AddressLine2, lead.City, lead.StateProvince, lead.PostalCode, lead.Country), caller.UserId, ct);
            newAccount = true;
        }

        // 3. Contact person: link (must belong to that account — BR-23) or create under the account.
        int contactId;
        bool newContact;
        if (options.ExistingContactId is { } existingContact)
        {
            var contact = (await Accounts.GetContactsAsync(org, [existingContact], ct)).GetValueOrDefault(existingContact)
                ?? throw new BadRequestException("The contact to link was not found.");
            if (contact.AccountUuid != accountUuid)
                throw new BadRequestException("The contact belongs to another account. Choose one of this account's contacts.");
            contactId = existingContact;
            newContact = false;
        }
        else
        {
            var name = string.Join(' ', new[] { lead.Salutation, lead.FirstName, lead.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            contactId = await Accounts.CreateContactAsync(org, accountUuid,
                new CrmNewContact(name, lead.JobTitle, lead.MobilePhone ?? lead.Phone, lead.Email), ct);
            newContact = true;
        }

        // 4. The opportunity; 5. the lead closes as CONVERTED — one save.
        var opportunity = new CrmOpportunity
        {
            OrganizationId = org, OpportunityNumber = await _numbers.NextAsync(NumberPrefix, DateTime.UtcNow, org, ct),
            Topic = topic, Description = lead.Description, AccountUuid = accountUuid, ContactPersonId = contactId,
            SourceLeadId = lead.Id, CampaignId = lead.CampaignId, PipelineStageId = firstStage.Id, Status = OpportunityStatuses.Open,
            EstimatedRevenue = revenue, CurrencyCode = lead.CurrencyCode,
            EstimatedCloseDate = options.EstimatedCloseDate?.Date ?? lead.EstimatedCloseDate,
            AssignedToUserId = assignee, SalesTeamId = lead.SalesTeamId,
            CreatedByUserId = caller.UserId, UpdatedByUserId = caller.UserId
        };
        _db.Opportunities.Add(opportunity);
        await _db.SaveChangesAsync(ct);

        lead.Status = LeadStatuses.Converted;
        lead.ConvertedAt = DateTime.UtcNow;
        lead.ConvertedAccountUuid = accountUuid;
        lead.ConvertedContactId = contactId;
        lead.ConvertedOpportunityId = opportunity.Id;
        lead.AccountUuid = accountUuid;
        lead.ContactPersonId = contactId;
        lead.UpdatedByUserId = caller.UserId;
        _leads.AddHistory(caller, lead, HistoryKinds.Status, LeadStatuses.Qualified, LeadStatuses.Converted, null);
        _leads.AddHistory(caller, lead, HistoryKinds.Converted, null, opportunity.OpportunityNumber,
            $"{(newAccount ? "New" : "Existing")} account, {(newContact ? "new" : "existing")} contact");
        _db.RecordHistory.Add(new CrmRecordHistory
        {
            OrganizationId = org, EntityType = HistoryEntities.Opportunity, EntityId = opportunity.Id, Kind = HistoryKinds.Created,
            ToValue = OpportunityStatuses.Open, Reason = lead.LeadNumber, UserId = caller.UserId, At = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);

        await _events.PublishAsync(new LeadStatusChangedEvent(lead.Uuid, org, LeadStatuses.Qualified, LeadStatuses.Converted, null), ct);
        await _events.PublishAsync(new LeadConvertedEvent(lead.Uuid, opportunity.Uuid, accountUuid, contactId, org), ct);
        await _events.PublishAsync(new OpportunityCreatedEvent(opportunity.Uuid, lead.Uuid, accountUuid, org), ct);

        return new LeadConversionResult
        {
            OpportunityUuid = opportunity.Uuid, OpportunityNumber = opportunity.OpportunityNumber, AccountUuid = accountUuid,
            ContactId = contactId, IsNewAccount = newAccount, IsNewContact = newContact
        };
    }
}
