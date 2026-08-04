using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.WorkflowEngine.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillDocumentTimelineOrganizationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The prior migration (AddOrganizationIdTenantScoping) stamped every existing
            // document_timelines row with a hardcoded default org, since Hangfire jobs had no way
            // to resolve the real tenant at write time (see HangfireTenantScope/TenantContext).
            // Recover the true org for each row by joining its TraceId back to whichever document
            // table actually owns that chain root — every document type's own row is already
            // correctly tenant-scoped, so it's the authoritative source here.
            migrationBuilder.Sql(@"
                UPDATE dt
                SET dt.OrganizationId = COALESCE(
                    pr.OrganizationId,
                    q.OrganizationId,
                    po.OrganizationId,
                    g.OrganizationId,
                    mir.OrganizationId,
                    inv.OrganizationId,
                    '84d0a96d-52d4-4375-9260-357b46fb9d9f')
                FROM workflow_schema.document_timelines dt
                LEFT JOIN demand.purchase_requisitions pr ON pr.TraceId = dt.TraceId
                LEFT JOIN demand.quotations q ON q.TraceId = dt.TraceId
                LEFT JOIN demand.purchase_orders po ON po.TraceId = dt.TraceId
                LEFT JOIN warehouse.grns g ON g.TraceId = dt.TraceId
                LEFT JOIN material.material_issue_requests mir ON mir.TraceId = dt.TraceId
                LEFT JOIN finance.invoices inv ON inv.TraceId = dt.TraceId;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only backfill — no schema change to revert, and the prior (incorrect)
            // organization values aren't worth restoring.
        }
    }
}
