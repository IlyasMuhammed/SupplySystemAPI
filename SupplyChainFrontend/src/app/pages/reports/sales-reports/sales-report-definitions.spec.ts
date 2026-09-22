import { OPEN_DELIVERY_STATUSES, SALES_REPORTS, findReport } from './sales-report-definitions';

// These pin the dashboard to what the server says about each report (SalesReportsController and its siblings):
// the address, the permissions, the filters it reads, and the properties of every row. A field named wrong here
// is not an error on screen, only a dash where a figure should be, which is why the property names are checked.

const REPORT_VIEW = 'REPORT_VIEW';
const REPORT_EXPORT = 'REPORT_EXPORT';

/** The books each report needs besides the reports permission, exactly as its controller declares them. */
const BOOKS: Record<string, string[]> = {
  'order-register':        ['SALE_ORDER_VIEW'],
  'customer-ledger':       ['CUSTOMER_LEDGER_VIEW'],
  'aging-receivables':     ['SALES_INVOICE_VIEW'],
  'sales-by-product':      ['SALES_INVOICE_VIEW'],
  'sales-by-customer':     ['SALES_INVOICE_VIEW'],
  'fulfillment-status':    ['DELIVERY_VIEW'],
  'margin-analysis':       ['SALE_ORDER_VIEW'],
  'sales-vs-purchase':     ['SALES_INVOICE_VIEW', 'PRODUCT_LEDGER_VIEW'],
  'product-ledger':        ['PRODUCT_LEDGER_VIEW'],
  'product-profitability': ['PRODUCT_LEDGER_VIEW', 'SALES_INVOICE_VIEW']
};

/** The query parameters each report reads (its filter class), other than paging. */
const PARAMETERS: Record<string, string[]> = {
  'order-register':        ['dateFrom', 'dateTo', 'status', 'partnerId', 'deliveryMode'],
  'customer-ledger':       ['partnerId', 'dateFrom', 'dateTo'],
  'aging-receivables':     ['asOf', 'partnerId'],
  'sales-by-product':      ['dateFrom', 'dateTo', 'partnerId'],
  'sales-by-customer':     ['dateFrom', 'dateTo'],
  'fulfillment-status':    ['status', 'warehouseId', 'deliveryMode'],
  'margin-analysis':       ['dateFrom', 'dateTo', 'groupBy'],
  'sales-vs-purchase':     ['dateFrom', 'dateTo', 'period'],
  'product-ledger':        ['productId', 'variantId', 'dateFrom', 'dateTo'],
  'product-profitability': ['dateFrom', 'dateTo']
};

/** The properties of the rows of each table, as the server's models name them. */
const ROW_PROPERTIES: Record<string, Record<string, string[]>> = {
  'order-register': {
    totals: ['currencyCode', 'orderCount', 'subtotal', 'discountAmount', 'taxAmount', 'grandTotal'],
    orders: ['soNumber', 'orderDate', 'customerName', 'status', 'deliveryMode', 'currencyCode', 'lineCount', 'subtotal', 'discountAmount', 'taxAmount', 'grandTotal']
  },
  'customer-ledger': {
    summary: ['currencyCode', 'openingBalance', 'totalDebit', 'totalCredit', 'closingBalance', 'entryCount'],
    entries: ['entryDate', 'entryType', 'referenceNumber', 'narration', 'currencyCode', 'debitAmount', 'creditAmount', 'balance']
  },
  'aging-receivables': {
    totals: ['currencyCode', 'invoiceCount', 'days0To30', 'days31To60', 'days61To90', 'over90', 'total'],
    customers: ['customerName', 'currencyCode', 'invoiceCount', 'days0To30', 'days31To60', 'days61To90', 'over90', 'total'],
    invoices: ['invoiceNumber', 'saleOrderNumber', 'customerName', 'currencyCode', 'invoiceDate', 'dueDate', 'daysPastDue', 'bucket', 'grandTotal', 'amountPaid', 'outstanding']
  },
  'sales-by-product': {
    totals: ['currencyCode', 'productCount', 'revenue'],
    products: ['productName', 'currencyCode', 'quantitySold', 'revenue', 'averageUnitPrice']
  },
  'sales-by-customer': {
    totals: ['currencyCode', 'customerCount', 'orderCount', 'invoiceCount', 'revenue', 'averageOrderValue'],
    customers: ['customerName', 'currencyCode', 'orderCount', 'invoiceCount', 'revenue', 'averageOrderValue']
  },
  'fulfillment-status': {
    'by-status': ['status', 'count'],
    'by-warehouse': ['warehouseName', 'count'],
    'by-mode': ['deliveryMode', 'count'],
    deliveries: ['deliveryNumber', 'saleOrderNumber', 'customerName', 'status', 'deliveryMode', 'warehouseName', 'requestedDate', 'promisedDate', 'daysOpen', 'lineCount', 'quantityOrdered', 'quantityDelivered']
  },
  'margin-analysis': {
    totals: ['currencyCode', 'groupCount', 'lineCount', 'uncostedLineCount', 'sellingValue', 'cost', 'margin', 'marginPercent'],
    margins: ['name', 'detail', 'currencyCode', 'lineCount', 'quantity', 'sellingValue', 'cost', 'margin', 'marginPercent', 'averageSellingPrice', 'averageCost']
  },
  'sales-vs-purchase': {
    totals: ['currencyCode', 'periodCount', 'invoiceCount', 'revenue', 'costOfGoodsSold', 'grossMargin', 'grossMarginPercent', 'uncostedRevenue'],
    periods: ['periodLabel', 'currencyCode', 'invoiceCount', 'revenue', 'costOfGoodsSold', 'grossMargin', 'grossMarginPercent', 'uncostedRevenue']
  },
  'product-ledger': {
    summary: ['openingQuantity', 'openingValue', 'quantityIn', 'valueIn', 'quantityOut', 'valueOut', 'closingQuantity', 'closingValue', 'closingWeightedAverageCost', 'movementCount'],
    movements: ['entryDate', 'variantName', 'sku', 'entryType', 'referenceNumber', 'partnerName', 'direction', 'quantity', 'unitCost', 'totalCost', 'runningQty', 'runningValue', 'weightedAverageCost']
  },
  'product-profitability': {
    totals: ['currencyCode', 'productCount', 'revenue', 'costOfGoodsSold', 'grossProfit', 'marginPercent'],
    products: ['rank', 'productName', 'currencyCode', 'quantitySold', 'revenue', 'costOfGoodsSold', 'grossProfit', 'marginPercent']
  }
};

describe('sales report definitions', () => {
  it('has the ten reports of the addendum, R1 to R10, in order', () => {
    expect(SALES_REPORTS.map(r => r.code)).toEqual(['R1', 'R2', 'R3', 'R4', 'R5', 'R6', 'R7', 'R8', 'R9', 'R10']);
    expect(SALES_REPORTS.map(r => r.key)).toEqual([
      'order-register', 'customer-ledger', 'aging-receivables', 'sales-by-product', 'sales-by-customer',
      'fulfillment-status', 'margin-analysis', 'sales-vs-purchase', 'product-ledger', 'product-profitability'
    ]);
  });

  it('finds a report by its key, and nothing for a key that is not one', () => {
    expect(findReport('aging-receivables')!.code).toBe('R3');
    expect(findReport('nope')).toBeUndefined();
    expect(findReport(null)).toBeUndefined();
    expect(findReport(undefined)).toBeUndefined();
  });

  it('says what each report is, in words', () => {
    for (const r of SALES_REPORTS) {
      expect(r.title).withContext(r.key).not.toBe('');
      expect(r.description).withContext(r.key).not.toBe('');
      expect(r.note).withContext(r.key).not.toBe('');
      expect(r.icon).withContext(r.key).toContain('pi ');
    }
  });

  describe('permissions', () => {
    for (const report of SALES_REPORTS) {
      it(`${report.code} needs the reports permission and the books it is made of, to see it and to download it`, () => {
        expect(report.viewPermissions).toEqual([REPORT_VIEW, ...BOOKS[report.key]]);
        expect(report.exportPermissions).toEqual([REPORT_EXPORT, ...BOOKS[report.key]]);
      });
    }
  });

  describe('filters', () => {
    for (const report of SALES_REPORTS) {
      it(`${report.code} filters by exactly what the server reads`, () => {
        expect(report.filters.map(f => f.id)).toEqual(PARAMETERS[report.key]);
      });
    }

    it('requires a customer for the ledger and a product for the product ledger, and nothing else', () => {
      const required = SALES_REPORTS.flatMap(r => r.filters.filter(f => f.required).map(f => `${r.code}:${f.id}`));

      expect(required).toEqual(['R2:partnerId', 'R9:productId']);
    });

    it('offers the sale order statuses, and the delivery statuses that are still open', () => {
      const values = (key: string, id: string) => findReport(key)!.filters.find(f => f.id === id)!.options!.map(o => o.value);

      expect(values('order-register', 'status')).toEqual(['', 'DRAFT', 'CONFIRMED', 'PARTIALLY_FULFILLED', 'FULFILLED', 'INVOICED', 'CLOSED', 'CANCELLED']);
      expect(values('fulfillment-status', 'status')).toEqual(['', ...OPEN_DELIVERY_STATUSES]);
      expect(OPEN_DELIVERY_STATUSES).not.toContain('DELIVERED');
      expect(OPEN_DELIVERY_STATUSES).not.toContain('CANCELLED');
      expect(OPEN_DELIVERY_STATUSES).not.toContain('CLOSED');
      expect(OPEN_DELIVERY_STATUSES).not.toContain('SHORT_CLOSED');
    });

    it('offers both delivery modes, and the groupings and periods the server accepts', () => {
      const options = (key: string, id: string) => findReport(key)!.filters.find(f => f.id === id)!.options!.map(o => o.value);

      expect(options('order-register', 'deliveryMode')).toEqual(['', 'SHIP', 'SELF_PICKUP']);
      expect(options('fulfillment-status', 'deliveryMode')).toEqual(['', 'SHIP', 'SELF_PICKUP']);
      expect(options('margin-analysis', 'groupBy')).toEqual(['PRODUCT', 'CUSTOMER', 'ORDER']);
      expect(options('sales-vs-purchase', 'period')).toEqual(['DAY', 'WEEK', 'MONTH']);
    });

    it('starts the margin analysis grouped by product and sales vs purchase by month, as the server does', () => {
      expect(findReport('margin-analysis')!.filters.find(f => f.id === 'groupBy')!.defaultValue).toBe('PRODUCT');
      expect(findReport('sales-vs-purchase')!.filters.find(f => f.id === 'period')!.defaultValue).toBe('MONTH');
    });
  });

  describe('tables', () => {
    for (const report of SALES_REPORTS) {
      it(`${report.code} reads only properties the servers rows have`, () => {
        const expected = ROW_PROPERTIES[report.key];

        expect(report.sections.map(s => s.id)).withContext('the tables').toEqual(Object.keys(expected));
        for (const section of report.sections) {
          for (const column of section.columns) {
            expect(expected[section.id]).withContext(`${report.code} ${section.id}: ${column.field}`).toContain(column.field);
          }
        }
      });

      it(`${report.code} lists its own rows in one paged table, and last`, () => {
        const paged = report.sections.filter(s => s.paged);

        expect(paged.length).toBe(1);
        expect(report.sections[report.sections.length - 1]).toBe(paged[0]);
      });
    }

    it('gives every column a heading and a property, once to a table', () => {
      for (const report of SALES_REPORTS) {
        for (const section of report.sections) {
          const fields = section.columns.map(c => c.field);
          expect(new Set(fields).size).withContext(`${report.code} ${section.id}`).toBe(fields.length);
          for (const column of section.columns) {
            expect(column.header).not.toBe('');
            expect(column.field).not.toBe('');
          }
        }
      }
    });

    it('colours every tag it draws', () => {
      for (const report of SALES_REPORTS) {
        for (const column of report.sections.flatMap(s => s.columns).filter(c => c.kind === 'tag')) {
          expect(Object.keys(column.severity ?? {}).length).withContext(`${report.code} ${column.field}`).toBeGreaterThan(0);
        }
      }
    });

    it('names the margin analysis rows by what it is grouped by', () => {
      const header = findReport('margin-analysis')!.sections[1].columns[0].header as (r: any) => string;

      expect(header({ criteria: { groupBy: 'PRODUCT' } })).toBe('Product');
      expect(header({ criteria: { groupBy: 'CUSTOMER' } })).toBe('Customer');
      expect(header({ criteria: { groupBy: 'ORDER' } })).toBe('Order');
      expect(header(null)).toBe('Name');
    });

    it('reads its rows out of the report, and copes with a report that has none', () => {
      const ledger = findReport('product-ledger')!;

      expect(ledger.sections[0].rows({ summary: { movementCount: 3 } })).toEqual([{ movementCount: 3 }]);
      expect(ledger.sections[0].rows({})).toEqual([]);

      const warehouses = findReport('fulfillment-status')!.sections[1].rows({ byWarehouse: [{ warehouseName: null, count: 2 }, { warehouseName: 'Main', count: 1 }] });
      expect(warehouses.map((w: any) => w.warehouseName)).toEqual(['No warehouse', 'Main']);
    });
  });

  it('draws charts for the aging and margin reports, and for no other', () => {
    expect(SALES_REPORTS.filter(r => r.charts).map(r => r.code)).toEqual(['R3', 'R7', 'R8', 'R10']);
  });
});
