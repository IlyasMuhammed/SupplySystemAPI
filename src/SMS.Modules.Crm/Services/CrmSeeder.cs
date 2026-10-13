using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Crm.Data;
using SMS.Modules.Crm.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Crm.Services;

/// <summary>
/// D-7 — each organization's 14 lead sources (§4.2) and 8 default pipeline stages (§7.2), created the first time the
/// organization uses the CRM (and for every organization at startup). Idempotent: only missing codes are added, and the
/// unique indexes make a concurrent second seed fail harmlessly (it is retried as "already there").
/// </summary>
public interface ICrmSeeder
{
    Task EnsureAsync(Guid organizationId, CancellationToken ct = default);
}

internal sealed class CrmSeeder : ICrmSeeder, IOrganizationProvisionedHandler
{
    public Task OnOrganizationProvisionedAsync(Guid organizationId, CancellationToken ct = default) => EnsureAsync(organizationId, ct);

    internal static readonly (string Code, string Name)[] LeadSources =
    [
        ("WEB_FORM", "Website Form"), ("FACEBOOK", "Facebook"), ("INSTAGRAM", "Instagram"), ("WHATSAPP", "WhatsApp"),
        ("PHONE_CALL", "Phone Call"), ("WALK_IN", "Walk-in"), ("REFERRAL", "Referral"), ("EXISTING_CUSTOMER", "Existing Customer"),
        ("EMAIL", "Email"), ("LINKEDIN", "LinkedIn"), ("IMPORT", "Data Import"), ("PARTNER", "Partner/Reseller"),
        ("TRADE_SHOW", "Trade Show / Exhibition"), ("OTHER", "Other"),
    ];

    internal static readonly (string Code, string Name, string Type, int Sequence, int Probability, bool Won, bool Lost)[] Stages =
    [
        ("NEW_LEAD",        "New Lead",             StageTypes.Lead,        10, 5,   false, false),
        ("INITIAL_CONTACT", "Initial Contact",      StageTypes.Lead,        20, 10,  false, false),
        ("QUALIFICATION",   "Qualification",        StageTypes.Lead,        30, 20,  false, false),
        ("NEEDS_ANALYSIS",  "Needs Analysis",       StageTypes.Opportunity, 40, 40,  false, false),
        ("PROPOSAL",        "Proposal / Quotation", StageTypes.Opportunity, 50, 60,  false, false),
        ("NEGOTIATION",     "Negotiation",          StageTypes.Opportunity, 60, 75,  false, false),
        ("WON",             "Won",                  StageTypes.Opportunity, 70, 100, true,  false),
        ("LOST",            "Lost",                 StageTypes.Opportunity, 80, 0,   false, true),
    ];

    /// <summary>Organizations seeded in this process — the check costs a query only once per organization.</summary>
    private static readonly ConcurrentDictionary<Guid, bool> Seeded = new();

    private readonly CrmDbContext _db;

    public CrmSeeder(CrmDbContext db) => _db = db;

    internal static void ForgetAll() => Seeded.Clear();

    public async Task EnsureAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty || Seeded.ContainsKey(organizationId)) return;
        try
        {
            await SeedAsync(organizationId, ct);
        }
        catch (DbUpdateException)
        {
            // Another request seeded the same organization at the same moment; the unique index refused our copy.
            _db.ChangeTracker.Clear();
        }
        Seeded[organizationId] = true;
    }

    private async Task SeedAsync(Guid org, CancellationToken ct)
    {
        var sourceCodes = await _db.LeadSources.IgnoreQueryFilters().Where(s => s.OrganizationId == org)
            .Select(s => s.Code).ToListAsync(ct);
        var order = 0;
        foreach (var (code, name) in LeadSources)
        {
            order += 10;
            if (sourceCodes.Contains(code)) continue;
            _db.LeadSources.Add(new CrmLeadSource
            {
                OrganizationId = org, Code = code, Name = name, SortOrder = order, IsSystem = true
            });
        }

        var stageCodes = await _db.PipelineStages.IgnoreQueryFilters().Where(s => s.OrganizationId == org && s.SalesTeamId == null)
            .Select(s => s.Code).ToListAsync(ct);
        foreach (var s in Stages)
        {
            if (stageCodes.Contains(s.Code)) continue;
            _db.PipelineStages.Add(new CrmPipelineStage
            {
                OrganizationId = org, Code = s.Code, Name = s.Name, StageType = s.Type, Sequence = s.Sequence,
                WinProbability = s.Probability, IsWon = s.Won, IsLost = s.Lost
            });
        }

        if (_db.ChangeTracker.HasChanges()) await _db.SaveChangesAsync(ct);
    }
}
