import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SaleInquiryListComponent } from './sale-inquiry-list.component';
import { SalesPreorderService, SaleInquiryListItem } from '../../../../services/sales-preorder.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { AuthService } from '../../../service/auth.service';

function item(overrides: Partial<SaleInquiryListItem> = {}): SaleInquiryListItem {
  return {
    uuid: 'inq-1', inquiryNumber: 'INQ-2026-00042', partnerId: 'p-1', partnerName: 'GlobalTech Co',
    receivedDate: '2026-10-01T00:00:00', status: 'UNDER_REVIEW', lineCount: 5, pendingLineCount: 2,
    createdDate: '2026-10-01T08:00:00', ...overrides
  };
}

describe('SaleInquiryListComponent (A32-PB-08)', () => {
  let fixture: ComponentFixture<SaleInquiryListComponent>;
  let component: SaleInquiryListComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;
  let partners: jasmine.SpyObj<BusinessPartnerService>;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  async function setup(items: SaleInquiryListItem[] = [
    item(),
    item({ uuid: 'inq-2', inquiryNumber: 'INQ-2026-00041', partnerName: 'Meridian Ltd', status: 'QUOTED', lineCount: 3, pendingLineCount: 0 })
  ]) {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['getInquiries']);
    service.getInquiries.and.returnValue(of({
      success: true, message: '', result: { data: items, totalRecords: items.length, page: 1, pageSize: 20, totalPages: 1 }
    } as any));
    partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(of({ success: true, message: '', result: { data: [{ uuid: 'p-1', companyName: 'GlobalTech Co' }], totalRecords: 1 } } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleInquiryListComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesPreorderService, useValue: service },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleInquiryListComponent);
    component = fixture.componentInstance;
  }

  beforeEach(() => { permissions = ['SALE_INQUIRY_VIEW']; });

  it('loads page one through the table and renders number, customer, received, status and line count', async () => {
    await setup();
    fixture.detectChanges();
    component.onPageChange({ first: 0, rows: 20 });
    fixture.detectChanges();

    expect(service.getInquiries).toHaveBeenCalledWith(jasmine.objectContaining({ page: 1, pageSize: 20 }));
    const rows = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="inquiry-row"]')) as HTMLElement[];
    expect(rows.length).toBe(2);
    const first = rows[0].textContent!;
    expect(first).toContain('INQ-2026-00042');
    expect(first).toContain('GlobalTech Co');
    expect(first).toContain('01 Oct 2026');
    expect(first).toContain('Under Review');
    expect(rows[0].querySelector('[data-testid="line-count"]')!.textContent!.trim()).toContain('5');
  });

  it('paints the status badge in the spec colour', async () => {
    await setup();
    fixture.detectChanges();
    component.onPageChange({ first: 0, rows: 20 });
    fixture.detectChanges();

    const badges = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="status-badge"]')) as HTMLElement[];
    expect(badges[0].classList).toContain('st-orange');
    expect(badges[1].classList).toContain('st-purple');
  });

  it('shows a received date as the day it names, whatever the time zone', async () => {
    await setup([item({ receivedDate: '2026-10-01T00:00:00Z' })]);
    fixture.detectChanges();
    component.onPageChange({ first: 0, rows: 20 });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="received"]').textContent).toContain('01 Oct 2026');
  });

  it('sends only the filters that are set, the dates as the days picked', async () => {
    await setup();
    component.selectedStatus = 'REVIEW_COMPLETE';
    component.customer = { uuid: 'p-1', companyName: 'GlobalTech Co' } as any;
    component.receivedFrom = new Date(2026, 9, 1);
    component.receivedTo = null;
    component.load();

    const filter = service.getInquiries.calls.mostRecent().args[0]!;
    expect(filter.status).toBe('REVIEW_COMPLETE');
    expect(filter.partnerId).toBe('p-1');
    expect(filter.receivedFrom).toBe('2026-10-01');
    expect(filter.receivedTo).toBeUndefined();
    expect(filter.search).toBeUndefined();
  });

  it('reloads from the first page when a filter changes, and resets every filter', async () => {
    await setup();
    component.currentPage = 3;
    component.selectedStatus = 'DECLINED';
    component.onFilterChange();
    expect(service.getInquiries).toHaveBeenCalledWith(jasmine.objectContaining({ page: 1, status: 'DECLINED' }));

    component.receivedTo = new Date(2026, 9, 31);
    component.resetFilters();
    const filter = service.getInquiries.calls.mostRecent().args[0]!;
    expect([filter.status, filter.partnerId, filter.receivedFrom, filter.receivedTo]).toEqual([undefined, undefined, undefined, undefined]);
  });

  it('offers every inquiry status as a filter', async () => {
    await setup();
    expect(component.statusOptions.map(o => o.value))
      .toEqual(['', 'RECEIVED', 'UNDER_REVIEW', 'REVIEW_COMPLETE', 'QUOTED', 'DECLINED']);
  });

  it('searches customers among partners flagged as customers', async () => {
    await setup();
    component.searchCustomers({ query: 'glo' } as any);
    expect(partners.getPartners).toHaveBeenCalledWith(jasmine.objectContaining({ isCustomer: true, search: 'glo' }));
    expect(component.customerSuggestions.length).toBe(1);
  });

  it('clears the loading flag and empties the list when the request fails', async () => {
    await setup();
    service.getInquiries.and.returnValue(throwError(() => ({ status: 500 })));
    component.load();

    expect(component.isLoading).toBeFalse();
    expect(component.inquiries).toEqual([]);
  });

  it('offers "+ New Inquiry" only to someone who may create one', async () => {
    await setup();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="new-inquiry"]')).toBeNull();

    permissions = ['SALE_INQUIRY_VIEW', 'SALE_INQUIRY_CREATE'];
    await setup();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="new-inquiry"]')).not.toBeNull();
  });
});
