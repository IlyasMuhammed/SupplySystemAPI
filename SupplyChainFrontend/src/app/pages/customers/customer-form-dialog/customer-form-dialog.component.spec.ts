import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { CustomerFormDialogComponent } from './customer-form-dialog.component';
import { CustomerDetail, CustomerService } from '../../../services/customer.service';
import { CurrenciesService } from '../../../services/currencies.service';

/** A37 §15.3/§15.4 — the customer create/edit dialog (API-CONTRACT §5). */
describe('CustomerFormDialogComponent (A37)', () => {
  let fixture: ComponentFixture<CustomerFormDialogComponent>;
  let component: CustomerFormDialogComponent;
  let service: jasmine.SpyObj<CustomerService>;
  let saved: string[];

  const ok = (result: unknown) => of({ success: true, message: '', result } as any);
  const walkIn: CustomerDetail = {
    uuid: 'c-w', code: 'C-WALKIN', name: 'Walk-in customer', customerType: 'WALK_IN', creditLimit: 0, balance: 0, isActive: true,
    isSystem: true, paymentTermsDays: 0, isVendor: false, createdAt: ''
  };
  const company: CustomerDetail = { ...walkIn, uuid: 'c-1', code: 'C-00001', name: 'Acme', customerType: 'COMPANY', isSystem: false,
    creditLimit: 5000, rowVersion: 'AAAA' };

  async function setup(customer: CustomerDetail | null, creditEnabled: boolean) {
    service = jasmine.createSpyObj<CustomerService>('CustomerService', ['createCustomer', 'updateCustomer']);
    service.createCustomer.and.returnValue(ok('c-new'));
    service.updateCustomer.and.returnValue(ok(null));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CustomerFormDialogComponent],
      providers: [
        provideNoopAnimations(),
        { provide: CustomerService, useValue: service },
        { provide: CurrenciesService, useValue: { getAll: () => ok([{ id: 'cur-1', code: 'USD', name: 'US Dollar', symbol: '$' }]) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(CustomerFormDialogComponent);
    component = fixture.componentInstance;
    saved = [];
    component.saved.subscribe(u => saved.push(u));
    fixture.componentRef.setInput('customer', customer);
    fixture.componentRef.setInput('creditEnabled', creditEnabled);
    fixture.componentRef.setInput('visible', true);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const q = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  afterEach(() => fixture?.destroy());

  it('creates a customer: trimmed text, empty → null, and emits the new uuid', async () => {
    await setup(null, true);
    component.form.patchValue({ name: '  Acme  ', customerType: 'INDIVIDUAL', phone: ' ', email: 'a@b.c', creditLimit: 1000, paymentTermsDays: 30 });
    component.save();
    expect(service.createCustomer).toHaveBeenCalledWith(jasmine.objectContaining({
      name: 'Acme', customerType: 'INDIVIDUAL', phone: null, email: 'a@b.c', creditLimit: 1000, paymentTermsDays: 30
    }));
    expect(saved).toEqual(['c-new']);
  });

  it('refuses an empty name with the server\'s words, without a request', async () => {
    await setup(null, true);
    component.save();
    fixture.detectChanges();
    expect(service.createCustomer).not.toHaveBeenCalled();
    expect(q('customer-form-error')!.textContent).toContain('Name is required.');
  });

  it('shows the server\'s 400 message verbatim', async () => {
    await setup(company, true);
    service.updateCustomer.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Walk-in customers cannot have a credit limit.' } })));
    component.save();
    fixture.detectChanges();
    expect(q('customer-form-error')!.textContent!.trim()).toBe('Walk-in customers cannot have a credit limit.');
    expect(saved).toEqual([]);
  });

  it('leaves the credit limit out (and hides it) without FEATURE_CREDIT_MANAGEMENT', async () => {
    await setup(null, false);
    expect(q('credit-limit-field')).toBeNull();
    component.form.patchValue({ name: 'Acme' });
    expect('creditLimit' in component.payload()).toBeFalse();
  });

  it('an edit without credit management sends the stored limit back (PUT replaces every field)', async () => {
    await setup(company, false);
    component.form.patchValue({ creditLimit: 99 });
    expect(component.payload().creditLimit).toBe(5000);
  });

  it('edit sends rowVersion and updates by uuid', async () => {
    await setup(company, true);
    component.save();
    expect(service.updateCustomer).toHaveBeenCalledWith('c-1', jasmine.objectContaining({ name: 'Acme', creditLimit: 5000, rowVersion: 'AAAA' }));
    expect(saved).toEqual(['c-1']);
  });

  it('the walk-in customer: type locked to WALK_IN, credit limit 0', async () => {
    await setup(walkIn, true);
    expect(component.isWalkIn).toBeTrue();
    expect(component.form.get('customerType')!.disabled).toBeTrue();
    expect(component.typeOptions.filter(o => !o.disabled).map(o => o.value)).toEqual(['WALK_IN']);
    expect(q('walk-in-type-note')).not.toBeNull();
    expect(component.payload()).toEqual(jasmine.objectContaining({ customerType: 'WALK_IN', creditLimit: 0 }));
  });

  it('a new customer cannot be made a walk-in', async () => {
    await setup(null, true);
    expect(component.typeOptions.find(o => o.value === 'WALK_IN')!.disabled).toBeTrue();
    expect(component.typeOptions.find(o => o.value === 'COMPANY')!.disabled).toBeFalse();
  });
});
