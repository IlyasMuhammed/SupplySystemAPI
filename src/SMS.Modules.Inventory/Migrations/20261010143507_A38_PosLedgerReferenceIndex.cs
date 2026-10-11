using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class A38_PosLedgerReferenceIndex : Migration
    {
        // A38 (INV, D-24) — POS stock postings are idempotent per (SourceUuid, TransactionType), checked against the ledger's
        // ReferenceId; this index keeps that check a seek. Additive and guarded: every API start replays migrations on the
        // drifted shared database.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_InventoryLedgerEntries_ReferenceId_TransactionType'
                               AND object_id = OBJECT_ID('inventory.InventoryLedgerEntries'))
                    CREATE INDEX [IX_InventoryLedgerEntries_ReferenceId_TransactionType]
                        ON [inventory].[InventoryLedgerEntries] ([ReferenceId], [TransactionType]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_InventoryLedgerEntries_ReferenceId_TransactionType'
                           AND object_id = OBJECT_ID('inventory.InventoryLedgerEntries'))
                    DROP INDEX [IX_InventoryLedgerEntries_ReferenceId_TransactionType] ON [inventory].[InventoryLedgerEntries];
                """);
        }
    }
}
