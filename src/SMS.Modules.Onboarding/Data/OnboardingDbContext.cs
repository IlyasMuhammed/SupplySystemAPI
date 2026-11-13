using Microsoft.EntityFrameworkCore;
using SMS.Modules.Onboarding.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Onboarding.Data;

/// <summary>A50 D-2 — schema <c>onboarding</c>. The tenant filter applies to every org-scoped entity (SetupDefaults are
/// platform rows and have none); services also filter on the organization explicitly.</summary>
internal sealed class OnboardingDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "onboarding";

    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public OnboardingDbContext(DbContextOptions<OnboardingDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<SetupDefault>            SetupDefaults           => Set<SetupDefault>();
    internal DbSet<TenantSetupLog>          TenantSetupLogs         => Set<TenantSetupLog>();
    internal DbSet<OrganizationSetup>       OrganizationSetups      => Set<OrganizationSetup>();
    internal DbSet<SeededRecord>            SeededRecords           => Set<SeededRecord>();
    internal DbSet<OnboardingChecklist>     OnboardingChecklists    => Set<OnboardingChecklist>();
    internal DbSet<OnboardingChecklistStep> OnboardingChecklistSteps => Set<OnboardingChecklistStep>();
    internal DbSet<DocumentTemplate>        DocumentTemplates       => Set<DocumentTemplate>();
    internal DbSet<EmailTemplate>           EmailTemplates          => Set<EmailTemplate>();
    internal DbSet<DemoRecord>              DemoRecords             => Set<DemoRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OnboardingDbContext).Assembly);
        modelBuilder.ApplyTenantQueryFilters(this);
        modelBuilder.ApplyTenantIndexes();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BeforeSave()
    {
        this.StampTenantScopedEntities(_tenantContext);
        this.StampModifiedAt();
    }
}
