/*
 * MT-007 / FSD Section 7.1 — one-time multi-tenancy migration verification script.
 *
 * Run once against the target database immediately after applying every MT-003..MT-007
 * migration, before onboarding any organization beyond SCM-DEMO. It has two independent checks:
 *
 *   1. NULL organization_id audit — every tenant-scoped table (any table with an
 *      OrganizationId column) must have zero NULL rows, with the single documented exception of
 *      lookups.LookupValues, whose OrganizationId is deliberately nullable for global (cross-org)
 *      catalog rows such as currencies (see IGloballyExemptTenantScopedEntity, MT-003).
 *
 *   2. SCM-DEMO-only audit — before this migration, the system was single-tenant, so every
 *      non-null organization_id value in every table must equal SCM-DEMO's id. A second/third
 *      distinct org id shows up only once real multi-tenant onboarding has begun (via
 *      POST /api/system/organizations), which is expected after that point — this script is a
 *      pre-onboarding gate, not a permanent invariant.
 *
 * Purely read-only — SELECT/PRINT statements only, safe to run against production.
 */

SET NOCOUNT ON;

DECLARE @ScmDemoOrgId uniqueidentifier =
    (SELECT Id FROM tenant.Organizations WHERE OrgCode = 'SCM-DEMO');

IF @ScmDemoOrgId IS NULL
BEGIN
    PRINT 'FAIL: could not resolve SCM-DEMO organization id from tenant.Organizations — aborting.';
    RETURN;
END

PRINT CONCAT('SCM-DEMO organization id: ', CAST(@ScmDemoOrgId AS nvarchar(36)));
PRINT '';

-- ── Discover every tenant-scoped table ────────────────────────────────────────
-- Any table with a column literally named OrganizationId, across every schema.
DECLARE @Tables TABLE (SchemaName sysname, TableName sysname, IsExpectedNullable bit);

INSERT INTO @Tables (SchemaName, TableName, IsExpectedNullable)
SELECT
    s.name,
    t.name,
    CASE WHEN s.name = 'lookups' AND t.name = 'LookupValues' THEN 1 ELSE 0 END
FROM sys.columns c
JOIN sys.tables  t ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE c.name = 'OrganizationId';

DECLARE @Results TABLE (
    SchemaName        sysname,
    TableName         sysname,
    TotalRows         int,
    NullOrgIdRows     int,
    NonScmDemoRows    int,
    ExpectedNullable  bit
);

DECLARE @Schema sysname, @Table sysname, @ExpectedNullable bit, @Sql nvarchar(max);

DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT SchemaName, TableName, IsExpectedNullable FROM @Tables;

OPEN cur;
FETCH NEXT FROM cur INTO @Schema, @Table, @ExpectedNullable;

WHILE @@FETCH_STATUS = 0
BEGIN
    -- Dynamic SQL executed via sp_executesql runs in its own scope and cannot see the outer
    -- batch's @Results table variable directly, so this only produces a result set — the caller
    -- captures it with "INSERT INTO @Results EXEC sp_executesql ..." below.
    SET @Sql = N'
        SELECT ''' + @Schema + N''' AS SchemaName, ''' + @Table + N''' AS TableName,
            COUNT(*) AS TotalRows,
            SUM(CASE WHEN OrganizationId IS NULL THEN 1 ELSE 0 END) AS NullOrgIdRows,
            SUM(CASE WHEN OrganizationId IS NOT NULL AND OrganizationId <> @OrgId THEN 1 ELSE 0 END) AS NonScmDemoRows,
            ' + CAST(@ExpectedNullable AS nvarchar(1)) + N' AS ExpectedNullable
        FROM ' + QUOTENAME(@Schema) + N'.' + QUOTENAME(@Table) + N';';

    INSERT INTO @Results
    EXEC sp_executesql @Sql, N'@OrgId uniqueidentifier', @OrgId = @ScmDemoOrgId;

    FETCH NEXT FROM cur INTO @Schema, @Table, @ExpectedNullable;
END

CLOSE cur;
DEALLOCATE cur;

-- ── Report ─────────────────────────────────────────────────────────────────────
SELECT
    SchemaName, TableName, TotalRows, NullOrgIdRows, NonScmDemoRows,
    CASE
        WHEN NullOrgIdRows > 0 AND ExpectedNullable = 0
            THEN 'FAIL: unexpected NULL organization_id'
        WHEN NonScmDemoRows > 0
            THEN 'INFO: contains rows for orgs other than SCM-DEMO (expected once onboarding has started)'
        ELSE 'OK'
    END AS Verdict
FROM @Results
ORDER BY
    CASE WHEN NullOrgIdRows > 0 AND ExpectedNullable = 0 THEN 0 ELSE 1 END,
    SchemaName, TableName;

DECLARE @UnexpectedNulls int =
    (SELECT COUNT(*) FROM @Results WHERE NullOrgIdRows > 0 AND ExpectedNullable = 0);
DECLARE @NonScmDemoTables int =
    (SELECT COUNT(*) FROM @Results WHERE NonScmDemoRows > 0);

PRINT '';
IF @UnexpectedNulls > 0
    PRINT CONCAT('FAIL: ', @UnexpectedNulls, ' table(s) have unexpected NULL organization_id rows — see Verdict column above.');
ELSE
    PRINT 'PASS: zero unexpected NULL organization_id rows across all tenant-scoped tables.';

IF @NonScmDemoTables > 0
    PRINT CONCAT('INFO: ', @NonScmDemoTables, ' table(s) already contain data for organizations other than SCM-DEMO (multi-tenant onboarding has begun).');
ELSE
    PRINT 'INFO: every row in every tenant-scoped table currently belongs to SCM-DEMO.';

-- ── Explicit spot-checks called out by the ticket ─────────────────────────────
PRINT '';
PRINT '-- auth.Roles --';
SELECT COUNT(*) AS TotalRoles,
       SUM(CASE WHEN OrganizationId = @ScmDemoOrgId THEN 1 ELSE 0 END) AS ScmDemoRoles,
       SUM(CASE WHEN OrganizationId IS NULL THEN 1 ELSE 0 END) AS NullOrgIdRoles
FROM auth.Roles;

PRINT '-- workflow_schema.workflow_definitions --';
SELECT COUNT(*) AS TotalWorkflowDefinitions,
       SUM(CASE WHEN OrganizationId = @ScmDemoOrgId THEN 1 ELSE 0 END) AS ScmDemoWorkflowDefinitions,
       SUM(CASE WHEN OrganizationId IS NULL THEN 1 ELSE 0 END) AS NullOrgIdWorkflowDefinitions
FROM workflow_schema.workflow_definitions;

PRINT '-- tenant.SuperAdminUsers --';
SELECT su.UserId, su.CreatedAt, u.Email, u.RoleID
FROM tenant.SuperAdminUsers su
LEFT JOIN auth.UserAccounts u ON u.UserID = su.UserId;
