using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Pos.Migrations
{
    /// <inheritdoc />
    public partial class A38_PosInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pos");

            migrationBuilder.CreateTable(
                name: "customer_pos_settings",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllowPosCredit = table.Column<bool>(type: "bit", nullable: false),
                    OfflineCreditLimitOverride = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    MaxSingleCreditOrder = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    RequiresIdForCredit = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_pos_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "product_pos_settings",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PosName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PosCategoryId = table.Column<int>(type: "int", nullable: true),
                    Color = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: true),
                    ImageUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsWeighable = table.Column<bool>(type: "bit", nullable: false),
                    IsFavorite = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsPosActive = table.Column<bool>(type: "bit", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_pos_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "stores",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AddressLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AddressLine2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StateProvince = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Timezone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TaxInclusivePricing = table.Column<bool>(type: "bit", nullable: false),
                    DefaultTaxCodeUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultTaxCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DefaultTaxPercent = table.Column<decimal>(type: "decimal(9,4)", nullable: false),
                    ReceiptHeader = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReceiptFooter = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    OfflineCreditTolerancePct = table.Column<decimal>(type: "decimal(9,4)", nullable: false),
                    AutoCloseShiftHours = table.Column<int>(type: "int", nullable: false),
                    VarianceThreshold = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "number_sequences",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<int>(type: "int", nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    BusinessDate = table.Column<DateTime>(type: "date", nullable: false),
                    LastValue = table.Column<int>(type: "int", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_number_sequences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_number_sequences_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "pos",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "store_staff",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    PosRole = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_staff", x => x.Id);
                    table.ForeignKey(
                        name: "FK_store_staff_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "pos",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "terminals",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DeviceUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeviceBoundAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PinFailedAttempts = table.Column<int>(type: "int", nullable: false),
                    PinLockedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "binary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_terminals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_terminals_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "pos",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shifts",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<int>(type: "int", nullable: false),
                    TerminalId = table.Column<int>(type: "int", nullable: false),
                    ShiftNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    BusinessDate = table.Column<DateTime>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OpenedBy = table.Column<int>(type: "int", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosingStartedBy = table.Column<int>(type: "int", nullable: true),
                    ClosingStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedBy = table.Column<int>(type: "int", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReconciledBy = table.Column<int>(type: "int", nullable: true),
                    ReconciledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OpeningFloat = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalCashSales = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalCardSales = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalMobileSales = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalOtherSales = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalCreditSales = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalCashRefunds = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalOtherRefunds = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalCashIn = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalCashOut = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ExpectedCashClosing = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ActualCashClosing = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CashVariance = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    VarianceThreshold = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    VarianceNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReconcileNotes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    OrderCount = table.Column<int>(type: "int", nullable: false),
                    RefundCount = table.Column<int>(type: "int", nullable: false),
                    GrossTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    NetTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalsRecalculatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StaleAlertedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shifts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_shifts_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "pos",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shifts_terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalSchema: "pos",
                        principalTable: "terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cash_movements",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShiftId = table.Column<int>(type: "int", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PerformedBy = table.Column<int>(type: "int", nullable: false),
                    PerformedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LocalId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_movements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cash_movements_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalSchema: "pos",
                        principalTable: "shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cashier_sessions",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShiftId = table.Column<int>(type: "int", nullable: false),
                    TerminalId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LocalId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cashier_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cashier_sessions_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalSchema: "pos",
                        principalTable: "shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cashier_sessions_terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalSchema: "pos",
                        principalTable: "terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LocalId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OfflineNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    OrderType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StoreId = table.Column<int>(type: "int", nullable: false),
                    TerminalId = table.Column<int>(type: "int", nullable: false),
                    ShiftId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: true),
                    CashierUserId = table.Column<int>(type: "int", nullable: false),
                    CustomerUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CustomerName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    IsWalkIn = table.Column<bool>(type: "bit", nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DiscountType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DiscountValue = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TaxTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    GrandTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ChangeAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    BalanceDue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PaymentStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TaxInclusive = table.Column<bool>(type: "bit", nullable: false),
                    TaxCodeUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TaxCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TaxPercent = table.Column<decimal>(type: "decimal(9,4)", nullable: false),
                    OriginalOrderId = table.Column<int>(type: "int", nullable: true),
                    ReturnReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    VoidReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    VoidedBy = table.Column<int>(type: "int", nullable: true),
                    VoidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsOffline = table.Column<bool>(type: "bit", nullable: false),
                    Flags = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClientGrandTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    StockPosted = table.Column<bool>(type: "bit", nullable: false),
                    FinancePosted = table.Column<bool>(type: "bit", nullable: false),
                    PostingError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PostingAttempts = table.Column<int>(type: "int", nullable: false),
                    LastPostingAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SalesInvoiceUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SalesInvoiceNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreditNoteUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreditNoteNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    BusinessDate = table.Column<DateTime>(type: "date", nullable: false),
                    CreatedAtLocal = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_orders_cashier_sessions_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "pos",
                        principalTable: "cashier_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orders_orders_OriginalOrderId",
                        column: x => x.OriginalOrderId,
                        principalSchema: "pos",
                        principalTable: "orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orders_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalSchema: "pos",
                        principalTable: "shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orders_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "pos",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orders_terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalSchema: "pos",
                        principalTable: "terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    LocalLineId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    VariantName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Sku = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Barcode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CatalogPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    DiscountType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DiscountValue = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    NetAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TaxPercent = table.Column<decimal>(type: "decimal(9,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    OriginalLineId = table.Column<int>(type: "int", nullable: true),
                    ReturnedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_lines_order_lines_OriginalLineId",
                        column: x => x.OriginalLineId,
                        principalSchema: "pos",
                        principalTable: "order_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_lines_orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "pos",
                        principalTable: "orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "pos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    ShiftId = table.Column<int>(type: "int", nullable: false),
                    LocalId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Method = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ChangeGiven = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsRefund = table.Column<bool>(type: "bit", nullable: false),
                    CustomerPaymentUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payments_orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "pos",
                        principalTable: "orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_payments_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalSchema: "pos",
                        principalTable: "shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cash_movements_OrganizationId_LocalId",
                schema: "pos",
                table: "cash_movements",
                columns: new[] { "OrganizationId", "LocalId" },
                unique: true,
                filter: "[LocalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_cash_movements_ShiftId",
                schema: "pos",
                table: "cash_movements",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_cash_movements_Uuid",
                schema: "pos",
                table: "cash_movements",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cashier_sessions_OrganizationId_LocalId",
                schema: "pos",
                table: "cashier_sessions",
                columns: new[] { "OrganizationId", "LocalId" },
                unique: true,
                filter: "[LocalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_cashier_sessions_ShiftId",
                schema: "pos",
                table: "cashier_sessions",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_cashier_sessions_TerminalId_IsActive",
                schema: "pos",
                table: "cashier_sessions",
                columns: new[] { "TerminalId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_cashier_sessions_Uuid",
                schema: "pos",
                table: "cashier_sessions",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_pos_settings_OrganizationId_CustomerUuid",
                schema: "pos",
                table: "customer_pos_settings",
                columns: new[] { "OrganizationId", "CustomerUuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_pos_settings_OrganizationId_ModifiedAt",
                schema: "pos",
                table: "customer_pos_settings",
                columns: new[] { "OrganizationId", "ModifiedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_number_sequences_OrganizationId_StoreId_Prefix_BusinessDate",
                schema: "pos",
                table: "number_sequences",
                columns: new[] { "OrganizationId", "StoreId", "Prefix", "BusinessDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_number_sequences_StoreId",
                schema: "pos",
                table: "number_sequences",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_OrderId_LineNumber",
                schema: "pos",
                table: "order_lines",
                columns: new[] { "OrderId", "LineNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_OrganizationId_VariantUuid",
                schema: "pos",
                table: "order_lines",
                columns: new[] { "OrganizationId", "VariantUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_OriginalLineId",
                schema: "pos",
                table: "order_lines",
                column: "OriginalLineId");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_Uuid",
                schema: "pos",
                table: "order_lines",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_OrganizationId_CustomerUuid",
                schema: "pos",
                table: "orders",
                columns: new[] { "OrganizationId", "CustomerUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_orders_OrganizationId_LocalId",
                schema: "pos",
                table: "orders",
                columns: new[] { "OrganizationId", "LocalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_OrganizationId_OrderNumber",
                schema: "pos",
                table: "orders",
                columns: new[] { "OrganizationId", "OrderNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_OrganizationId_StoreId_CreatedAt",
                schema: "pos",
                table: "orders",
                columns: new[] { "OrganizationId", "StoreId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_orders_OriginalOrderId",
                schema: "pos",
                table: "orders",
                column: "OriginalOrderId",
                filter: "[OriginalOrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_orders_PostingPending",
                schema: "pos",
                table: "orders",
                columns: new[] { "StockPosted", "FinancePosted" });

            migrationBuilder.CreateIndex(
                name: "IX_orders_SessionId",
                schema: "pos",
                table: "orders",
                column: "SessionId",
                filter: "[SessionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_orders_ShiftId",
                schema: "pos",
                table: "orders",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_orders_StoreId",
                schema: "pos",
                table: "orders",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_orders_TerminalId",
                schema: "pos",
                table: "orders",
                column: "TerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_orders_Uuid",
                schema: "pos",
                table: "orders",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_OrderId",
                schema: "pos",
                table: "payments",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_payments_OrganizationId_LocalId",
                schema: "pos",
                table: "payments",
                columns: new[] { "OrganizationId", "LocalId" },
                unique: true,
                filter: "[LocalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_payments_ShiftId_Method",
                schema: "pos",
                table: "payments",
                columns: new[] { "ShiftId", "Method" });

            migrationBuilder.CreateIndex(
                name: "IX_payments_Uuid",
                schema: "pos",
                table: "payments",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_pos_settings_OrganizationId_ModifiedAt",
                schema: "pos",
                table: "product_pos_settings",
                columns: new[] { "OrganizationId", "ModifiedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_product_pos_settings_OrganizationId_ProductUuid",
                schema: "pos",
                table: "product_pos_settings",
                columns: new[] { "OrganizationId", "ProductUuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shifts_OrganizationId_ShiftNumber",
                schema: "pos",
                table: "shifts",
                columns: new[] { "OrganizationId", "ShiftNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shifts_OrganizationId_Status",
                schema: "pos",
                table: "shifts",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_shifts_OrganizationId_StoreId_OpenedAt",
                schema: "pos",
                table: "shifts",
                columns: new[] { "OrganizationId", "StoreId", "OpenedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_shifts_StoreId",
                schema: "pos",
                table: "shifts",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_shifts_Uuid",
                schema: "pos",
                table: "shifts",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_shifts_TerminalId_Active",
                schema: "pos",
                table: "shifts",
                column: "TerminalId",
                unique: true,
                filter: "[Status] IN ('OPEN','CLOSING')");

            migrationBuilder.CreateIndex(
                name: "IX_store_staff_OrganizationId_StoreId_UserId",
                schema: "pos",
                table: "store_staff",
                columns: new[] { "OrganizationId", "StoreId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_store_staff_OrganizationId_UserId",
                schema: "pos",
                table: "store_staff",
                columns: new[] { "OrganizationId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_store_staff_StoreId",
                schema: "pos",
                table: "store_staff",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_stores_OrganizationId_Code",
                schema: "pos",
                table: "stores",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stores_OrganizationId_WarehouseUuid",
                schema: "pos",
                table: "stores",
                columns: new[] { "OrganizationId", "WarehouseUuid" },
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_stores_Uuid",
                schema: "pos",
                table: "stores",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_terminals_OrganizationId_DeviceUuid",
                schema: "pos",
                table: "terminals",
                columns: new[] { "OrganizationId", "DeviceUuid" },
                unique: true,
                filter: "[DeviceUuid] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_terminals_OrganizationId_StoreId_Code",
                schema: "pos",
                table: "terminals",
                columns: new[] { "OrganizationId", "StoreId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_terminals_StoreId",
                schema: "pos",
                table: "terminals",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_terminals_Uuid",
                schema: "pos",
                table: "terminals",
                column: "Uuid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cash_movements",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "customer_pos_settings",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "number_sequences",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "order_lines",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "product_pos_settings",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "store_staff",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "cashier_sessions",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "shifts",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "terminals",
                schema: "pos");

            migrationBuilder.DropTable(
                name: "stores",
                schema: "pos");
        }
    }
}
