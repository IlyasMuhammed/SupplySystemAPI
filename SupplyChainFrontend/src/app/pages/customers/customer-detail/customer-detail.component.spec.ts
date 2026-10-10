import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { CustomerDetailComponent } from './customer-detail.component';
import { CustomerDetail, CustomerService } from '../../../services/customer.service';
import { SaleOrderService } from '../../../services/sale-order.service';
import { CurrenciesService } from '../../../services/currencies.service';
import { AuthService } from '../../service/auth.service';
import { ModuleService } from '../../../services/module.service';

/** A37 §15.4 — the customer object page: Details, Financial, Orders; activate / deactivate; walk-in rules. */
describe('CustomerDetailComponent (A37)', () => {
  let fixture: ComponentFixture<CustomerDetailComponent>;
  let component: CustomerDetailComponent;
  let el: HTMLElement;
  let service: jasmine.SpyObj<CustomerService>;
  let orders: jasmine.SpyObj<SaleOrderService>;

  const ok = (result: unknown) => of({ success: true, message: '', result } as any);
  const acme: CustomerDetail = {
    uuid: 'c-1', code: 'C-00001', name: 'Acme', customerType: 'COMPANY', phone: '0300-1', email: 'a@acme.pk', creditLimit: 1000,
    balance: 1500, currencyCode: 'PKR', isActive: true, isSystem: false, paymentTermsDays: 30, isVendor: false,
    addressLine1: '1 Mall Rd', city: 'Lahore', createdAt: '2026-10-01T10:00:00Z', modifiedAt: null
  };
  const walkIn: CustomerDetail = { ...acme, uuid: 'c-w', code: 'C-WALKIN', name: 'Walk-in customer', customerType: 'WALK_IN', isSystem: true, creditLimit: 0 };

  async function setup(customer: CustomerDetail, opts: { perms?: string[]; features?: string[] } = {}) {
    const perms = opts.perms ?? ['CUSTOMER_VIEW', 'CUSTOMER_EDIT', 'CUSTOMER_DEACTIVATE', 'CUSTOMER_LEDGER_VIEW', 'SALE_ORDER_VIEW'];
    const features = opts.features ?? ['FEATURE_CREDIT_MANAGEMENT'];
    service = jasmine.createSpyObj<CustomerService>('CustomerService', ['getCustomer', 'getBalance', 'setStatus', 'updateCustomer']);
    service.getCustomer.and.returnValue(ok(customer));
    service.getBalance.and.returnValue(ok({ balance: 1500, currencyCode: 'PKR', overdue: 200 }));
    service.setStatus.and.returnValue(ok(null));
    orders = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', ['getSaleOrders']);
    orders.getSaleOrders.and.returnValue(ok({ data: [
      { uuid: 'so-1', soNumber: 'SO-0001', orderDate: '2026-10-02', grandTotal: 900, currencyCode: 'PKR', status: 'CONFIRMED' }
    ], totalRecords: 14 }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CustomerDetailComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => customer.uuid } } } },
        { provide: CustomerService, useValue: service },
        { provide: SaleOrderService, useValue: orders },
        { provide: CurrenciesService, useValue: { getAll: () => ok([]) } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c) } },
        { provide: ModuleService, useValue: { isFeatureEnabled: (c: string) => features.includes(c) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(CustomerDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('renders the header, Details and Financial with the balance endpoint and a ledger link', async () => {
    await setup(acme);
    expect(service.getCustomer).toHaveBeenCalledWith('c-1');
    expect(q('customer-title')!.textContent).toContain('C-00001');
    expect(q('customer-title')!.textContent).toContain('Acme');
    expect(q('section-details')!.textContent).toContain('1 Mall Rd, Lahore');
    expect(q('financial-balance')!.textContent).toContain('1,500.00 PKR');
    expect(q('kpi-balance')!.textContent).toContain('200.00 overdue');
    expect(component.overCredit).toBeTrue();
    expect(q('ledger-link')!.getAttribute('href')).toContain('/portal/pages/finance/customer-ledger/c-1');
    expect(component.sections.map(s => s.label)).toEqual(['Details', 'Financial', 'Orders']);
  });

  it('credit management off: the limit is shown as not enforced', async () => {
    await setup(acme, { features: [] });
    expect(q('credit-off-note')!.textContent).toContain('switched off');
    expect(component.overCredit).toBeFalse();
  });

  it('Orders lists the customer\'s sale orders through the partner filter', async () => {
    await setup(acme);
    expect(orders.getSaleOrders).toHaveBeenCalledWith(jasmine.objectContaining({ partnerId: 'c-1', pageSize: 10 }));
    expect(q('orders-table')!.textContent).toContain('SO-0001');
    expect(q('section-orders')!.textContent).toContain('latest 1 of 14');

    await setup(acme, { perms: ['CUSTOMER_VIEW'] });
    expect(orders.getSaleOrders).not.toHaveBeenCalled();
    expect(q('orders-no-access')).not.toBeNull();
    expect(q('edit-customer')).toBeNull();
    expect(q('deactivate-customer')).toBeNull();
  });

  it('deactivate asks first, then PATCHes the status; activate does not ask', async () => {
    await setup(acme);
    const confirm = spyOn((component as any).confirm, 'confirm').and.callFake((c: any) => c.accept());
    component.toggleStatus();
    expect(confirm).toHaveBeenCalled();
    expect(service.setStatus).toHaveBeenCalledWith('c-1', false);

    await setup({ ...acme, isActive: false });
    expect(q('activate-customer')).not.toBeNull();
    component.toggleStatus();
    expect(service.setStatus).toHaveBeenCalledWith('c-1', true);
  });

  it('the walk-in customer cannot be deactivated; a server refusal is shown as it comes', async () => {
    await setup(walkIn);
    expect(component.isWalkIn).toBeTrue();
    const btn = q('deactivate-customer')!.querySelector('button')!;
    expect(btn.disabled).toBeTrue();
    expect(q('customer-system')).not.toBeNull();

    service.setStatus.and.returnValue(throwError(() => ({ status: 400, error: { message: 'The walk-in customer cannot be deactivated.' } })));
    const toast = spyOn((component as any).messages, 'add');
    component.applyStatus(false);
    expect(toast).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'The walk-in customer cannot be deactivated.' }));
  });

  it('a missing customer shows the error', async () => {
    service = undefined as any;
    await setup(acme);
    service.getCustomer.and.returnValue(throwError(() => ({ status: 404, error: {} })));
    component.load();
    fixture.detectChanges();
    expect(q('customer-load-error')!.textContent).toContain('Customer not found.');
  });
});
