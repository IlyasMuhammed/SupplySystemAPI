import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';
import { throwError } from 'rxjs';

import { POLICY_CONFLICT, PosStoreReturnsComponent } from './store-returns.component';
import { PosReturnsAdminService } from '../../../services/pos-returns-admin.service';
import { effectivePolicyFixture, fail400, ok } from '../pos-fixtures.testing';

/** A39 — the store page's Returns tab: org default vs store-specific policy, PUT / DELETE, 409, messages verbatim. */
describe('PosStoreReturnsComponent (A39)', () => {
  let fixture: ComponentFixture<PosStoreReturnsComponent>;
  let component: PosStoreReturnsComponent;
  let el: HTMLElement;
  let api: jasmine.SpyObj<PosReturnsAdminService>;
  let confirm: ConfirmationService;

  async function setup(policy: any, canManage = true) {
    api = jasmine.createSpyObj<PosReturnsAdminService>('PosReturnsAdminService', ['getStorePolicy', 'saveStorePolicy', 'deleteStorePolicy']);
    api.getStorePolicy.and.returnValue(policy instanceof Error ? throwError(() => ({ status: 400, error: { message: policy.message } })) : ok(policy));
    api.saveStorePolicy.and.returnValue(ok(null));
    api.deleteStorePolicy.and.returnValue(ok(null));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PosStoreReturnsComponent],
      providers: [provideNoopAnimations(), provideRouter([]), ConfirmationService, MessageService, { provide: PosReturnsAdminService, useValue: api }]
    }).compileComponents();
    confirm = TestBed.inject(ConfirmationService);
    fixture = TestBed.createComponent(PosStoreReturnsComponent);
    component = fixture.componentInstance;
    component.storeUuid = 's-1';
    component.storeName = 'Mall store';
    component.canManage = canManage;
    component.ngOnChanges({ storeUuid: {} as any });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('a store following the org default shows it read only; switching to store-specific PUTs a copy without rowVersion', async () => {
    await setup(effectivePolicyFixture({ restockingFeePct: 10 }));
    expect(api.getStorePolicy).toHaveBeenCalledWith('s-1');
    expect(component.mode).toBe('ORG');
    expect(q('org-summary')!.textContent).toContain('30-day returns');
    expect(q('org-summary')!.textContent).toContain('10% restocking fee');
    expect(q('use-default')).toBeNull();   // nothing to delete

    component.mode = 'STORE';
    fixture.detectChanges();
    expect(q('store-new-hint')).not.toBeNull();
    expect(component.model!.name).toBe('Mall store return policy');
    expect(component.model!.restockingFeePct).toBe(10);
    component.model!.returnWindowDays = 14;
    component.save();
    const [store, body] = api.saveStorePolicy.calls.mostRecent().args;
    expect(store).toBe('s-1');
    expect(body.returnWindowDays).toBe(14);
    expect('rowVersion' in body).toBeFalse();
    expect(api.getStorePolicy).toHaveBeenCalledTimes(2);   // reloaded
  });

  it('a store with its own policy edits it with rowVersion; 409 and 400 messages are shown', async () => {
    await setup(effectivePolicyFixture({ source: 'STORE', storeUuid: 's-1', name: 'Mall rules', rowVersion: 'rv9' }));
    expect(component.mode).toBe('STORE');
    expect(q('store-new-hint')).toBeNull();
    api.saveStorePolicy.and.returnValue(throwError(() => ({ status: 409, error: {} })));
    component.save();
    expect(api.saveStorePolicy.calls.mostRecent().args[1].rowVersion).toBe('rv9');
    expect(component.error).toBe(POLICY_CONFLICT);
    api.saveStorePolicy.and.returnValue(fail400('Restocking fee must be between 0 and 100.'));
    component.save();
    fixture.detectChanges();
    expect(q('policy-error')!.textContent).toContain('Restocking fee must be between 0 and 100.');

    component.model!.restockingFeePct = 120;
    api.saveStorePolicy.calls.reset();
    component.save();
    expect(api.saveStorePolicy).not.toHaveBeenCalled();
    expect(component.error).toBe('The restocking fee must be between 0 and 100%.');
  });

  it('"Use organization default" asks, then DELETEs the store policy', async () => {
    await setup(effectivePolicyFixture({ source: 'STORE', storeUuid: 's-1' }));
    const ask = spyOn(confirm, 'confirm').and.callFake((c: any) => c.accept());
    component.mode = 'ORG';
    fixture.detectChanges();
    (q('use-default')!.querySelector('button') as HTMLButtonElement).click();
    expect(ask).toHaveBeenCalled();
    expect(api.deleteStorePolicy).toHaveBeenCalledWith('s-1');
  });

  it('read only without POS_RETURN_POLICY_MANAGE; "No return policy configured" is shown verbatim', async () => {
    await setup(effectivePolicyFixture({ source: 'STORE', storeUuid: 's-1' }), false);
    expect(q('save-store-policy')).toBeNull();
    expect((q('pol-name') as HTMLInputElement).disabled).toBeTrue();
    component.save();
    expect(api.saveStorePolicy).not.toHaveBeenCalled();

    await setup(new Error('No return policy configured. Contact your administrator.'));
    expect(q('returns-load-error')!.textContent).toContain('No return policy configured. Contact your administrator.');
    expect(component.mode).toBe('ORG');
  });
});
