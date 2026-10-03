import { signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { QboSyncStatusStore } from './qbo-sync-status.store';
import { SalesInvoiceListComponent } from '../../../pages/finance/sales-invoices/sales-invoice-list/sales-invoice-list.component';
import { PartnerListComponent } from '../../../pages/suppliers/partner-list/partner-list.component';
import { SalesInvoiceService } from '../../../services/sales-invoice.service';
import { LogisticsService } from '../../../services/logistics.service';
import { BusinessPartnerService } from '../../../services/business-partner.service';
import { AuthService } from '../../../pages/service/auth.service';
import { TenantService } from '../../../pages/service/tenant.service';
import { environment } from '../../../../environments/environment';

// The sync badge on real host pages: the QuickBooks column appears only when the integration is
// available, and a whole page of rows costs one status lookup per kind.

const BASE = `${environment.apiUrl}/integrations/quickbooks`;
const CONNECTED = { status: 'Live', isConnected: true, appConfigured: true, environment: 'Sandbox', mode: 'Live', reconnectSoon: false };

function ok<T>(result: T) {
  return { success: true, message: '', result };
}

describe('QuickBooks sync badge placements', () => {
  let http: HttpTestingController;
  let features: ReturnType<typeof signal<string[]>>;
  const permissions = ['INTEGRATION_VIEW', 'SALES_INVOICE_VIEW', 'SUPPLIER_VIEW'];

  function baseProviders() {
    return [
      provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
      { provide: TenantService, useValue: { hasFeature: (c: string) => features().includes(c), tenant: signal(null) } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } }
    ];
  }

  function headers(fixture: ComponentFixture<unknown>): string[] {
    return Array.from(fixture.nativeElement.querySelectorAll('th') as NodeListOf<HTMLElement>).map(th => th.textContent!.trim());
  }

  function badges(fixture: ComponentFixture<unknown>): HTMLElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('[data-testid="qbo-sync-badge"]'));
  }

  beforeEach(() => { features = signal<string[]>(['MODULE_INTEGRATION']); });
  afterEach(() => http.verify());

  describe('sales invoice list', () => {
    const rows = ['inv-1', 'inv-2', 'inv-3'].map((uuid, i) => ({
      uuid, invoiceNumber: `SINV-${i}`, saleOrderUuid: 'so', saleOrderNumber: 'SO', partnerId: 'p', partnerName: 'Acme',
      invoiceDate: '2026-09-20T00:00:00Z', dueDate: '2026-10-20T00:00:00Z', grandTotal: 1, amountPaid: 0, balanceDue: 1,
      status: 'ISSUED', currencyCode: 'PKR'
    }));

    function setup() {
      const invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getInvoices']);
      invoices.getInvoices.and.returnValue(of(ok({ data: rows, totalRecords: 3, page: 1, pageSize: 20, totalPages: 1 })) as any);
      TestBed.configureTestingModule({
        imports: [SalesInvoiceListComponent],
        providers: [
          ...baseProviders(),
          { provide: SalesInvoiceService, useValue: invoices },
          { provide: LogisticsService, useValue: jasmine.createSpyObj('LogisticsService', ['getDeliveries']) }
        ]
      });
      http = TestBed.inject(HttpTestingController);
      const fixture = TestBed.createComponent(SalesInvoiceListComponent);
      fixture.detectChanges();
      return fixture;
    }

    it('adds a QuickBooks column and makes one lookup for every invoice on the page', fakeAsync(() => {
      const fixture = setup();
      expect(headers(fixture)).toContain('QuickBooks');

      tick(QboSyncStatusStore.BATCH_WINDOW_MS);
      http.expectOne(`${BASE}/connection`).flush(ok(CONNECTED));
      const lookup = http.expectOne(`${BASE}/status/lookup`);
      expect(lookup.request.body).toEqual({ kind: 'SalesInvoice', externalIds: ['inv-1', 'inv-2', 'inv-3'] });
      lookup.flush(ok({ items: [{ kind: 'SalesInvoice', externalId: 'inv-1', state: 'Synced' }] }));
      fixture.detectChanges();

      expect(badges(fixture).map(b => b.textContent!.trim())).toEqual(['Synced', 'Not synced', 'Not synced']);
    }));

    it('has no QuickBooks column, and asks nothing, when the feature is off', fakeAsync(() => {
      features.set([]);
      const fixture = setup();
      expect(headers(fixture)).not.toContain('QuickBooks');
      tick(QboSyncStatusStore.BATCH_WINDOW_MS);
      http.expectNone(() => true);
      expect(badges(fixture).length).toBe(0);
    }));
  });

  describe('business partner list', () => {
    it('shows a customer and a vendor badge per partner, one lookup per kind', fakeAsync(() => {
      const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners', 'deletePartner']);
      partners.getPartners.and.returnValue(of(ok({
        data: [
          { uuid: 'bp-1', partnerCode: 'A', companyName: 'Both', partnerType: 'X', isVendor: true, isCustomer: true, isCarrier: false, isServiceProvider: false, isActive: true },
          { uuid: 'bp-2', partnerCode: 'B', companyName: 'Customer only', partnerType: 'X', isVendor: false, isCustomer: true, isCarrier: false, isServiceProvider: false, isActive: true },
          { uuid: 'bp-3', partnerCode: 'C', companyName: 'Carrier', partnerType: 'X', isVendor: false, isCustomer: false, isCarrier: true, isServiceProvider: false, isActive: true }
        ],
        totalRecords: 3, page: 1, pageSize: 20, totalPages: 1
      })) as any);

      TestBed.configureTestingModule({
        imports: [PartnerListComponent],
        providers: [...baseProviders(), { provide: BusinessPartnerService, useValue: partners }]
      });
      http = TestBed.inject(HttpTestingController);
      const fixture = TestBed.createComponent(PartnerListComponent);
      fixture.detectChanges();

      tick(QboSyncStatusStore.BATCH_WINDOW_MS);
      http.expectOne(`${BASE}/connection`).flush(ok(CONNECTED));
      const lookups = http.match(`${BASE}/status/lookup`);
      expect(lookups.map(r => r.request.body).sort((a, b) => a.kind.localeCompare(b.kind))).toEqual([
        { kind: 'Customer', externalIds: ['bp-1', 'bp-2'] },
        { kind: 'Vendor', externalIds: ['bp-1'] }
      ]);
      lookups.forEach(r => r.flush(ok({ items: [] })));
      fixture.detectChanges();

      expect(badges(fixture).map(b => b.textContent!.trim())).toEqual([
        'Customer: Not synced', 'Vendor: Not synced', 'Customer: Not synced'
      ]);
    }));
  });
});
