import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter, Router } from '@angular/router';
import { of } from 'rxjs';

import { CustomerListComponent } from './customer-list.component';
import { CustomerListItem, CustomerService } from '../../../services/customer.service';
import { CurrenciesService } from '../../../services/currencies.service';
import { AuthService } from '../../service/auth.service';
import { ModuleService } from '../../../services/module.service';

/** A37 §15.3 — the customer list: columns, filters, sort, New customer, Export. */
describe('CustomerListComponent (A37)', () => {
  let fixture: ComponentFixture<CustomerListComponent>;
  let component: CustomerListComponent;
  let el: HTMLElement;
  let service: jasmine.SpyObj<CustomerService>;

  const ok = (result: unknown) => of({ success: true, message: '', result } as any);
  const rows: CustomerListItem[] = [
    { uuid: 'c-w', code: 'C-WALKIN', name: 'Walk-in customer', customerType: 'WALK_IN', creditLimit: 0, balance: 0, isActive: true, isSystem: true },
    { uuid: 'c-1', code: 'C-00001', name: 'Acme', customerType: 'COMPANY', phone: '0300-1', email: 'a@acme.pk', creditLimit: 50000,
      balance: 1234.5, currencyCode: 'PKR', isActive: true, isSystem: false }
  ];

  async function setup(perms: string[] = ['CUSTOMER_VIEW', 'CUSTOMER_CREATE']) {
    service = jasmine.createSpyObj<CustomerService>('CustomerService', ['getCustomers', 'getAllCustomers', 'createCustomer']);
    service.getCustomers.and.returnValue(ok({ data: rows, totalRecords: 2, page: 1, pageSize: 25, totalPages: 1 }));
    service.getAllCustomers.and.returnValue(of(rows));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CustomerListComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: CustomerService, useValue: service },
        { provide: CurrenciesService, useValue: { getAll: () => ok([]) } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c) } },
        { provide: ModuleService, useValue: { isFeatureEnabled: () => true } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(CustomerListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('loads active customers first and shows the eight columns', async () => {
    await setup();
    expect(service.getCustomers).toHaveBeenCalledWith(jasmine.objectContaining({ status: 'ACTIVE', page: 1, pageSize: 25 }));
    const headers = Array.from(el.querySelectorAll('th')).map(h => h.textContent!.trim());
    expect(headers).toEqual(['Code', 'Name', 'Type', 'Phone', 'Email', 'Credit limit', 'Balance', 'Status']);
    const body = Array.from(el.querySelectorAll('[data-testid="customer-row"]'));
    expect(body.length).toBe(2);
    expect(body[0].textContent).toContain('Walk-in');
    expect(body[0].textContent).toContain('System');
    expect(body[1].textContent).toContain('C-00001');
    expect(body[1].textContent).toContain('50,000.00');
    expect(body[1].textContent).toContain('1,234.50');
    expect(q('customer-count')!.textContent).toContain('2');
  });

  it('filters by status chip and type, and sends the table sort', async () => {
    await setup();
    q('status-chip-INACTIVE')!.click();
    expect(service.getCustomers).toHaveBeenCalledWith(jasmine.objectContaining({ status: 'INACTIVE', page: 1 }));
    component.type = 'INDIVIDUAL';
    component.applyFilters();
    expect(service.getCustomers).toHaveBeenCalledWith(jasmine.objectContaining({ type: 'INDIVIDUAL', status: 'INACTIVE' }));
    component.onLazyLoad({ first: 25, rows: 25, sortField: 'balance', sortOrder: -1 });
    expect(service.getCustomers).toHaveBeenCalledWith(jasmine.objectContaining({ page: 2, sortField: 'balance', sortOrder: 'desc' }));
    component.onLazyLoad({ first: 0, rows: 25, sortField: 'phone', sortOrder: 1 });
    expect(service.getCustomers.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ sortField: null, sortOrder: null }));
  });

  it('New customer only with CUSTOMER_CREATE; it opens the dialog, and a save opens the new customer', async () => {
    await setup(['CUSTOMER_VIEW']);
    expect(q('new-customer')).toBeNull();
    await setup();
    expect(q('new-customer')).not.toBeNull();
    const nav = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    component.onCreated('c-9');
    expect(nav).toHaveBeenCalledWith(['/portal/pages/customers', 'c-9']);
  });

  it('Export writes a CSV of every customer matching the filter', async () => {
    await setup();
    const download = spyOn(component, 'download');
    component.search = 'acme';
    component.exportCsv();
    expect(service.getAllCustomers).toHaveBeenCalledWith(jasmine.objectContaining({ search: 'acme', status: 'ACTIVE' }));
    const [csv, name] = download.calls.mostRecent().args;
    expect(csv.split('\r\n').length).toBe(3);
    expect(csv).toContain('C-00001,Acme,Company');
    expect(name).toMatch(/^customers-\d{4}-\d{2}-\d{2}\.csv$/);
  });
});
