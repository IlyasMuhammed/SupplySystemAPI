import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SaleQuotationListComponent } from './sale-quotation-list.component';
import { SALE_QUOTATION_STATUS_SEVERITY } from '../sale-quotation.shared';
import {
  SalesPreorderService, SaleQuotationListItem, SALE_QUOTATION_STATUSES
} from '../../../../services/sales-preorder.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { AuthService } from '../../../service/auth.service';

function item(overrides: Partial<SaleQuotationListItem> = {}): SaleQuotationListItem {
  return {
    uuid: 'sq-1', quotationNumber: 'SQ-2026-00015', partnerId: 'p-1', partnerName: 'GlobalTech Co',
    customerReference: null, sourceInquiryUuid: 'inq-42', sourceInquiryNumber: 'INQ-2026-00042',
    currencyId: 'cur-usd', currencyCode: 'USD', validFrom: '2026-10-03T00:00:00', validTo: '2026-10-31T00:00:00',
    status: 'DRAFT', grandTotal: 6470, lineCount: 6, sentAt: null, createdDate: '2026-10-03T08:00:00Z',
    ...overrides
  };
}

describe('SaleQuotationListComponent (A32-PC-10)', () => {
  let fixture: ComponentFixture<SaleQuotationListComponent>;
  let component: SaleQuotationListComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;
  let partners: jasmine.SpyObj<BusinessPartnerService>;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  async function setup(items: SaleQuotationListItem[] = [
    item(),
    item({ uuid: 'sq-2', quotationNumber: 'SQ-2026-00016', partnerName: 'Acme Ltd', sourceInquiryUuid: null,
           sourceInquiryNumber: null, status: 'SENT', grandTotal: 1200.5, currencyCode: 'PKR' })
  ]) {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['getQuotations']);
    service.getQuotations.and.returnValue(of({
      success: true, message: '',
      result: { data: items, totalRecords: items.length, page: 1, pageSize: 20, totalPages: 1 }
    } as any));
    partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(of({ success: true, message: '', result: { data: [{ uuid: 'p-1', companyName: 'GlobalTech Co' }], totalRecords: 1 } } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleQuotationListComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesPreorderService, useValue: service },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleQuotationListComponent);
    component = fixture.componentInstance;
  }

  function render() {
    fixture.detectChanges();
    component.onPageChange({ first: 0, rows: 20 });
    fixture.detectChanges();
  }

  function cells(testId: string): string[] {
    return Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`)).map((e: any) => e.textContent.trim());
  }

  beforeEach(() => { permissions = ['SALE_QUOTATION_VIEW']; });

  it('loads page one through the table and renders number, customer, validity, status and total', async () => {
    await setup();
    render();

    expect(service.getQuotations).toHaveBeenCalledWith(jasmine.objectContaining({ page: 1, pageSize: 20 }));
    expect(cells('number')).toEqual(['SQ-2026-00015', 'SQ-2026-00016']);
    expect(cells('customer')).toEqual(['GlobalTech Co', 'Acme Ltd']);
    expect(cells('valid-from')[0]).toBe('03 Oct 2026');
    expect(cells('valid-to')[0]).toBe('31 Oct 2026');
    expect(cells('status')).toEqual(['Draft', 'Sent']);
    expect(cells('grand-total')[0]).toContain('6,470.00');
    expect(cells('grand-total')[0]).toContain('USD');
    expect(cells('grand-total')[1]).toContain('1,200.50');
    expect(component.isLoading).toBeFalse();
  });

  it('shows a validity date as the calendar day the server sent, wherever the reader is', async () => {
    await setup([item({ validFrom: '2026-10-01T00:00:00Z', validTo: '2026-10-31T00:00:00Z' })]);
    render();
    expect(cells('valid-from')).toEqual(['01 Oct 2026']);
    expect(cells('valid-to')).toEqual(['31 Oct 2026']);
  });

  it('links the source inquiry when there is one, and shows a dash when there is not', async () => {
    await setup();
    render();

    const links = fixture.nativeElement.querySelectorAll('[data-testid="source-inquiry"] a');
    expect(links.length).toBe(1);
    expect(links[0].textContent.trim()).toBe('INQ-2026-00042');
    expect(links[0].getAttribute('href')).toBe('/portal/pages/sales/inquiries/inq-42');
    expect(cells('source-inquiry')[1]).toBe('—');
  });

  it('opens a quotation from its row', async () => {
    await setup();
    render();
    const view = fixture.nativeElement.querySelector('[data-testid="view-quotation"] a, a[data-testid="view-quotation"], [data-testid="view-quotation"]');
    expect(view).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="number"] a').getAttribute('href'))
      .toBe('/portal/pages/sales/quotations/sq-1');
  });

  // ── Filters ────────────────────────────────────────────────────────────────

  it('sends only the filters that are set', async () => {
    await setup();
    component.load();

    const filter = service.getQuotations.calls.mostRecent().args[0]!;
    expect(filter.status).toBeUndefined();
    expect(filter.partnerId).toBeUndefined();
    expect(filter.validToFrom).toBeUndefined();
    expect(filter.validToTo).toBeUndefined();
    expect(filter.search).toBeUndefined();
  });

  it('filters by status from the first page', async () => {
    await setup();
    component.currentPage = 3;
    component.selectedStatus = 'SENT';
    component.onFilterChange();

    expect(service.getQuotations).toHaveBeenCalledWith(jasmine.objectContaining({ page: 1, status: 'SENT' }));
  });

  it('offers every status the server can return as a filter, each with a severity', async () => {
    await setup();
    expect(component.statusOptions.map(o => o.value)).toEqual(['', ...SALE_QUOTATION_STATUSES]);
    for (const s of SALE_QUOTATION_STATUSES) expect(SALE_QUOTATION_STATUS_SEVERITY[s]).withContext(s).toBeDefined();
  });

  it('filters by a customer picked from the list, and clears it', async () => {
    await setup();
    component.searchCustomers({ query: 'glo' } as any);
    expect(partners.getPartners).toHaveBeenCalledWith(jasmine.objectContaining({ isCustomer: true, search: 'glo' }));
    expect(component.customerSuggestions.length).toBe(1);

    component.selectedCustomer = { uuid: 'p-1', companyName: 'GlobalTech Co' } as any;
    component.onFilterChange();
    expect(service.getQuotations.calls.mostRecent().args[0]!.partnerId).toBe('p-1');

    component.selectedCustomer = 'typed text, not picked' as any;
    component.onFilterChange();
    expect(service.getQuotations.calls.mostRecent().args[0]!.partnerId).withContext('only a picked customer filters').toBeUndefined();
  });

  it('filters by the valid-until range as calendar days, with no time-zone shift', async () => {
    await setup();
    component.validToFrom = new Date(2026, 9, 1);   // 1 Oct, local midnight
    component.validToTo = new Date(2026, 9, 31, 23, 30);
    component.onFilterChange();

    const filter = service.getQuotations.calls.mostRecent().args[0]!;
    expect(filter.validToFrom).toBe('2026-10-01');
    expect(filter.validToTo).toBe('2026-10-31');
  });

  it('searches by number after a pause, from the first page', async () => {
    jasmine.clock().install();
    try {
      await setup();
      component.currentPage = 2;
      component.searchText = 'SQ-2026';
      component.onSearchChange();
      expect(service.getQuotations).not.toHaveBeenCalled();
      jasmine.clock().tick(450);
      expect(service.getQuotations).toHaveBeenCalledWith(jasmine.objectContaining({ page: 1, search: 'SQ-2026' }));
    } finally {
      jasmine.clock().uninstall();
    }
  });

  it('resets every filter', async () => {
    await setup();
    component.selectedStatus = 'SENT';
    component.selectedCustomer = { uuid: 'p-1', companyName: 'GlobalTech Co' } as any;
    component.validToFrom = new Date(2026, 9, 1);
    component.validToTo = new Date(2026, 9, 31);
    component.searchText = 'x';
    component.resetFilters();

    const filter = service.getQuotations.calls.mostRecent().args[0]!;
    expect(filter).toEqual(jasmine.objectContaining({ page: 1 }));
    expect(filter.status).toBeUndefined();
    expect(filter.partnerId).toBeUndefined();
    expect(filter.validToFrom).toBeUndefined();
    expect(filter.search).toBeUndefined();
  });

  it('clears the loading flag and empties the list when the request fails', async () => {
    await setup();
    service.getQuotations.and.returnValue(throwError(() => ({ status: 500 })));
    component.load();

    expect(component.isLoading).toBeFalse();
    expect(component.quotations).toEqual([]);
  });

  // ── Permissions ────────────────────────────────────────────────────────────

  it('offers "+ New Quotation" only to someone who may create one', async () => {
    await setup();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="new-quotation"]')).toBeNull();

    permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_CREATE'];
    await setup();
    fixture.detectChanges();
    const button = fixture.nativeElement.querySelector('[data-testid="new-quotation"]');
    expect(button).not.toBeNull();
    expect(button.textContent).toContain('New Quotation');
  });

  it('lets a draft be edited by someone who may edit, and no other quotation', async () => {
    permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_EDIT'];
    await setup();
    expect(component.canEdit(item({ status: 'DRAFT' }))).toBeTrue();
    expect(component.canEdit(item({ status: 'SENT' }))).toBeFalse();

    permissions = ['SALE_QUOTATION_VIEW'];
    expect(component.canEdit(item({ status: 'DRAFT' }))).withContext('viewing is not editing').toBeFalse();
  });
});
