namespace SMS.Shared.Common;

// The fixed, well-known id of the "Supply Chain Demo" organization seeded by
// TenancyDataSeeder. MT-003's migrations backfill every pre-existing row's organization_id to
// this value, so it must be a stable constant rather than a runtime Guid.NewGuid() — the seeder
// itself uses this same constant so fresh databases and this already-seeded dev database agree.
public static class TenantDefaults
{
    public static readonly Guid ScmDemoOrganizationId = Guid.Parse("84D0A96D-52D4-4375-9260-357B46FB9D9F");
}
