# Procure-to-stock gap analysis: SMS vs SAP MM and Odoo (2026-10-18)

Scope: Inventory, Purchase Requisition (PR), Quotation/RFQ, Purchase Order (PO), GRN (+ invoice match). Source: three read-only code reviews; SAP/Odoo behaviour is from general product knowledge, not the repo. Priorities: P0 = blocks a correct procure-to-pay cycle / data-integrity risk, P1 = expected by buyers/auditors, P2 = nice to have.

## 0. Pending from Addendums 45/46
- Live Playwright pass not done (API hung at startup on the shared Azure DB; restart needs the user's go-ahead).
- GRN `UnitCost` and invoice `UnitPrice`/`LineTotal` still 2dp (PO is 4dp) -> mismatch on 4dp pricelist prices.
- Server does not re-price when PO order date changes (UI banner only); PATCH PO does not return the resolution; RFQ create has no supplier field; no exchange-rate feed job; reorder-point auto-PO, CSV import and partial-GR split paths do not exist.

## 1. Defects to fix first
1. GRN approval adds rejected qty to PO `QtyReceived` (`GrnStatusHandler.cs:130`) - a rejection can complete the PO line.
2. Single-supplier PR convert loops over already-converted lines (`PurchaseOrderRepository.cs:126-130`) - duplicate PO lines on 2nd conversion.
3. No `RequirePermission` on `RequisitionsController`, and on RFQ `GetAccessLinks`/`ResendLink`/`UpdateQuotation`.
4. `OpenBidsAsync` never checks the RFQ due date (message claims it does).
5. Invoice with match Variance is approvable with no reason/role and no payment block; tolerance hard-coded 5%.
6. Stock adjustment picks the first inventory row for variant+warehouse (ignores batch/serial/bin) and a decrease can go below zero (`InventoryRepository.cs:1905-1926`).
7. Hard-codes: GRN over-receipt 3%, adjustment thresholds (qty>100 / value>50,000), 30-day invoice due date, fallback currency "PKR", GRN finance threshold 500,000 PKR.

## 2. P0 feature gaps
| Area | Gap | SAP / Odoo |
|---|---|---|
| Inventory | No costing engine honouring `ValuationMethod` (field unused; item cost = last GRN cost; WAC only in Finance ledger); no FIFO layers/standard cost/as-of-date valuation | SAP price control S/V + MBEW; Odoo Standard/AVCO/FIFO layers |
| Inventory | No cycle count / physical inventory (`LastCountDate` unused) | MI01/MI04/MI07; Odoo Physical Inventory |
| Inventory | No stock-transfer document / in-transit stage / UI (only via logistics delivery) | SAP 351/101 STO; Odoo internal transfers |
| Inventory | Expiry not enforced (no block/warn, no alerts/report, `ShelfLifeDays` unused); no quarantine/blocked status | SAP shelf-life check & stock types; Odoo lot alert dates |
| PR | No per-line RFQ award; award->PO only via PR line and only price written back | ME47/ME49 per-item; Odoo pick lines across vendors |
| PR | No auto-PR from reorder/MRP/MIR shortage (alerts only; `QtyOnOrder` never maintained) | MRP (MD01); Odoo orderpoints |
| PR/PO | No blanket/framework agreements (flag off, `PriceSource.BlanketOrder` reserved) | Contracts/scheduling agreements; Odoo blanket orders |
| PO | Approved PO cannot be amended/versioned, cancelled or short-closed (cancel has no endpoint) | ME22N + versions; Odoo lock/unlock, cancel |
| GRN | No reversal of an approved GRN | MIGO 102; Odoo return |
| GRN/Finance | GRN posts nothing to GL: no GR/IR accrual, clearing or price-variance posting | 101 Dr Inventory/Cr GR-IR, MR11; Odoo stock input/valuation |
| Match | Match tolerances not configurable (price vs qty keys), no payment block | MIRO tolerance keys; Odoo bill control policy |

## 3. P1 gaps
- **PR:** budget/commitment control (BudgetCode is text); PR cancel/close and qty-level conversion tracking; multi-PR consolidation; header-only approval (no per-line release / re-release on change).
- **RFQ:** no currency, incoterms/payment terms or bid-validity on RFQ/bid; one-time supplier link = no revisions/BAFO; comparison by total/lead-time only (no weighted score, no scorecard, no currency normalisation); no source list/quota; awarded prices don't feed pricelists; no RFQ expiry state/reminders.
- **PO:** no payment terms/incoterms (invoice due date hard-coded 30d); no tax/discount/freight on PO (`LineDiscountPct` reference only); single date per line (no schedule lines); PO email goes to PO creator not supplier, no supplier acknowledgement; no per-line tolerance; drop-ship GRN still needs a warehouse.
- **GRN/costing:** landed cost missing; bin captured but unused by putaway; one lot/serial per line, no lot master; rejected goods not posted to blocked stock; no receipt without PO; no inspection plans/sampling; cost = latest price; price variance to inventory missing.
- **Returns/AP:** SRO not pre-filled from GRN; debit note doesn't reduce `QtyInvoiced`; invoice auto-created only (no bill-from-PO proposing uninvoiced qty confirmed).
- **Inventory:** UoM entity + conversions (free-text UoM); putaway rules & configurable removal strategy (expiry-first hard-coded); negative-stock policy switch; stock reports (aging, slow/dead, ABC, as-of-date, valuation by product); master-data audit (no audit calls in Inventory); approvals not on workflow engine; ledger has no reversal/period close; bin-level stock identity & capacity.

## 4. P2
Purchasing group/cost-centre on PR; EXPIRED/CLOSED RFQ states and automated reminders; vendor evaluation shown in comparison; variant matrix & packaging/multiple barcodes; recursive categories with costing policy; standalone scrap; kitting; label printing/scanner flows; consignment/subcontracting/VMI; inter-company transfer.

## 5. Where SMS is ahead
Supplier portal for RFQ/SRO (email + WhatsApp links, sealed bids, attachments); multi-currency PO with rate lock (A35); pricelist auto-pricing with FX (A45/A46); source-agnostic reservations + allocation engine; POS stack.

## 6. Suggested sequence
1. Fix the section-1 defects (small, high value, includes security).
2. Accounting correctness: GR/IR on GRN approval + clearing at invoice, configurable match tolerance + payment block, GRN reversal, 4dp on GRN/invoice.
3. PO lifecycle: cancel/close/short-close, amendments with re-approval, payment terms/incoterms, tax/discount/charges.
4. Sourcing: per-line award + direct award->PO, RFQ currency/terms/validity, bid revisions, auto-PR from reorder, blanket agreements.
5. Inventory core: costing engine, cycle count, transfer document with in-transit, expiry control/quarantine, batch-aware adjustments, UoM.
6. P1 remainder (landed cost, putaway, reports, audit).
