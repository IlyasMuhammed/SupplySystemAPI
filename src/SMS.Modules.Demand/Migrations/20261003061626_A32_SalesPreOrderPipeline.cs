using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class A32_SalesPreOrderPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerPoAttachmentUuid",
                schema: "demand",
                table: "sale_orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CustomerPoDate",
                schema: "demand",
                table: "sale_orders",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerPoReference",
                schema: "demand",
                table: "sale_orders",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceInquiryId",
                schema: "demand",
                table: "sale_orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceQuotationId",
                schema: "demand",
                table: "sale_orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                schema: "demand",
                table: "sale_orders",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "MANUAL");

            migrationBuilder.CreateTable(
                name: "rejection_reasons",
                schema: "demand",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    CreatedBy = table.Column<int>(type: "int", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rejection_reasons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sale_inquiries",
                schema: "demand",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InquiryNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PartnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerReference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CustomerReferenceDate = table.Column<DateTime>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReceivedDate = table.Column<DateTime>(type: "date", nullable: false),
                    ResponseDeadline = table.Column<DateTime>(type: "date", nullable: true),
                    AssignedToUserId = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DeclineReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_inquiries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sale_inquiry_lines",
                schema: "demand",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleInquiryId = table.Column<int>(type: "int", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    ProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RequestedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RequestedUomCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    RequestedDeliveryDate = table.Column<DateTime>(type: "date", nullable: true),
                    LineStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CanSupplyQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    EstimatedDeliveryDate = table.Column<DateTime>(type: "date", nullable: true),
                    RejectionReasonId = table.Column<int>(type: "int", nullable: true),
                    RejectionNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AlternativeProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AlternativeVariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AlternativeNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RequiresProcurement = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ProcurementLeadDays = table.Column<int>(type: "int", nullable: true),
                    ReviewedByUserId = table.Column<int>(type: "int", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_inquiry_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sale_inquiry_lines_rejection_reasons_RejectionReasonId",
                        column: x => x.RejectionReasonId,
                        principalSchema: "demand",
                        principalTable: "rejection_reasons",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_sale_inquiry_lines_sale_inquiries_SaleInquiryId",
                        column: x => x.SaleInquiryId,
                        principalSchema: "demand",
                        principalTable: "sale_inquiries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sale_quotations",
                schema: "demand",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuotationNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PartnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerReference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CustomerReferenceDate = table.Column<DateTime>(type: "date", nullable: true),
                    SourceInquiryId = table.Column<int>(type: "int", nullable: true),
                    CurrencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "date", nullable: false),
                    ValidTo = table.Column<DateTime>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PaymentTerms = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DeliveryTerms = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Subtotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    GrandTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    InternalNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentByUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_quotations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sale_quotations_sale_inquiries_SourceInquiryId",
                        column: x => x.SourceInquiryId,
                        principalSchema: "demand",
                        principalTable: "sale_inquiries",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "sale_quotation_lines",
                schema: "demand",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleQuotationId = table.Column<int>(type: "int", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    SourceInquiryLineId = table.Column<int>(type: "int", nullable: true),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    DiscountPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    TaxPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    TaxCodeUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TaxCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PromisedDeliveryDate = table.Column<DateTime>(type: "date", nullable: true),
                    LineType = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    RejectionReasonId = table.Column<int>(type: "int", nullable: true),
                    RejectionNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AlternativeForLineId = table.Column<int>(type: "int", nullable: true),
                    AlternativeNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CustomerResponse = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    CustomerResponseDate = table.Column<DateTime>(type: "date", nullable: true),
                    CustomerResponseNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CustomerCounterPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_quotation_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sale_quotation_lines_rejection_reasons_RejectionReasonId",
                        column: x => x.RejectionReasonId,
                        principalSchema: "demand",
                        principalTable: "rejection_reasons",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_sale_quotation_lines_sale_inquiry_lines_SourceInquiryLineId",
                        column: x => x.SourceInquiryLineId,
                        principalSchema: "demand",
                        principalTable: "sale_inquiry_lines",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_sale_quotation_lines_sale_quotation_lines_AlternativeForLineId",
                        column: x => x.AlternativeForLineId,
                        principalSchema: "demand",
                        principalTable: "sale_quotation_lines",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_sale_quotation_lines_sale_quotations_SaleQuotationId",
                        column: x => x.SaleQuotationId,
                        principalSchema: "demand",
                        principalTable: "sale_quotations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sale_orders_OrganizationId_CustomerPoReference",
                schema: "demand",
                table: "sale_orders",
                columns: new[] { "OrganizationId", "CustomerPoReference" },
                filter: "[CustomerPoReference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sale_orders_SourceInquiryId",
                schema: "demand",
                table: "sale_orders",
                column: "SourceInquiryId",
                filter: "[SourceInquiryId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sale_orders_SourceQuotationId",
                schema: "demand",
                table: "sale_orders",
                column: "SourceQuotationId",
                unique: true,
                filter: "[SourceQuotationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_rejection_reasons_OrganizationId_Code",
                schema: "demand",
                table: "rejection_reasons",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rejection_reasons_UUID",
                schema: "demand",
                table: "rejection_reasons",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_inquiries_AssignedToUserId",
                schema: "demand",
                table: "sale_inquiries",
                column: "AssignedToUserId",
                filter: "[AssignedToUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sale_inquiries_OrganizationId_InquiryNumber",
                schema: "demand",
                table: "sale_inquiries",
                columns: new[] { "OrganizationId", "InquiryNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_inquiries_OrganizationId_Status",
                schema: "demand",
                table: "sale_inquiries",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sale_inquiries_PartnerId",
                schema: "demand",
                table: "sale_inquiries",
                column: "PartnerId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_inquiries_TraceId",
                schema: "demand",
                table: "sale_inquiries",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_inquiries_UUID",
                schema: "demand",
                table: "sale_inquiries",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_inquiry_lines_OrganizationId",
                schema: "demand",
                table: "sale_inquiry_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_inquiry_lines_ProductUuid",
                schema: "demand",
                table: "sale_inquiry_lines",
                column: "ProductUuid",
                filter: "[ProductUuid] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sale_inquiry_lines_RejectionReasonId",
                schema: "demand",
                table: "sale_inquiry_lines",
                column: "RejectionReasonId",
                filter: "[RejectionReasonId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sale_inquiry_lines_SaleInquiryId_LineNumber",
                schema: "demand",
                table: "sale_inquiry_lines",
                columns: new[] { "SaleInquiryId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_inquiry_lines_UUID",
                schema: "demand",
                table: "sale_inquiry_lines",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotation_lines_AlternativeForLineId",
                schema: "demand",
                table: "sale_quotation_lines",
                column: "AlternativeForLineId",
                filter: "[AlternativeForLineId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotation_lines_OrganizationId",
                schema: "demand",
                table: "sale_quotation_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotation_lines_RejectionReasonId",
                schema: "demand",
                table: "sale_quotation_lines",
                column: "RejectionReasonId",
                filter: "[RejectionReasonId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotation_lines_SaleQuotationId_CustomerResponse",
                schema: "demand",
                table: "sale_quotation_lines",
                columns: new[] { "SaleQuotationId", "CustomerResponse" });

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotation_lines_SaleQuotationId_LineNumber",
                schema: "demand",
                table: "sale_quotation_lines",
                columns: new[] { "SaleQuotationId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotation_lines_SourceInquiryLineId",
                schema: "demand",
                table: "sale_quotation_lines",
                column: "SourceInquiryLineId",
                filter: "[SourceInquiryLineId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotation_lines_UUID",
                schema: "demand",
                table: "sale_quotation_lines",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotation_lines_VariantUuid",
                schema: "demand",
                table: "sale_quotation_lines",
                column: "VariantUuid");

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotations_OrganizationId_QuotationNumber",
                schema: "demand",
                table: "sale_quotations",
                columns: new[] { "OrganizationId", "QuotationNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotations_OrganizationId_Status",
                schema: "demand",
                table: "sale_quotations",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotations_OrganizationId_ValidTo",
                schema: "demand",
                table: "sale_quotations",
                columns: new[] { "OrganizationId", "ValidTo" });

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotations_PartnerId",
                schema: "demand",
                table: "sale_quotations",
                column: "PartnerId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotations_SourceInquiryId",
                schema: "demand",
                table: "sale_quotations",
                column: "SourceInquiryId",
                filter: "[SourceInquiryId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotations_TraceId",
                schema: "demand",
                table: "sale_quotations",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_quotations_UUID",
                schema: "demand",
                table: "sale_quotations",
                column: "UUID",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_sale_orders_sale_inquiries_SourceInquiryId",
                schema: "demand",
                table: "sale_orders",
                column: "SourceInquiryId",
                principalSchema: "demand",
                principalTable: "sale_inquiries",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_sale_orders_sale_quotations_SourceQuotationId",
                schema: "demand",
                table: "sale_orders",
                column: "SourceQuotationId",
                principalSchema: "demand",
                principalTable: "sale_quotations",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sale_orders_sale_inquiries_SourceInquiryId",
                schema: "demand",
                table: "sale_orders");

            migrationBuilder.DropForeignKey(
                name: "FK_sale_orders_sale_quotations_SourceQuotationId",
                schema: "demand",
                table: "sale_orders");

            migrationBuilder.DropTable(
                name: "sale_quotation_lines",
                schema: "demand");

            migrationBuilder.DropTable(
                name: "sale_inquiry_lines",
                schema: "demand");

            migrationBuilder.DropTable(
                name: "sale_quotations",
                schema: "demand");

            migrationBuilder.DropTable(
                name: "rejection_reasons",
                schema: "demand");

            migrationBuilder.DropTable(
                name: "sale_inquiries",
                schema: "demand");

            migrationBuilder.DropIndex(
                name: "IX_sale_orders_OrganizationId_CustomerPoReference",
                schema: "demand",
                table: "sale_orders");

            migrationBuilder.DropIndex(
                name: "IX_sale_orders_SourceInquiryId",
                schema: "demand",
                table: "sale_orders");

            migrationBuilder.DropIndex(
                name: "IX_sale_orders_SourceQuotationId",
                schema: "demand",
                table: "sale_orders");

            migrationBuilder.DropColumn(
                name: "CustomerPoAttachmentUuid",
                schema: "demand",
                table: "sale_orders");

            migrationBuilder.DropColumn(
                name: "CustomerPoDate",
                schema: "demand",
                table: "sale_orders");

            migrationBuilder.DropColumn(
                name: "CustomerPoReference",
                schema: "demand",
                table: "sale_orders");

            migrationBuilder.DropColumn(
                name: "SourceInquiryId",
                schema: "demand",
                table: "sale_orders");

            migrationBuilder.DropColumn(
                name: "SourceQuotationId",
                schema: "demand",
                table: "sale_orders");

            migrationBuilder.DropColumn(
                name: "SourceType",
                schema: "demand",
                table: "sale_orders");
        }
    }
}
