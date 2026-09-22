import { SalesReportKey } from '../../../services/sales-reports.service';
import { formatCode } from '../../../shared/format-code';
import { Severity, LEDGER_ENTRY_SEVERITY } from '../../finance/receivables/receivables.shared';
import { SALE_ORDER_STATUS_SEVERITY } from '../../sales/sale-orders/sale-order-list/sale-order-list.component';
import { DELIVERY_STATUS_SEVERITY } from '../../logistics/deliveries/delivery-list/delivery-list.component';
import { ChartSpec, agingCharts, marginCharts, profitabilityCharts, salesVsPurchaseCharts } from './sales-report-charts';

// The ten sales reports (A29 §15), as data: what each is called, what it needs, what can be filtered, and which
// tables and charts make it up. The dashboard draws every one of them from this, so what a report needs from the
// user, and from the server, is written down in one place.

export type CellKind = 'text' | 'code' | 'tag' | 'int' | 'qty' | 'money' | 'cost' | 'percent' | 'date';

export interface ColumnDef {
  /** A function when the heading depends on the report, as R7's does on what it is grouped by. */
  header: string | ((report: any) => string);
  field: string;
  /** Text when left out. A code is shown as words; a tag as words with a colour from `severity`. */
  kind?: CellKind;
  severity?: Record<string, Severity>;
}

export interface SectionDef {
  id: string;
  title: string;
  rows: (report: any) => any[];
  columns: ColumnDef[];
  /** The report's own list, which the server pages. The other sections are whole. */
  paged?: boolean;
}

/**
 * What a filter is. Its `id` is the query parameter the server reads it as, and the key its value is held under:
 * a customer is `partnerId`, a product `productId`, and so on.
 */
export type FilterKind = 'dateFrom' | 'dateTo' | 'asOf' | 'customer' | 'select' | 'product' | 'variant' | 'warehouse';

export interface FilterDef {
  id: string;
  kind: FilterKind;
  label: string;
  options?: { label: string; value: string }[];
  /** For a select: what it starts as, when that is not "all". */
  defaultValue?: string;
  /** The report cannot be run without it. */
  required?: boolean;
}

export interface ReportDef {
  key: SalesReportKey;
  /** R1 to R10, as §15 numbers them. */
  code: string;
  title: string;
  description: string;
  /** What the figures mean and what they leave out, for whoever reads them. */
  note: string;
  icon: string;
  /** Every one of these is needed to see the report: the reports themselves, and the books it is made of. */
  viewPermissions: string[];
  /** Every one of these is needed to download it. */
  exportPermissions: string[];
  filters: FilterDef[];
  sections: SectionDef[];
  charts?: (report: any) => ChartSpec[];
}

// ── Shared pieces ────────────────────────────────────────────────────────────

const REPORT_VIEW = 'REPORT_VIEW';
const REPORT_EXPORT = 'REPORT_EXPORT';

/** The permissions behind a report: the reports themselves, and what the report is made of. */
const needs = (...books: string[]) => ({
  viewPermissions: [REPORT_VIEW, ...books],
  exportPermissions: [REPORT_EXPORT, ...books]
});

const dateFrom: FilterDef = { id: 'dateFrom', kind: 'dateFrom', label: 'From' };
const dateTo: FilterDef   = { id: 'dateTo',   kind: 'dateTo',   label: 'To' };
const customer: FilterDef = { id: 'partnerId', kind: 'customer', label: 'Customer' };

const all = { label: 'All', value: '' };

const SALE_ORDER_STATUSES = ['DRAFT', 'CONFIRMED', 'PARTIALLY_FULFILLED', 'FULFILLED', 'INVOICED', 'CLOSED', 'CANCELLED'];

/** Every delivery status that is not finished with: the ones R6 lists. */
export const OPEN_DELIVERY_STATUSES = [
  'DRAFT', 'RELEASED', 'PICKING', 'PICKED', 'PACKED', 'STAGED', 'PENDING_APPROVAL',
  'GOODS_ISSUED', 'IN_TRANSIT', 'PARTIALLY_DELIVERED', 'ON_HOLD'
];

const codeOptions = (codes: string[]) => [all, ...codes.map(c => ({ label: formatCode(c), value: c }))];

const DELIVERY_MODES = [
  { label: 'Ship to customer',  value: 'SHIP' },
  { label: 'Customer collects', value: 'SELF_PICKUP' }
];

const AGING_SEVERITY: Record<string, Severity> = { '0-30': 'success', '31-60': 'info', '61-90': 'warn', '90+': 'danger' };

const text = (header: ColumnDef['header'], field: string): ColumnDef => ({ header, field });
const code = (header: string, field: string): ColumnDef => ({ header, field, kind: 'code' });
const tag = (header: string, field: string, severity: Record<string, Severity>): ColumnDef => ({ header, field, kind: 'tag', severity });
const int = (header: string, field: string): ColumnDef => ({ header, field, kind: 'int' });
const qty = (header: string, field: string): ColumnDef => ({ header, field, kind: 'qty' });
const money = (header: string, field: string): ColumnDef => ({ header, field, kind: 'money' });
const cost = (header: string, field: string): ColumnDef => ({ header, field, kind: 'cost' });
const percent = (header: string, field: string): ColumnDef => ({ header, field, kind: 'percent' });
const date = (header: string, field: string): ColumnDef => ({ header, field, kind: 'date' });

const currency = text('Currency', 'currencyCode');

const agingBuckets: ColumnDef[] = [
  money('0-30', 'days0To30'), money('31-60', 'days31To60'), money('61-90', 'days61To90'), money('90+', 'over90'), money('Total', 'total')
];

// ── The ten ──────────────────────────────────────────────────────────────────

export const SALES_REPORTS: ReportDef[] = [
  {
    key: 'order-register', code: 'R1', title: 'Sales Order Register', icon: 'pi pi-shopping-bag',
    description: 'Every sale order, newest first, with what it comes to.',
    note: 'The totals cover every order the filters match, cancelled and draft ones included: filter by status for the figure you want. Each currency is kept apart, as there is no exchange rate to add them with.',
    ...needs('SALE_ORDER_VIEW'),
    filters: [
      dateFrom, dateTo,
      { id: 'status', kind: 'select', label: 'Status', options: codeOptions(SALE_ORDER_STATUSES) },
      customer,
      { id: 'deliveryMode', kind: 'select', label: 'Delivery', options: [all, ...DELIVERY_MODES] }
    ],
    sections: [
      {
        id: 'totals', title: 'Totals', rows: r => r.totals,
        columns: [currency, int('Orders', 'orderCount'), money('Subtotal', 'subtotal'), money('Discount', 'discountAmount'), money('Tax', 'taxAmount'), money('Total', 'grandTotal')]
      },
      {
        id: 'orders', title: 'Orders', paged: true, rows: r => r.items,
        columns: [
          text('Order #', 'soNumber'), date('Order date', 'orderDate'), text('Customer', 'customerName'),
          tag('Status', 'status', SALE_ORDER_STATUS_SEVERITY), code('Delivery', 'deliveryMode'), currency,
          int('Lines', 'lineCount'), money('Subtotal', 'subtotal'), money('Discount', 'discountAmount'),
          money('Tax', 'taxAmount'), money('Total', 'grandTotal')
        ]
      }
    ]
  },
  {
    key: 'customer-ledger', code: 'R2', title: 'Customer Ledger', icon: 'pi pi-book',
    description: "One customer's account: what they were billed, what they paid, and what they owe.",
    note: "A ledger is one customer's account, so a customer is required. The opening balance is what they owed when the range began; each line shows the balance after it, in its own currency.",
    ...needs('CUSTOMER_LEDGER_VIEW'),
    filters: [{ ...customer, required: true }, dateFrom, dateTo],
    sections: [
      {
        id: 'summary', title: 'Summary', rows: r => r.summaries,
        columns: [currency, money('Opening balance', 'openingBalance'), money('Debits', 'totalDebit'), money('Credits', 'totalCredit'), money('Closing balance', 'closingBalance'), int('Entries', 'entryCount')]
      },
      {
        id: 'entries', title: 'Entries', paged: true, rows: r => r.entries,
        columns: [
          date('Date', 'entryDate'), tag('Type', 'entryType', LEDGER_ENTRY_SEVERITY), text('Reference', 'referenceNumber'),
          text('Narration', 'narration'), currency, money('Debit', 'debitAmount'), money('Credit', 'creditAmount'), money('Balance', 'balance')
        ]
      }
    ]
  },
  {
    key: 'aging-receivables', code: 'R3', title: 'Aging Receivables', icon: 'pi pi-clock',
    description: 'What customers still owe, aged by how long past due.',
    note: 'Invoices still owed on the as-of day, aged by days past their due date: 0-30 includes what is not yet due. Each currency is kept apart. The charts cover every outstanding invoice; the list is paged.',
    ...needs('SALES_INVOICE_VIEW'),
    filters: [{ id: 'asOf', kind: 'asOf', label: 'As of' }, customer],
    sections: [
      { id: 'totals', title: 'Totals', rows: r => r.totals, columns: [currency, int('Invoices', 'invoiceCount'), ...agingBuckets] },
      { id: 'customers', title: 'By customer', rows: r => r.customers, columns: [text('Customer', 'customerName'), currency, int('Invoices', 'invoiceCount'), ...agingBuckets] },
      {
        id: 'invoices', title: 'Outstanding invoices', paged: true, rows: r => r.invoices,
        columns: [
          text('Invoice #', 'invoiceNumber'), text('Order #', 'saleOrderNumber'), text('Customer', 'customerName'), currency,
          date('Invoice date', 'invoiceDate'), date('Due', 'dueDate'), int('Days past due', 'daysPastDue'),
          tag('Age', 'bucket', AGING_SEVERITY), money('Total', 'grandTotal'), money('Paid', 'amountPaid'), money('Outstanding', 'outstanding')
        ]
      }
    ],
    charts: agingCharts
  },
  {
    key: 'sales-by-product', code: 'R4', title: 'Sales by Product', icon: 'pi pi-box',
    description: 'Units and revenue by product, highest revenue first.',
    note: 'Sales are the invoices that stand, counted on the day they were issued, and revenue is what they billed before tax. Each currency is kept apart.',
    ...needs('SALES_INVOICE_VIEW'),
    filters: [dateFrom, dateTo, customer],
    sections: [
      { id: 'totals', title: 'Totals', rows: r => r.totals, columns: [currency, int('Products', 'productCount'), money('Revenue', 'revenue')] },
      {
        id: 'products', title: 'Products', paged: true, rows: r => r.items,
        columns: [text('Product', 'productName'), currency, qty('Quantity sold', 'quantitySold'), money('Revenue', 'revenue'), cost('Average unit price', 'averageUnitPrice')]
      }
    ]
  },
  {
    key: 'sales-by-customer', code: 'R5', title: 'Sales by Customer', icon: 'pi pi-users',
    description: 'Revenue, order count and average order value by customer.',
    note: 'Sales are the invoices that stand, counted on the day they were issued, and revenue is what they billed before tax. An order billed in several invoices counts as one order. Each currency is kept apart.',
    ...needs('SALES_INVOICE_VIEW'),
    filters: [dateFrom, dateTo],
    sections: [
      {
        id: 'totals', title: 'Totals', rows: r => r.totals,
        columns: [currency, int('Customers', 'customerCount'), int('Orders', 'orderCount'), int('Invoices', 'invoiceCount'), money('Revenue', 'revenue'), money('Average order value', 'averageOrderValue')]
      },
      {
        id: 'customers', title: 'Customers', paged: true, rows: r => r.items,
        columns: [text('Customer', 'customerName'), currency, int('Orders', 'orderCount'), int('Invoices', 'invoiceCount'), money('Revenue', 'revenue'), money('Average order value', 'averageOrderValue')]
      }
    ]
  },
  {
    key: 'fulfillment-status', code: 'R6', title: 'Fulfilment Status', icon: 'pi pi-truck',
    description: 'Sale-order deliveries not yet delivered, by status, warehouse and mode.',
    note: 'A delivery is open until it has reached the customer or been given up on. Oldest first: the delivery that has waited longest is at the top.',
    ...needs('DELIVERY_VIEW'),
    filters: [
      { id: 'status', kind: 'select', label: 'Status', options: codeOptions(OPEN_DELIVERY_STATUSES) },
      { id: 'warehouseId', kind: 'warehouse', label: 'Warehouse' },
      { id: 'deliveryMode', kind: 'select', label: 'Delivery', options: [all, ...DELIVERY_MODES] }
    ],
    sections: [
      { id: 'by-status', title: 'By status', rows: r => r.byStatus, columns: [tag('Status', 'status', DELIVERY_STATUS_SEVERITY), int('Deliveries', 'count')] },
      { id: 'by-warehouse', title: 'By warehouse', rows: r => (r.byWarehouse ?? []).map((w: any) => ({ ...w, warehouseName: w.warehouseName ?? 'No warehouse' })), columns: [text('Warehouse', 'warehouseName'), int('Deliveries', 'count')] },
      { id: 'by-mode', title: 'By delivery mode', rows: r => r.byDeliveryMode, columns: [code('Delivery', 'deliveryMode'), int('Deliveries', 'count')] },
      {
        id: 'deliveries', title: 'Open deliveries', paged: true, rows: r => r.items,
        columns: [
          text('Delivery #', 'deliveryNumber'), text('Order #', 'saleOrderNumber'), text('Customer', 'customerName'),
          tag('Status', 'status', DELIVERY_STATUS_SEVERITY), code('Delivery', 'deliveryMode'), text('Warehouse', 'warehouseName'),
          date('Requested', 'requestedDate'), date('Promised', 'promisedDate'), int('Days open', 'daysOpen'),
          int('Lines', 'lineCount'), qty('Ordered', 'quantityOrdered'), qty('Delivered', 'quantityDelivered')
        ]
      }
    ]
  },
  {
    key: 'margin-analysis', code: 'R7', title: 'Margin Analysis', icon: 'pi pi-percentage',
    description: 'Selling price against purchase price, by product, customer or order.',
    note: 'The margin sale orders are expected to make, before anything is invoiced: the price on the order line against what the purchase orders raised for it cost. Only lines with a purchase order behind them are costed; the totals count the rest as uncosted.',
    ...needs('SALE_ORDER_VIEW'),
    filters: [
      dateFrom, dateTo,
      {
        id: 'groupBy', kind: 'select', label: 'Group by', defaultValue: 'PRODUCT',
        options: [{ label: 'Product', value: 'PRODUCT' }, { label: 'Customer', value: 'CUSTOMER' }, { label: 'Order', value: 'ORDER' }]
      }
    ],
    sections: [
      {
        id: 'totals', title: 'Totals', rows: r => r.totals,
        columns: [currency, int('Groups', 'groupCount'), int('Costed lines', 'lineCount'), int('Uncosted lines', 'uncostedLineCount'), money('Selling value', 'sellingValue'), money('Cost', 'cost'), money('Margin', 'margin'), percent('Margin %', 'marginPercent')]
      },
      {
        id: 'margins', title: 'Margins', paged: true, rows: r => r.items,
        columns: [
          text(r => ({ PRODUCT: 'Product', CUSTOMER: 'Customer', ORDER: 'Order' } as Record<string, string>)[r?.criteria?.groupBy] ?? 'Name', 'name'),
          text('Customer', 'detail'), currency, int('Lines', 'lineCount'), qty('Quantity', 'quantity'),
          money('Selling value', 'sellingValue'), money('Cost', 'cost'), money('Margin', 'margin'), percent('Margin %', 'marginPercent'),
          cost('Average price', 'averageSellingPrice'), cost('Average cost', 'averageCost')
        ]
      }
    ],
    charts: marginCharts
  },
  {
    key: 'sales-vs-purchase', code: 'R8', title: 'Sales vs Purchase', icon: 'pi pi-chart-line',
    description: 'Revenue against the cost of the goods sold, by period.',
    note: 'The margin actually made: what the invoices that stand billed, before tax, against the cost of sales the product ledger booked when they were issued. Uncosted revenue is from invoices with no cost booked at all, so it makes the margin look better than it was.',
    ...needs('SALES_INVOICE_VIEW', 'PRODUCT_LEDGER_VIEW'),
    filters: [
      dateFrom, dateTo,
      {
        id: 'period', kind: 'select', label: 'Period', defaultValue: 'MONTH',
        options: [{ label: 'Day', value: 'DAY' }, { label: 'Week', value: 'WEEK' }, { label: 'Month', value: 'MONTH' }]
      }
    ],
    sections: [
      {
        id: 'totals', title: 'Totals', rows: r => r.totals,
        columns: [currency, int('Periods', 'periodCount'), int('Invoices', 'invoiceCount'), money('Revenue', 'revenue'), money('Cost of goods sold', 'costOfGoodsSold'), money('Gross margin', 'grossMargin'), percent('Margin %', 'grossMarginPercent'), money('Uncosted revenue', 'uncostedRevenue')]
      },
      {
        id: 'periods', title: 'Periods', paged: true, rows: r => r.items,
        columns: [text('Period', 'periodLabel'), currency, int('Invoices', 'invoiceCount'), money('Revenue', 'revenue'), money('Cost of goods sold', 'costOfGoodsSold'), money('Gross margin', 'grossMargin'), percent('Margin %', 'grossMarginPercent'), money('Uncosted revenue', 'uncostedRevenue')]
      }
    ],
    charts: salesVsPurchaseCharts
  },
  {
    key: 'product-ledger', code: 'R9', title: 'Product Ledger', icon: 'pi pi-list',
    description: "One product's stock account: every movement, with the quantity and value held after it.",
    note: "A product ledger is one product's account, of all its variants, so a product is required; a variant narrows it. The running figures are the product's, over all its variants. Cost is among the most sensitive numbers a company has, which is why this needs the product ledger permission.",
    ...needs('PRODUCT_LEDGER_VIEW'),
    filters: [
      { id: 'productId', kind: 'product', label: 'Product', required: true },
      { id: 'variantId', kind: 'variant', label: 'Variant' },
      dateFrom, dateTo
    ],
    sections: [
      {
        id: 'summary', title: 'Summary', rows: r => (r.summary ? [r.summary] : []),
        columns: [
          qty('Opening qty', 'openingQuantity'), money('Opening value', 'openingValue'), qty('Qty in', 'quantityIn'), money('Value in', 'valueIn'),
          qty('Qty out', 'quantityOut'), money('Value out', 'valueOut'), qty('Closing qty', 'closingQuantity'), money('Closing value', 'closingValue'),
          cost('Closing average cost', 'closingWeightedAverageCost'), int('Movements', 'movementCount')
        ]
      },
      {
        id: 'movements', title: 'Movements', paged: true, rows: r => r.items,
        columns: [
          date('Date', 'entryDate'), text('Variant', 'variantName'), text('SKU', 'sku'), code('Type', 'entryType'), text('Reference', 'referenceNumber'),
          text('Partner', 'partnerName'), code('Direction', 'direction'), qty('Quantity', 'quantity'), cost('Unit cost', 'unitCost'), money('Total cost', 'totalCost'),
          qty('Held qty', 'runningQty'), money('Held value', 'runningValue'), cost('Average cost', 'weightedAverageCost')
        ]
      }
    ]
  },
  {
    key: 'product-profitability', code: 'R10', title: 'Product Profitability', icon: 'pi pi-star',
    description: 'Revenue less cost of goods sold by product, ranked by margin.',
    note: 'Over the sales that have both an invoice and a cost of sales booked on the product ledger: a sale whose cost was never booked is left out rather than shown at a full margin. Ranked within each currency, as currencies cannot be compared.',
    ...needs('PRODUCT_LEDGER_VIEW', 'SALES_INVOICE_VIEW'),
    filters: [dateFrom, dateTo],
    sections: [
      {
        id: 'totals', title: 'Totals', rows: r => r.totals,
        columns: [currency, int('Products', 'productCount'), money('Revenue', 'revenue'), money('Cost of goods sold', 'costOfGoodsSold'), money('Gross profit', 'grossProfit'), percent('Margin %', 'marginPercent')]
      },
      {
        id: 'products', title: 'Products', paged: true, rows: r => r.items,
        columns: [int('Rank', 'rank'), text('Product', 'productName'), currency, qty('Quantity sold', 'quantitySold'), money('Revenue', 'revenue'), money('Cost of goods sold', 'costOfGoodsSold'), money('Gross profit', 'grossProfit'), percent('Margin %', 'marginPercent')]
      }
    ],
    charts: profitabilityCharts
  }
];

export function findReport(key: string | null | undefined): ReportDef | undefined {
  return SALES_REPORTS.find(r => r.key === key);
}
