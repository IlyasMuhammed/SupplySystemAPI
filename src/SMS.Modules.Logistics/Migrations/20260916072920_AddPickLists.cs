using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddPickLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pick_lists",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryOrderId = table.Column<int>(type: "int", nullable: false),
                    PickListNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AssignedToUserId = table.Column<int>(type: "int", nullable: true),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pick_lists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pick_lists_delivery_orders_DeliveryOrderId",
                        column: x => x.DeliveryOrderId,
                        principalSchema: "logistics",
                        principalTable: "delivery_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pick_list_lines",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PickListId = table.Column<int>(type: "int", nullable: false),
                    DeliveryOrderLineId = table.Column<int>(type: "int", nullable: false),
                    SeqNo = table.Column<int>(type: "int", nullable: false),
                    ReservationUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ItemDescription = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    UnitOfMeasure = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ZoneName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BinCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    BatchNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SerialNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExpiryDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    QtyToPick = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    QtyPicked = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    QtyShort = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    ShortReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    PickedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PickedBy = table.Column<int>(type: "int", nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pick_list_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pick_list_lines_delivery_order_lines_DeliveryOrderLineId",
                        column: x => x.DeliveryOrderLineId,
                        principalSchema: "logistics",
                        principalTable: "delivery_order_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pick_list_lines_pick_lists_PickListId",
                        column: x => x.PickListId,
                        principalSchema: "logistics",
                        principalTable: "pick_lists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pick_list_lines_DeliveryOrderLineId",
                schema: "logistics",
                table: "pick_list_lines",
                column: "DeliveryOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_pick_list_lines_PickListId_ReservationUuid",
                schema: "logistics",
                table: "pick_list_lines",
                columns: new[] { "PickListId", "ReservationUuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pick_list_lines_PickListId_SeqNo",
                schema: "logistics",
                table: "pick_list_lines",
                columns: new[] { "PickListId", "SeqNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pick_list_lines_UUID",
                schema: "logistics",
                table: "pick_list_lines",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pick_lists_DeliveryOrderId",
                schema: "logistics",
                table: "pick_lists",
                column: "DeliveryOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_pick_lists_OrganizationId_PickListNumber",
                schema: "logistics",
                table: "pick_lists",
                columns: new[] { "OrganizationId", "PickListNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pick_lists_OrganizationId_WarehouseUuid_Status",
                schema: "logistics",
                table: "pick_lists",
                columns: new[] { "OrganizationId", "WarehouseUuid", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_pick_lists_UUID",
                schema: "logistics",
                table: "pick_lists",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pick_list_lines",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "pick_lists",
                schema: "logistics");
        }
    }
}
