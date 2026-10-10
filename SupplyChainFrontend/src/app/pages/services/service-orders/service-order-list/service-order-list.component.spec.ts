import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { ServiceOrderListComponent } from './service-order-list.component';
import { ServiceOrderListItem, ServiceOrderService } from '../../../../services/service-order.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { UserService } from '../../../../services/user.service';
import { AuthService } from '../../../service/auth.service';

const ok = <T>(result: T) => of({ success: true, message: '', result } as any);

function item(overrides: Partial<ServiceOrderListItem> = {}): ServiceOrderListItem {
  return {
    uuid: 'so-1', serviceNumber: 'SVC-2026-0001', serviceProductName: 'AC installation', customerUuid: 'c-1', customerName: 'Cool Air Ltd',
    scheduledDate: '2026-10-12', scheduledTime: '09:30:00', assignedUserId: 7, assignedUserName: 'Usman Khan',
    status: 'WAITING', priority: 2, materialReadiness: 'SHORTAGE', quantity: 1, ...overrides
  };
}

// A36-P2-12 — the service order list report.
describe('ServiceOrderListComponent', () => {
  let fixture: ComponentFixture<ServiceOrderListComponent>;
  let component: ServiceOrderListComponent;
  let service: jasmine.SpyObj<ServiceOrderService>;
  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  beforeEach(async () => {
    service = jasmine.createSpyObj<ServiceOrderService>('ServiceOrderService', ['getList']);
    service.getList.and.returnValue(ok({ data: [item()], totalRecords: 1, page: 1, pageSize: 25, totalPages: 1 }));
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(ok({ data: [{ uuid: 'c-1', companyName: 'Cool Air Ltd', partnerCode: 'C001' }], totalRecords: 1 }));
    const users = jasmine.createSpyObj<UserService>('UserService', ['getUsers']);
    users.getUsers.and.returnValue(ok({ items: [] }));

    await TestBed.configureTestingModule({
      imports: [ServiceOrderListComponent],
      providers: [
        provideRouter([]), provideNoopAnimations(),
        { provide: ServiceOrderService, useValue: service },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: UserService, useValue: users },
        { provide: AuthService, useValue: { hasPermission: (c: string) => c === 'SERVICE_ORDER_CREATE', getUserData: () => ({ userId: 7, firstName: 'Usman', lastName: 'Khan' }) } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ServiceOrderListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('loads 25 a page and shows the row with its status pill and readiness icon', () => {
    expect(service.getList).toHaveBeenCalled();
    expect(service.getList.calls.mostRecent().args[0]!.pageSize).toBe(25);
    const row = q('order-SVC-2026-0001')!;
    expect(text(row)).toContain('Cool Air Ltd');
    expect(text(row)).toContain('09:30');
    const pill = row.querySelector('[data-testid="status"]')!;
    expect(text(pill)).toBe('Waiting');
    expect(pill.classList).toContain('wn');
    expect(row.querySelector('[data-testid="readiness"]')!.getAttribute('data-readiness')).toBe('SHORTAGE');
  });

  it('offers a chip for each of the nine statuses, and they combine', () => {
    for (const s of ['DRAFT', 'PLANNED', 'MATERIAL_PENDING', 'WAITING', 'READY', 'IN_PROGRESS', 'COMPLETED', 'CLOSED', 'CANCELLED']) {
      expect(q('status-chip-' + s)).withContext(s).not.toBeNull();
    }
    q('status-chip-WAITING')!.click();
    q('status-chip-READY')!.click();
    expect(service.getList.calls.mostRecent().args[0]!.status).toEqual(['WAITING', 'READY']);
    q('status-chip-ALL')!.click();
    expect(service.getList.calls.mostRecent().args[0]!.status).toEqual([]);
  });

  it('sends customer, assignee, priority and the date range', () => {
    component.customerUuid = 'c-1';
    component.assignedUserId = 7;
    component.priority = 3;
    component.dateRange = [new Date(2026, 9, 1), new Date(2026, 9, 31)];
    component.onFilterChange();
    const f = service.getList.calls.mostRecent().args[0]!;
    expect(f.customerUuid).toBe('c-1');
    expect(f.assignedUserId).toBe(7);
    expect(f.priority).toBe(3);
    expect(f.fromDate).toBe('2026-10-01');
    expect(f.toDate).toBe('2026-10-31');
    expect(f.page).toBe(1);
  });

  it('waits for the end of a date range before loading', () => {
    const calls = service.getList.calls.count();
    component.dateRange = [new Date(2026, 9, 1), null as any];
    component.onDateChange();
    expect(service.getList.calls.count()).toBe(calls);
  });

  it('loads the customer picker from customers only', () => {
    expect(component.customerOptions).toEqual([{ label: 'Cool Air Ltd (C001)', value: 'c-1' }]);
  });

  it('shows "New service order" to someone allowed to create', () => {
    expect(q('new')).not.toBeNull();
  });
});
