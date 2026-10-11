import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { CustomerPosSectionComponent } from './customer-pos-section.component';
import { PosAdminService } from '../../../services/pos-admin.service';
import { fail400, ok } from '../pos-fixtures.testing';

/** A38 §17.7.6 — the customer's POS settings; the walk-in customer can never buy on credit. */
describe('CustomerPosSectionComponent (A38)', () => {
  let fixture: ComponentFixture<CustomerPosSectionComponent>;
  let component: CustomerPosSectionComponent;
  let el: HTMLElement;
  let pos: jasmine.SpyObj<PosAdminService>;

  async function setup(isWalkIn: boolean, settings: unknown = { allowPosCredit: true, offlineCreditLimitOverride: 5000,
                                                                maxSingleCreditOrder: 2000, requiresIdForCredit: true, notes: 'VIP' }) {
    pos = jasmine.createSpyObj<PosAdminService>('PosAdminService', ['getCustomerSettings', 'saveCustomerSettings']);
    pos.getCustomerSettings.and.returnValue(ok(settings));
    pos.saveCustomerSettings.and.callFake((_u, req) => ok(req));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CustomerPosSectionComponent],
      providers: [provideNoopAnimations(), { provide: PosAdminService, useValue: pos }]
    }).compileComponents();
    fixture = TestBed.createComponent(CustomerPosSectionComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('isWalkIn', isWalkIn);
    fixture.componentRef.setInput('customerUuid', 'c-1');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('loads and saves the settings', async () => {
    await setup(false);
    expect(pos.getCustomerSettings).toHaveBeenCalledWith('c-1');
    expect(component.settings.allowPosCredit).toBeTrue();
    component.settings.notes = '  Pays monthly ';
    component.save();
    expect(pos.saveCustomerSettings).toHaveBeenCalledWith('c-1', {
      allowPosCredit: true, offlineCreditLimitOverride: 5000, maxSingleCreditOrder: 2000, requiresIdForCredit: true, notes: 'Pays monthly'
    });
  });

  it('credit off clears the credit-only fields on save', async () => {
    await setup(false);
    component.settings.allowPosCredit = false;
    component.save();
    expect(pos.saveCustomerSettings).toHaveBeenCalledWith('c-1', jasmine.objectContaining({
      allowPosCredit: false, offlineCreditLimitOverride: null, maxSingleCreditOrder: null, requiresIdForCredit: false
    }));
  });

  it('walk-in: allow credit is disabled, explained and always sent false; a refusal is shown verbatim', async () => {
    await setup(true);
    expect(component.settings.allowPosCredit).toBeFalse();
    expect(q('cpos-walkin-note')!.textContent).toContain('Walk-in customers cannot buy on credit.');
    const box = q('cpos-allow')!.querySelector('input') as HTMLInputElement;
    expect(box.disabled).toBeTrue();
    component.settings.allowPosCredit = true;   // forced in code: still false on the wire
    pos.saveCustomerSettings.and.returnValue(fail400('Walk-In customers cannot use credit.'));
    component.save();
    expect(pos.saveCustomerSettings.calls.mostRecent().args[1].allowPosCredit).toBeFalse();
    expect(component.error).toBe('Walk-In customers cannot use credit.');
  });
});
