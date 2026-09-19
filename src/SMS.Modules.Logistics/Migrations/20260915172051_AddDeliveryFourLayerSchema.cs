using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryFourLayerSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "addresses",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Line1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Line2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CityName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CountryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CountryName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CountryIsoCode = table.Column<string>(type: "nchar(2)", fixedLength: true, maxLength: 2, nullable: true),
                    ContactName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ContactPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ContactPhoneE164 = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", nullable: true),
                    AddressType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ConsigneeUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ValidationStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ValidationNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_addresses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "consignments",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsignmentNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CarrierId = table.Column<int>(type: "int", nullable: true),
                    CarrierName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CarrierServiceCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MasterAwb = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CarrierReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FreightTerms = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CodAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CodCurrency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    PickupWindowStart = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PickupWindowEnd = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Etd = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Eta = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActualDispatchAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActualArrivalAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VehicleNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    DriverName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DriverPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ShipFromAddressId = table.Column<int>(type: "int", nullable: true),
                    ShipToAddressId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    BookingIdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BookingFailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_consignments_addresses_ShipFromAddressId",
                        column: x => x.ShipFromAddressId,
                        principalSchema: "logistics",
                        principalTable: "addresses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_consignments_addresses_ShipToAddressId",
                        column: x => x.ShipToAddressId,
                        principalSchema: "logistics",
                        principalTable: "addresses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_consignments_carriers_CarrierId",
                        column: x => x.CarrierId,
                        principalSchema: "logistics",
                        principalTable: "carriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "delivery_orders",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ShipFromAddressId = table.Column<int>(type: "int", nullable: true),
                    ShipToAddressId = table.Column<int>(type: "int", nullable: true),
                    RequestedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PromisedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Priority = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Incoterm = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StatusBeforeHold = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    HoldReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HeldAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HeldBy = table.Column<int>(type: "int", nullable: true),
                    LinesUnknown = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_orders_addresses_ShipFromAddressId",
                        column: x => x.ShipFromAddressId,
                        principalSchema: "logistics",
                        principalTable: "addresses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_orders_addresses_ShipToAddressId",
                        column: x => x.ShipToAddressId,
                        principalSchema: "logistics",
                        principalTable: "addresses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "consignment_stops",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsignmentId = table.Column<int>(type: "int", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    AddressId = table.Column<int>(type: "int", nullable: true),
                    StopType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PlannedArrival = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActualArrival = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consignment_stops", x => x.Id);
                    table.ForeignKey(
                        name: "FK_consignment_stops_addresses_AddressId",
                        column: x => x.AddressId,
                        principalSchema: "logistics",
                        principalTable: "addresses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_consignment_stops_consignments_ConsignmentId",
                        column: x => x.ConsignmentId,
                        principalSchema: "logistics",
                        principalTable: "consignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "delivery_order_lines",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryOrderId = table.Column<int>(type: "int", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ItemDescription = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    UnitOfMeasure = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    QtyOrdered = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    QtyPicked = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    QtyPacked = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    QtyShipped = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    QtyDelivered = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    QtyShort = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    ShortReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    BatchNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SerialNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourceLineUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BinUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UnitValue = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    IsHazardous = table.Column<bool>(type: "bit", nullable: false),
                    IsFragile = table.Column<bool>(type: "bit", nullable: false),
                    IsTemperatureControlled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_order_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_order_lines_delivery_orders_DeliveryOrderId",
                        column: x => x.DeliveryOrderId,
                        principalSchema: "logistics",
                        principalTable: "delivery_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shipment_packages",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryOrderId = table.Column<int>(type: "int", nullable: false),
                    PackageBarcode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PackageType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LengthCm = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    WidthCm = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    HeightCm = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    GrossWeightKg = table.Column<decimal>(type: "decimal(18,3)", nullable: true),
                    NetWeightKg = table.Column<decimal>(type: "decimal(18,3)", nullable: true),
                    DimWeightKg = table.Column<decimal>(type: "decimal(18,3)", nullable: true),
                    DeclaredValue = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    SealNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ParentPackageId = table.Column<int>(type: "int", nullable: true),
                    IsVoided = table.Column<bool>(type: "bit", nullable: false),
                    VoidReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipment_packages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_shipment_packages_delivery_orders_DeliveryOrderId",
                        column: x => x.DeliveryOrderId,
                        principalSchema: "logistics",
                        principalTable: "delivery_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_shipment_packages_shipment_packages_ParentPackageId",
                        column: x => x.ParentPackageId,
                        principalSchema: "logistics",
                        principalTable: "shipment_packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "consignment_deliveries",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsignmentId = table.Column<int>(type: "int", nullable: false),
                    DeliveryOrderId = table.Column<int>(type: "int", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    ConsignmentStopId = table.Column<int>(type: "int", nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consignment_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_consignment_deliveries_consignment_stops_ConsignmentStopId",
                        column: x => x.ConsignmentStopId,
                        principalSchema: "logistics",
                        principalTable: "consignment_stops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_consignment_deliveries_consignments_ConsignmentId",
                        column: x => x.ConsignmentId,
                        principalSchema: "logistics",
                        principalTable: "consignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_consignment_deliveries_delivery_orders_DeliveryOrderId",
                        column: x => x.DeliveryOrderId,
                        principalSchema: "logistics",
                        principalTable: "delivery_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "package_contents",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShipmentPackageId = table.Column<int>(type: "int", nullable: false),
                    DeliveryOrderLineId = table.Column<int>(type: "int", nullable: false),
                    Qty = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    BatchNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SerialNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_package_contents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_package_contents_delivery_order_lines_DeliveryOrderLineId",
                        column: x => x.DeliveryOrderLineId,
                        principalSchema: "logistics",
                        principalTable: "delivery_order_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_package_contents_shipment_packages_ShipmentPackageId",
                        column: x => x.ShipmentPackageId,
                        principalSchema: "logistics",
                        principalTable: "shipment_packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_addresses_OrganizationId_CityId",
                schema: "logistics",
                table: "addresses",
                columns: new[] { "OrganizationId", "CityId" });

            migrationBuilder.CreateIndex(
                name: "IX_addresses_OrganizationId_ValidationStatus",
                schema: "logistics",
                table: "addresses",
                columns: new[] { "OrganizationId", "ValidationStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_addresses_UUID",
                schema: "logistics",
                table: "addresses",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consignment_deliveries_ConsignmentId_DeliveryOrderId",
                schema: "logistics",
                table: "consignment_deliveries",
                columns: new[] { "ConsignmentId", "DeliveryOrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consignment_deliveries_ConsignmentStopId",
                schema: "logistics",
                table: "consignment_deliveries",
                column: "ConsignmentStopId");

            migrationBuilder.CreateIndex(
                name: "IX_consignment_deliveries_DeliveryOrderId",
                schema: "logistics",
                table: "consignment_deliveries",
                column: "DeliveryOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_consignment_deliveries_UUID",
                schema: "logistics",
                table: "consignment_deliveries",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consignment_stops_AddressId",
                schema: "logistics",
                table: "consignment_stops",
                column: "AddressId");

            migrationBuilder.CreateIndex(
                name: "IX_consignment_stops_ConsignmentId_Sequence",
                schema: "logistics",
                table: "consignment_stops",
                columns: new[] { "ConsignmentId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consignment_stops_UUID",
                schema: "logistics",
                table: "consignment_stops",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consignments_CarrierId",
                schema: "logistics",
                table: "consignments",
                column: "CarrierId");

            migrationBuilder.CreateIndex(
                name: "IX_consignments_OrganizationId_BookingIdempotencyKey",
                schema: "logistics",
                table: "consignments",
                columns: new[] { "OrganizationId", "BookingIdempotencyKey" },
                unique: true,
                filter: "[BookingIdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_consignments_OrganizationId_ConsignmentNumber",
                schema: "logistics",
                table: "consignments",
                columns: new[] { "OrganizationId", "ConsignmentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consignments_OrganizationId_MasterAwb",
                schema: "logistics",
                table: "consignments",
                columns: new[] { "OrganizationId", "MasterAwb" });

            migrationBuilder.CreateIndex(
                name: "IX_consignments_OrganizationId_Status",
                schema: "logistics",
                table: "consignments",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_consignments_ShipFromAddressId",
                schema: "logistics",
                table: "consignments",
                column: "ShipFromAddressId");

            migrationBuilder.CreateIndex(
                name: "IX_consignments_ShipToAddressId",
                schema: "logistics",
                table: "consignments",
                column: "ShipToAddressId");

            migrationBuilder.CreateIndex(
                name: "IX_consignments_UUID",
                schema: "logistics",
                table: "consignments",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_order_lines_DeliveryOrderId_LineNo",
                schema: "logistics",
                table: "delivery_order_lines",
                columns: new[] { "DeliveryOrderId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_order_lines_OrganizationId_VariantUuid",
                schema: "logistics",
                table: "delivery_order_lines",
                columns: new[] { "OrganizationId", "VariantUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_order_lines_UUID",
                schema: "logistics",
                table: "delivery_order_lines",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_orders_OrganizationId_DeliveryNumber",
                schema: "logistics",
                table: "delivery_orders",
                columns: new[] { "OrganizationId", "DeliveryNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_orders_OrganizationId_SourceType_SourceUuid",
                schema: "logistics",
                table: "delivery_orders",
                columns: new[] { "OrganizationId", "SourceType", "SourceUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_orders_OrganizationId_Status",
                schema: "logistics",
                table: "delivery_orders",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_orders_ShipFromAddressId",
                schema: "logistics",
                table: "delivery_orders",
                column: "ShipFromAddressId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_orders_ShipToAddressId",
                schema: "logistics",
                table: "delivery_orders",
                column: "ShipToAddressId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_orders_UUID",
                schema: "logistics",
                table: "delivery_orders",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_package_contents_DeliveryOrderLineId",
                schema: "logistics",
                table: "package_contents",
                column: "DeliveryOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_package_contents_ShipmentPackageId",
                schema: "logistics",
                table: "package_contents",
                column: "ShipmentPackageId");

            migrationBuilder.CreateIndex(
                name: "IX_package_contents_UUID",
                schema: "logistics",
                table: "package_contents",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shipment_packages_DeliveryOrderId",
                schema: "logistics",
                table: "shipment_packages",
                column: "DeliveryOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_shipment_packages_OrganizationId_PackageBarcode",
                schema: "logistics",
                table: "shipment_packages",
                columns: new[] { "OrganizationId", "PackageBarcode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shipment_packages_ParentPackageId",
                schema: "logistics",
                table: "shipment_packages",
                column: "ParentPackageId");

            migrationBuilder.CreateIndex(
                name: "IX_shipment_packages_UUID",
                schema: "logistics",
                table: "shipment_packages",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consignment_deliveries",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "package_contents",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "consignment_stops",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "delivery_order_lines",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "shipment_packages",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "consignments",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "delivery_orders",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "addresses",
                schema: "logistics");
        }
    }
}
