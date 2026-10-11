import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { throwError } from 'rxjs';

import { PosReturnPolicyPageComponent } from './return-policy-page.component';
import { PosReturnsAdminService } from '../../../services/pos-returns-admin.service';
import { POLICY_CONFLICT } from '../store-returns/store-returns.component';
import { ok, policyFixture } from '../pos-fixtures.testing';

/** A39 — `/pos/return-policy`: the organization default (PUT default + rowVersion, 409) and the store overrides. */
describe('PosReturnPolicyPageComponent (A39)', () => {
  let fixture: ComponentFixture<PosReturnPolicyPageComponent>;
  let component: PosReturnPolicyPageComponent;
  let el: HTMLElement;
  let api: jasmine.SpyObj<PosReturnsAdminService>;

  async function setup(policies: any[]) {
    api = jasmine.createSpyObj<PosReturnsAdminService>('PosReturnsAdminService', ['getPolicies', 'saveDefaultPolicy']);
    api.getPolicies.and.returnValue(ok(policies));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PosReturnPolicyPageComponent],
      providers: [provideNoopAnimations(), provideRouter([]), { provide: PosReturnsAdminService, useValue: api }]
    }).compileComponents();
    fixture = TestBed.createComponent(PosReturnPolicyPageComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('edits the org default with its rowVersion and lists the store overrides', async () => {
    await setup([policyFixture(), policyFixture({ uuid: 'p-2', storeUuid: 's-1', storeName: 'Mall store', name: 'Mall rules', restockingFeePct: 15 })]);
    expect(component.model!.name).toBe('Org policy');
    expect((q('pol-name') as HTMLInputElement).value).toBe('Org policy');
    expect(q('override-table')!.textContent).toContain('Mall store');
    expect(q('override-table')!.textContent).toContain('15% restocking fee');

    api.saveDefaultPolicy.and.returnValue(ok(policyFixture({ name: 'New', rowVersion: 'prv2' })));
    component.model!.name = ' New ';
    component.save();
    const body = api.saveDefaultPolicy.calls.mostRecent().args[0];
    expect(body.name).toBe('New');
    expect(body.rowVersion).toBe('prv');
    expect(component.model!.rowVersion).toBe('prv2');
  });

  it('409 asks to reload; client rules stop the save; no default yet starts from recommended values', async () => {
    await setup([policyFixture()]);
    api.saveDefaultPolicy.and.returnValue(throwError(() => ({ status: 409, error: {} })));
    component.save();
    fixture.detectChanges();
    expect(q('policy-error')!.textContent).toContain(POLICY_CONFLICT);
    component.model!.returnWindowDays = -3;
    api.saveDefaultPolicy.calls.reset();
    component.save();
    expect(api.saveDefaultPolicy).not.toHaveBeenCalled();

    await setup([]);
    expect(component.model!.name).toBe('Organization return policy');
    expect(component.model!.rowVersion).toBeUndefined();
    expect(q('override-table')!.textContent).toContain('Every store follows the default');
  });
});
