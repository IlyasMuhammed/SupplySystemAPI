import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { Subject, of, throwError } from 'rxjs';

import { SalesReportPanelComponent } from './sales-report-panel.component';
import { SalesReportsService } from '../../../../services/sales-reports.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { InventoryService, ProductListItemModel } from '../../../../services/inventory.service';
import { AuthService } from '../../../service/auth.service';
import { SALES_REPORTS, findReport } from '../sales-report-definitions';

const CUSTOMER = { uuid: 'cust-1', companyName: 'Acme Ltd' } as BusinessPartnerModel;
const PRODUCT = { id: 7, uuid: 'prod-1', sku: 'CAB-4MM', name: '4mm cable' } as ProductListItemModel;

const ALL_PERMISSIONS = [
  'REPORT_VIEW', 'REPORT_EXPORT', 'SALE_ORDER_VIEW', 'CUSTOMER_LEDGER_VIEW', 'SALES_INVOICE_VIEW',
  'DELIVERY_VIEW', 'PRODUCT_LEDGER_VIEW'
];

const page = { companyName: 'Acme Trading', generatedAt: '2026-09-21T09:30:00', totalRecords: 1, page: 1, pageSize: 20, totalPages: 1 };

/** One row in every table of every report, shaped as the server's models are. */
const SAMPLE: Record<string, any> = {
  'order-register': {
    ...page,
    totals: [{ currencyCode: 'PKR', orderCount: 3, subtotal: 900, discountAmount: 0, taxAmount: 100, grandTotal: 1000 }],
    items: [{ uuid: 'so-1', soNumber: 'SO-2026-00042', orderDate: '2026-09-01T00:00:00', customerName: 'Acme Ltd', status: 'PARTIALLY_FULFILLED', deliveryMode: 'SELF_PICKUP', currencyCode: 'PKR', lineCount: 2, subtotal: 900, discountAmount: 0, taxAmount: 100, grandTotal: 1000 }]
  },
  'customer-ledger': {
    ...page,
    summaries: [{ currencyCode: 'PKR', openingBalance: 0, totalDebit: 1170, totalCredit: 400, closingBalance: 770, entryCount: 2 }],
    entries: [{ sequenceNo: 1, entryDate: '2026-09-20T00:00:00', entryType: 'INVOICE', referenceNumber: 'SINV-20260920-0001', narration: 'Invoice issued', currencyCode: 'PKR', debitAmount: 1170, creditAmount: 0, balance: 1170 }]
  },
  'aging-receivables': {
    ...page,
    totals: [{ currencyCode: 'PKR', invoiceCount: 2, days0To30: 100, days31To60: 50, days61To90: 0, over90: 25, total: 175 }],
    customers: [
      { partnerId: 'c1', customerName: 'Acme Ltd', currencyCode: 'PKR', invoiceCount: 1, days0To30: 100, days31To60: 0, days61To90: 0, over90: 0, total: 100 },
      { partnerId: 'c2', customerName: 'Globex Corp', currencyCode: 'PKR', invoiceCount: 1, days0To30: 0, days31To60: 50, days61To90: 0, over90: 25, total: 75 }
    ],
    invoices: [{ invoiceNumber: 'SINV-20260701-0001', saleOrderNumber: 'SO-1', customerName: 'Globex Corp', currencyCode: 'PKR', invoiceDate: '2026-07-01T00:00:00', dueDate: '2026-07-31T00:00:00', daysPastDue: 52, bucket: '31-60', grandTotal: 75, amountPaid: 0, outstanding: 75 }]
  },
  'sales-by-product': {
    ...page,
    totals: [{ currencyCode: 'PKR', productCount: 1, revenue: 4000 }],
    items: [{ productUuid: 'p1', productName: '4mm cable', currencyCode: 'PKR', quantitySold: 100, revenue: 4000, averageUnitPrice: 40 }]
  },
  'sales-by-customer': {
    ...page,
    totals: [{ currencyCode: 'PKR', customerCount: 1, orderCount: 2, invoiceCount: 3, revenue: 5000, averageOrderValue: 2500 }],
    items: [{ partnerId: 'c1', customerName: 'Acme Ltd', currencyCode: 'PKR', orderCount: 2, invoiceCount: 3, revenue: 5000, averageOrderValue: 2500 }]
  },
  'fulfillment-status': {
    ...page,
    byStatus: [{ status: 'PICKING', count: 2 }],
    byWarehouse: [{ warehouseUuid: null, warehouseName: null, count: 2 }],
    byDeliveryMode: [{ deliveryMode: 'SHIP', count: 2 }],
    items: [{ deliveryUuid: 'd1', deliveryNumber: 'DLV-2026-00001', saleOrderNumber: 'SO-2026-00042', customerName: 'Acme Ltd', status: 'PICKING', deliveryMode: 'SHIP', warehouseName: null, requestedDate: null, promisedDate: '2026-09-25T00:00:00', createdDate: '2026-09-15T00:00:00', daysOpen: 6, lineCount: 1, quantityOrdered: 100, quantityDelivered: 0 }]
  },
  'margin-analysis': {
    ...page, criteria: { groupBy: 'CUSTOMER' },
    totals: [{ currencyCode: 'PKR', groupCount: 1, lineCount: 2, uncostedLineCount: 1, sellingValue: 1000, cost: 700, margin: 300, marginPercent: 30 }],
    items: [{ groupId: 'g1', name: 'Acme Ltd', detail: null, currencyCode: 'PKR', lineCount: 2, quantity: null, sellingValue: 1000, cost: 700, margin: 300, marginPercent: 30, averageSellingPrice: null, averageCost: null }]
  },
  'sales-vs-purchase': {
    ...page, criteria: { period: 'MONTH' },
    totals: [{ currencyCode: 'PKR', periodCount: 1, invoiceCount: 2, revenue: 2000, costOfGoodsSold: 1200, grossMargin: 800, grossMarginPercent: 40, uncostedRevenue: 0 }],
    items: [{ periodStart: '2026-09-01T00:00:00', periodLabel: '2026-09', currencyCode: 'PKR', invoiceCount: 2, revenue: 2000, costOfGoodsSold: 1200, grossMargin: 800, grossMarginPercent: 40, uncostedRevenue: 0 }]
  },
  'product-ledger': {
    ...page,
    summary: { openingQuantity: 10, openingValue: 100, quantityIn: 5, valueIn: 50, quantityOut: 3, valueOut: 30, closingQuantity: 12, closingValue: 120, closingWeightedAverageCost: 10, movementCount: 2 },
    items: [{ entryUuid: 'e1', variantUuid: 'v1', sku: 'CAB-4MM', variantName: '4mm', entryDate: '2026-09-02T00:00:00', entryType: 'PURCHASE', referenceNumber: 'GRN-1', partnerName: 'Supplier Ltd', direction: 'IN', quantity: 5, unitCost: 10.1234, totalCost: 50.62, runningQty: 15, runningValue: 150.62, weightedAverageCost: 10.0413 }]
  },
  'product-profitability': {
    ...page,
    totals: [{ currencyCode: 'PKR', productCount: 2, revenue: 1000, costOfGoodsSold: 600, grossProfit: 400, marginPercent: 40 }],
    items: [
      { rank: 1, productUuid: 'p1', productName: '4mm cable', currencyCode: 'PKR', quantitySold: 10, revenue: 600, costOfGoodsSold: 300, grossProfit: 300, marginPercent: 50 },
      { rank: 2, productUuid: 'p2', productName: 'Wire', currencyCode: 'PKR', quantitySold: 5, revenue: 400, costOfGoodsSold: 300, grossProfit: 100, marginPercent: 25 }
    ]
  }
};

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('SalesReportPanelComponent', () => {
  let fixture: ComponentFixture<SalesReportPanelComponent>;
  let component: SalesReportPanelComponent;
  let reports: jasmine.SpyObj<SalesReportsService>;
  let partners: jasmine.SpyObj<BusinessPartnerService>;
  let inventory: jasmine.SpyObj<InventoryService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function lastToast() {
    return toasts.calls.mostRecent().args[0] as { severity: string; summary: string; detail: string };
  }

  /** Waits for the toast a failed download raises once it has read the reason out of the bytes it came back as. */
  async function untilToasted() {
    for (let i = 0; i < 200 && !toasts.calls.count(); i++) await new Promise(resolve => setTimeout(resolve, 5));
  }

  async function create() {
    reports = jasmine.createSpyObj<SalesReportsService>('SalesReportsService', ['getReport', 'download']);
    reports.getReport.and.callFake((key: any) => ok(SAMPLE[key]));
    reports.download.and.returnValue(of(new Blob(['%PDF'])));

    partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(ok({ data: [CUSTOMER], totalRecords: 1, page: 1, pageSize: 20, totalPages: 1 }));

    inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getProductById', 'getWarehouses']);
    inventory.getProducts.and.returnValue(ok({ data: [PRODUCT], totalRecords: 1, page: 1, pageSize: 500, totalPages: 1 }));
    inventory.getProductById.and.returnValue(ok({
      ...PRODUCT, variants: [
        { uuid: 'v-1', sku: 'CAB-4MM-R', variantName: 'Red' },
        { uuid: 'v-2', sku: 'CAB-4MM', variantName: '' }
      ]
    }));
    inventory.getWarehouses.and.returnValue(ok([{ uuid: 'w-1', name: 'Main warehouse', code: 'MAIN' }, { uuid: 'w-2', name: 'North', code: 'N' }]));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SalesReportPanelComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(), MessageService,
        { provide: SalesReportsService, useValue: reports },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: InventoryService, useValue: inventory },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SalesReportPanelComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
  }

  /** Shows a report, as the dashboard does. */
  async function show(key: string) {
    await create();
    fixture.componentRef.setInput('definition', findReport(key));
    fixture.detectChanges();
    fixture.detectChanges();
  }

  beforeEach(() => { permissions = [...ALL_PERMISSIONS]; });

  // ── Starting ───────────────────────────────────────────────────────────────

  it('runs a report that needs nothing from the user at once, with the first page and only the filters that start filled', async () => {
    await show('order-register');

    expect(reports.getReport).toHaveBeenCalledOnceWith('order-register', { page: 1, pageSize: 20 });
    expect(query('not-run')).toBeNull();
  });

  it('starts the margin analysis grouped by product, and sales vs purchase by month', async () => {
    await show('margin-analysis');
    expect(reports.getReport).toHaveBeenCalledOnceWith('margin-analysis', { groupBy: 'PRODUCT', page: 1, pageSize: 20 });

    await show('sales-vs-purchase');
    expect(reports.getReport).toHaveBeenCalledOnceWith('sales-vs-purchase', { period: 'MONTH', page: 1, pageSize: 20 });
  });

  it('does not run a report that needs a customer, or a product, until it has one', async () => {
    await show('customer-ledger');
    expect(reports.getReport).not.toHaveBeenCalled();
    expect(query('not-run')!.textContent).toContain('Choose the customer to run this report.');

    await show('product-ledger');
    expect(reports.getReport).not.toHaveBeenCalled();
    expect(query('not-run')!.textContent).toContain('Choose the product to run this report.');
  });

  it('starts again with empty filters when it is given another report, and forgets the last one', async () => {
    await show('aging-receivables');
    component.values['asOf'] = new Date(2026, 8, 1);
    component.values['partnerId'] = CUSTOMER;
    expect(component.report).not.toBeNull();

    fixture.componentRef.setInput('definition', findReport('sales-by-customer'));
    fixture.detectChanges();

    expect(component.values['asOf']).toBeUndefined();
    expect(component.values['partnerId']).toBeUndefined();
    expect(Object.keys(component.values)).toEqual(['dateFrom', 'dateTo']);
    expect(component.report!['totals'][0].customerCount).withContext('the new report has run').toBe(1);
  });

  it('ignores an answer for the report it was showing before', async () => {
    await create();
    const slow = new Subject<any>();
    reports.getReport.and.returnValue(slow);
    fixture.componentRef.setInput('definition', findReport('order-register'));
    fixture.detectChanges();

    reports.getReport.and.callFake((key: any) => ok(SAMPLE[key]));
    fixture.componentRef.setInput('definition', findReport('sales-by-product'));
    fixture.detectChanges();
    slow.next({ success: true, message: '', result: SAMPLE['order-register'] });

    expect(component.report!['items'][0].productName).toBe('4mm cable');
  });

  // ── Filters into parameters ────────────────────────────────────────────────

  it('sends dates as calendar days, and the customer, status and mode as the server reads them', async () => {
    await show('order-register');

    component.values['dateFrom'] = new Date(2026, 8, 1);
    component.values['dateTo'] = new Date(2026, 8, 30, 23, 59);
    component.values['status'] = 'CONFIRMED';
    component.values['partnerId'] = CUSTOMER;
    component.values['deliveryMode'] = 'SELF_PICKUP';

    expect(component.buildParams()).toEqual({
      dateFrom: '2026-09-01', dateTo: '2026-09-30', status: 'CONFIRMED', partnerId: 'cust-1', deliveryMode: 'SELF_PICKUP'
    });
  });

  it('sends an as-of day for the aging, and leaves it out to mean today', async () => {
    await show('aging-receivables');
    expect(component.buildParams()).toEqual({});

    component.values['asOf'] = new Date(2026, 8, 15);
    expect(component.buildParams()).toEqual({ asOf: '2026-09-15' });
  });

  it('sends the product and variant of the product ledger, and the warehouse of the fulfilment report', async () => {
    await show('product-ledger');
    component.values['productId'] = PRODUCT;
    component.values['variantId'] = 'v-1';
    expect(component.buildParams()).toEqual({ productId: 'prod-1', variantId: 'v-1' });

    await show('fulfillment-status');
    component.values['warehouseId'] = 'w-2';
    expect(component.buildParams()).toEqual({ warehouseId: 'w-2' });
  });

  it('does not send a customer that was typed but never picked', async () => {
    await show('order-register');

    component.values['partnerId'] = 'Acm';

    expect(component.buildParams()).toEqual({});
  });

  it('refuses a range that ends before it starts, and asks nothing of the server', async () => {
    await show('order-register');
    reports.getReport.calls.reset();
    component.values['dateFrom'] = new Date(2026, 8, 30);
    component.values['dateTo'] = new Date(2026, 8, 1);

    component.run();

    expect(lastToast().severity).toBe('warn');
    expect(lastToast().detail).toBe('The start date is after the end date.');
    expect(reports.getReport).not.toHaveBeenCalled();
  });

  it('takes a range that starts and ends on the same day', async () => {
    await show('order-register');
    component.values['dateFrom'] = new Date(2026, 8, 5, 18);
    component.values['dateTo'] = new Date(2026, 8, 5, 2);

    expect(component.problem()).toBeNull();
  });

  it('refuses to run without the customer the ledger needs, and runs once it has one', async () => {
    await show('customer-ledger');

    component.run();
    expect(lastToast().detail).toBe('Choose the customer to run this report.');
    expect(reports.getReport).not.toHaveBeenCalled();

    component.values['partnerId'] = CUSTOMER;
    component.run();
    expect(reports.getReport).toHaveBeenCalledOnceWith('customer-ledger', { partnerId: 'cust-1', page: 1, pageSize: 20 });
  });

  // ── Paging ─────────────────────────────────────────────────────────────────

  it('runs from the first page when run again, and follows the table to another page and page size', async () => {
    await show('order-register');

    component.onPageChange({ first: 40, rows: 20 });
    expect(reports.getReport.calls.mostRecent().args[1]).toEqual({ page: 3, pageSize: 20 });

    component.onPageChange({ first: 0, rows: 50 });
    expect(reports.getReport.calls.mostRecent().args[1]).toEqual({ page: 1, pageSize: 50 });

    component.page = 4;
    component.run();
    expect(reports.getReport.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({ page: 1 }));
  });

  it('does not load a page before there is a report to page through', async () => {
    await show('customer-ledger');

    component.onPageChange({ first: 20, rows: 20 });

    expect(reports.getReport).not.toHaveBeenCalled();
  });

  // ── The tables ─────────────────────────────────────────────────────────────

  for (const definition of SALES_REPORTS) {
    it(`${definition.code} shows every table it has rows for, with the report's own list first in the data`, async () => {
      await create();
      fixture.componentRef.setInput('definition', definition);
      fixture.detectChanges();
      // A report that needs a customer or a product is given one, as the user would.
      for (const f of definition.filters.filter(f => f.required)) component.values[f.id] = f.kind === 'product' ? PRODUCT : CUSTOMER;
      component.run();
      fixture.detectChanges();
      fixture.detectChanges();

      const shown = Array.from(fixture.nativeElement.querySelectorAll('[data-section]')).map((e: any) => e.getAttribute('data-section'));
      expect(shown).toEqual(definition.sections.map(s => s.id));
      for (const section of definition.sections) {
        const rows = fixture.nativeElement.querySelectorAll(`[data-section="${section.id}"] [data-testid="row"]`);
        expect(rows.length).withContext(`${definition.code} ${section.id}`).toBeGreaterThan(0);
      }
      expect(query('generated')!.textContent).toContain('Acme Trading');
    });
  }

  it('leaves out a table that has no rows, but always shows the reports own list', async () => {
    await create();
    reports.getReport.and.returnValue(ok({ ...SAMPLE['order-register'], totals: [], items: [] }));
    fixture.componentRef.setInput('definition', findReport('order-register'));
    fixture.detectChanges();
    fixture.detectChanges();

    const shown = Array.from(fixture.nativeElement.querySelectorAll('[data-section]')).map((e: any) => e.getAttribute('data-section'));
    expect(shown).toEqual(['orders']);
    expect(fixture.nativeElement.textContent).toContain('Nothing matches these filters.');
  });

  it('writes a cell as words and figures, and a dash where there is nothing', async () => {
    await show('order-register');
    const col = (kind: any, field = 'x') => ({ header: 'h', field, kind });

    expect(component.cell({ x: 1234.5 }, col('money'))).toBe('1,234.50');
    expect(component.cell({ x: 1234.5678 }, col('cost'))).toBe('1,234.5678');
    expect(component.cell({ x: 1234.5 }, col('cost'))).toBe('1,234.50');
    expect(component.cell({ x: 12.5 }, col('qty'))).toBe('12.5');
    expect(component.cell({ x: 1234 }, col('qty'))).toBe('1,234');
    expect(component.cell({ x: 7.4 }, col('int'))).toBe('7');
    expect(component.cell({ x: 12.345 }, col('percent'))).toBe('12.3%');
    expect(component.cell({ x: 0 }, col('percent'))).toBe('0.0%');
    expect(component.cell({ x: '2026-09-20T00:00:00' }, col('date'))).toBe('20 Sep 2026');
    expect(component.cell({ x: 'PARTIALLY_FULFILLED' }, col('code'))).toBe('Partially Fulfilled');
    expect(component.cell({ x: 'PARTIALLY_FULFILLED' }, col('tag'))).toBe('Partially Fulfilled');
    expect(component.cell({ x: 'Acme' }, col(undefined))).toBe('Acme');
    expect(component.cell({ x: 0 }, col('money'))).withContext('zero is a figure').toBe('0.00');
    for (const kind of ['money', 'int', 'date', 'code', 'tag', 'percent', undefined]) {
      expect(component.cell({ x: null }, col(kind))).withContext(String(kind)).toBe('—');
      expect(component.cell({}, col(kind))).toBe('—');
      expect(component.cell({ x: '' }, col(kind))).toBe('—');
    }
  });

  it('colours a status, and shows a status the map does not know in a neutral colour', async () => {
    await show('order-register');
    const col = findReport('order-register')!.sections[1].columns.find(c => c.field === 'status')!;

    expect(component.severity({ status: 'CONFIRMED' }, col)).toBe('info');
    expect(component.severity({ status: 'SOMETHING_NEW' }, col)).toBe('secondary');
  });

  it('aligns figures to the right and words to the left', async () => {
    await show('order-register');

    expect(component.isNumber({ header: 'h', field: 'x', kind: 'money' })).toBeTrue();
    expect(component.isNumber({ header: 'h', field: 'x', kind: 'percent' })).toBeTrue();
    expect(component.isNumber({ header: 'h', field: 'x', kind: 'date' })).toBeFalse();
    expect(component.isNumber({ header: 'h', field: 'x' })).toBeFalse();
  });

  it('names the margin analysis rows by what it is grouped by, in the table heading', async () => {
    await show('margin-analysis');

    const heading = fixture.nativeElement.querySelector('[data-section="margins"] th').textContent.trim();
    expect(heading).toBe('Customer');
  });

  it('names the deliveries that have no warehouse, rather than leaving a blank', async () => {
    await show('fulfillment-status');

    const warehouse = fixture.nativeElement.querySelector('[data-section="by-warehouse"] [data-testid="row"] td').textContent.trim();
    expect(warehouse).toBe('No warehouse');
  });

  // ── Charts ─────────────────────────────────────────────────────────────────

  it('draws the aging charts: what is owed by age, and who owes it', async () => {
    await show('aging-receivables');

    expect(fixture.nativeElement.querySelectorAll('[data-testid="chart-card"]').length).toBe(2);
    expect(component.charts.map(c => c.id)).toEqual(['aging-buckets-PKR', 'aging-customers-PKR']);
    expect(fixture.nativeElement.querySelectorAll('canvas').length).toBe(2);
  });

  it('draws a chart for each of the margin reports', async () => {
    await show('margin-analysis');
    expect(component.charts.map(c => c.id)).toEqual(['margin-PKR']);
    expect(fixture.nativeElement.querySelectorAll('[data-testid="chart-card"]').length).toBe(1);

    await show('sales-vs-purchase');
    expect(component.charts.map(c => c.id)).toEqual(['sales-vs-purchase-PKR']);

    await show('product-profitability');
    expect(component.charts.map(c => c.id)).toEqual(['profitability-PKR']);
  });

  it('draws no charts for a report that has none', async () => {
    await show('order-register');

    expect(component.charts).toEqual([]);
    expect(query('charts')).toBeNull();
  });

  it('redraws the charts for the page it is on', async () => {
    await show('product-profitability');
    const next = { ...SAMPLE['product-profitability'], items: [SAMPLE['product-profitability'].items[1]] };
    reports.getReport.and.returnValue(ok(next));

    component.onPageChange({ first: 20, rows: 20 });

    expect(component.charts[0].data.labels).toEqual(['2. Wire']);
  });

  // ── Failing ────────────────────────────────────────────────────────────────

  it('shows the servers reason when a report fails, and clears what was there', async () => {
    await show('order-register');
    reports.getReport.and.returnValue(throwError(() => ({ error: { message: 'Not a status.' } })));

    component.run();

    expect(lastToast().severity).toBe('error');
    expect(lastToast().detail).toBe('Not a status.');
    expect(component.report).toBeNull();
    expect(component.charts).toEqual([]);
    expect(component.isLoading).toBeFalse();
  });

  it('falls back to a general message when the failure has none', async () => {
    await show('order-register');
    reports.getReport.and.returnValue(throwError(() => ({ status: 0 })));

    component.run();

    expect(lastToast().detail).toBe('The report could not be loaded.');
  });

  it('shows that it is running until the answer comes', async () => {
    await create();
    reports.getReport.and.returnValue(new Subject<any>());
    fixture.componentRef.setInput('definition', findReport('order-register'));
    fixture.detectChanges();
    fixture.detectChanges();

    expect(component.isLoading).toBeTrue();
    expect(query('loading')).not.toBeNull();
  });

  // ── Downloading ────────────────────────────────────────────────────────────

  describe('downloads', () => {
    let click: jasmine.Spy;
    let revoke: jasmine.Spy;
    let anchors: HTMLAnchorElement[];

    beforeEach(() => {
      anchors = [];
      spyOn(URL, 'createObjectURL').and.returnValue('blob:report');
      revoke = spyOn(URL, 'revokeObjectURL');
      click = spyOn(HTMLAnchorElement.prototype, 'click').and.callFake(function (this: HTMLAnchorElement) { anchors.push(this); });
    });

    const today = () => {
      const d = new Date();
      const p = (n: number) => String(n).padStart(2, '0');
      return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}`;
    };

    it('offers the PDF and the workbook only to someone who may download the report', async () => {
      await show('order-register');
      expect(query('export-pdf')).not.toBeNull();
      expect(query('export-excel')).not.toBeNull();

      permissions = permissions.filter(p => p !== 'REPORT_EXPORT');
      await show('order-register');
      expect(component.canExport).toBeFalse();
      expect(query('export-pdf')).toBeNull();
      expect(query('export-excel')).toBeNull();
      expect(query('run')).withContext('viewing is not downloading').not.toBeNull();
    });

    it('needs the books behind the report to download it, not only the reports permission', async () => {
      permissions = ['REPORT_VIEW', 'REPORT_EXPORT'];
      await show('product-profitability');

      expect(component.canExport).toBeFalse();

      permissions = ['REPORT_EXPORT', 'PRODUCT_LEDGER_VIEW'];
      await show('product-profitability');
      expect(component.canExport).withContext('and the other book').toBeFalse();

      permissions = ['REPORT_EXPORT', 'PRODUCT_LEDGER_VIEW', 'SALES_INVOICE_VIEW'];
      await show('product-profitability');
      expect(component.canExport).toBeTrue();
    });

    it('downloads the whole report as a PDF, with the filters and no paging, named for the report and the day', async () => {
      await show('order-register');
      component.values['status'] = 'CONFIRMED';
      component.onPageChange({ first: 20, rows: 20 });

      component.export('pdf');

      expect(reports.download).toHaveBeenCalledOnceWith('order-register', 'pdf', { status: 'CONFIRMED' });
      expect(anchors.length).toBe(1);
      expect(anchors[0].download).toBe(`order-register-${today()}.pdf`);
      expect(anchors[0].getAttribute('href')).toBe('blob:report');
      expect(revoke).toHaveBeenCalledWith('blob:report');
      expect(component.exporting).toBeNull();
    });

    it('downloads a workbook as xlsx', async () => {
      await show('aging-receivables');

      component.export('excel');

      expect(reports.download).toHaveBeenCalledOnceWith('aging-receivables', 'excel', {});
      expect(anchors[0].download).toBe(`aging-receivables-${today()}.xlsx`);
    });

    it('downloads from the buttons', async () => {
      await show('sales-by-customer');

      query('export-pdf')!.querySelector('button')!.click();
      query('export-excel')!.querySelector('button')!.click();

      expect(reports.download.calls.allArgs().map(a => a[1])).toEqual(['pdf', 'excel']);
    });

    it('downloads once however often it is asked while it is working', async () => {
      await show('order-register');
      reports.download.and.returnValue(new Subject<Blob>());

      component.export('pdf');
      component.export('pdf');
      component.export('excel');

      expect(reports.download).toHaveBeenCalledTimes(1);
      expect(component.exporting).toBe('pdf');
    });

    it('will not download for someone who may not', async () => {
      permissions = ['REPORT_VIEW', 'SALE_ORDER_VIEW'];
      await show('order-register');

      component.export('pdf');

      expect(reports.download).not.toHaveBeenCalled();
    });

    it('will not download without what the report needs, and says what', async () => {
      await show('customer-ledger');

      component.export('pdf');

      expect(reports.download).not.toHaveBeenCalled();
      expect(lastToast().detail).toBe('Choose the customer to run this report.');
    });

    it('shows the servers reason, read out of the bytes it came back as, when a download is refused', async () => {
      await show('order-register');
      const body = new Blob([JSON.stringify({ message: '12,000 orders match, more than the 10,000 one document carries. Narrow the filters.' })]);
      reports.download.and.returnValue(throwError(() => ({ status: 400, error: body })));

      toasts.calls.reset();
      component.export('pdf');
      await untilToasted();

      expect(lastToast().severity).toBe('error');
      expect(lastToast().summary).toBe('PDF not downloaded');
      expect(lastToast().detail).toContain('10,000 one document carries');
      expect(component.exporting).toBeNull();
      expect(click).not.toHaveBeenCalled();
    });

    it('falls back to a general message when the refusal is not the servers JSON', async () => {
      await show('order-register');
      reports.download.and.returnValue(throwError(() => ({ status: 500, error: new Blob(['<html>oops</html>']) })));

      toasts.calls.reset();
      component.export('excel');
      await untilToasted();

      expect(lastToast().summary).toBe('Excel not downloaded');
      expect(lastToast().detail).toBe('The download failed.');
    });

    it('reads the reason from an ordinary error body too', async () => {
      await show('order-register');
      reports.download.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Bad range.' } })));

      toasts.calls.reset();
      component.export('pdf');
      await untilToasted();

      expect(lastToast().detail).toBe('Bad range.');
    });
  });

  // ── Lookups ────────────────────────────────────────────────────────────────

  it('fetches products and warehouses only for the reports that filter by them', async () => {
    await show('order-register');
    expect(inventory.getProducts).not.toHaveBeenCalled();
    expect(inventory.getWarehouses).not.toHaveBeenCalled();

    await show('product-ledger');
    expect(inventory.getProducts).toHaveBeenCalledOnceWith({ pageSize: 500 });
    expect(component.productOptions).toEqual([{ label: '4mm cable (CAB-4MM)', value: PRODUCT }]);
    expect(inventory.getWarehouses).not.toHaveBeenCalled();

    await show('fulfillment-status');
    expect(inventory.getWarehouses).toHaveBeenCalledTimes(1);
    expect(component.warehouseOptions).toEqual([
      { label: 'All', value: '' }, { label: 'Main warehouse', value: 'w-1' }, { label: 'North', value: 'w-2' }
    ]);
  });

  it('says so when the products cannot be loaded, and offers all warehouses when they cannot', async () => {
    await create();
    inventory.getProducts.and.returnValue(throwError(() => ({ status: 403 })));
    inventory.getWarehouses.and.returnValue(throwError(() => ({ status: 403 })));
    fixture.componentRef.setInput('definition', findReport('product-ledger'));
    fixture.detectChanges();
    expect(lastToast().detail).toBe('The product list could not be loaded.');

    fixture.componentRef.setInput('definition', findReport('fulfillment-status'));
    fixture.detectChanges();
    expect(component.warehouseOptions).toEqual([{ label: 'All', value: '' }]);
  });

  it('loads the variants of the product chosen, and clears the variant chosen among the last ones', async () => {
    await show('product-ledger');
    component.values['variantId'] = 'stale';

    component.onProductChange(PRODUCT);

    expect(inventory.getProductById).toHaveBeenCalledOnceWith(7);
    expect(component.values['productId']).toBe(PRODUCT);
    expect(component.values['variantId']).toBeNull();
    expect(component.variantOptions).toEqual([
      { label: 'All variants', value: null },
      { label: 'Red (CAB-4MM-R)', value: 'v-1' },
      { label: 'CAB-4MM', value: 'v-2' }
    ]);
  });

  it('empties the variants when the product is cleared', async () => {
    await show('product-ledger');
    component.onProductChange(PRODUCT);

    component.onProductChange(null);

    expect(component.variantOptions).toEqual([]);
    expect(component.values['productId']).toBeNull();
  });

  it('carries on with no variants when they cannot be loaded, and ignores variants of a product no longer chosen', async () => {
    await show('product-ledger');
    inventory.getProductById.and.returnValue(throwError(() => ({ status: 500 })));
    component.onProductChange(PRODUCT);
    expect(component.variantOptions).toEqual([]);

    const late = new Subject<any>();
    inventory.getProductById.and.returnValues(late, new Subject<any>());
    component.onProductChange(PRODUCT);
    component.onProductChange({ ...PRODUCT, id: 8, uuid: 'prod-2' });
    late.next({ success: true, message: '', result: { variants: [{ uuid: 'v-9', sku: 'OLD', variantName: '' }] } });
    expect(component.variantOptions).toEqual([]);
  });

  it('searches active customers only, by what was typed', async () => {
    await show('order-register');

    component.searchCustomers({ query: 'acm' } as any);

    expect(partners.getPartners).toHaveBeenCalledOnceWith({ isCustomer: true, active: true, search: 'acm', pageSize: 20 });
    expect(component.customerSuggestions).toEqual([CUSTOMER]);
    partners.getPartners.and.returnValue(throwError(() => ({ status: 500 })));
    component.searchCustomers({ query: 'x' } as any);
    expect(component.customerSuggestions).toEqual([]);
  });

  it('shows a filter for each thing the report filters by', async () => {
    for (const definition of SALES_REPORTS) {
      await create();
      fixture.componentRef.setInput('definition', definition);
      fixture.detectChanges();

      const shown = Array.from(fixture.nativeElement.querySelectorAll('[data-filter]')).map((e: any) => e.getAttribute('data-filter'));
      expect(shown).withContext(definition.code).toEqual(definition.filters.map(f => f.id));
    }
  });

  it('marks the filters a report cannot run without', async () => {
    await show('customer-ledger');

    const required = fixture.nativeElement.querySelector('[data-filter="partnerId"] .required');
    expect(required).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-filter="dateFrom"] .required')).toBeNull();
  });

  it('says what a report means and leaves out, above its filters', async () => {
    await show('sales-vs-purchase');

    expect(query('note')!.textContent).toContain('Uncosted revenue');
    expect(fixture.nativeElement.querySelector('.code').textContent.trim()).toBe('R8');
  });

  it('has a sample for every report, so none goes untested', () => {
    expect(SALES_REPORTS.every(r => !!SAMPLE[r.key])).toBeTrue();
  });
});
