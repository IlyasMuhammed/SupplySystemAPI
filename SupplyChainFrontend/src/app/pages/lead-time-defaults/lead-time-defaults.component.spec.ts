import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { LeadTimeDefaultsComponent } from './lead-time-defaults.component';
import { LeadTimeDefaultsModel, LeadTimeService } from '../../services/lead-time.service';
import { AuthService } from '../service/auth.service';

/** A34-PB-08 — Settings → Lead Time Defaults (spec §11.5, API-CONTRACT §4.4). */
describe('LeadTimeDefaultsComponent (A34-PB-08)', () => {
  let fixture: ComponentFixture<LeadTimeDefaultsComponent>;
  let component: LeadTimeDefaultsComponent;
  let service: jasmine.SpyObj<LeadTimeService>;
  let permissions: string[];
  let el: HTMLElement;

  const ok = (result: unknown) => of({ success: true, message: '', result } as any);
  const SYSTEM: LeadTimeDefaultsModel = {
    pickPackDays: 1, shippingLeadTimeDays: 3, salesBufferDays: 1, manufacturingBufferDays: 0, qualityInspectionDays: 0,
    internalTransferDays: 0, isSaved: false
  };

  async function setup(model: LeadTimeDefaultsModel = SYSTEM) {
    service = jasmine.createSpyObj<LeadTimeService>('LeadTimeService', ['getDefaults', 'updateDefaults']);
    service.getDefaults.and.returnValue(ok(model));
    service.updateDefaults.and.callFake(req => ok({ ...req, isSaved: true, modifiedDate: '2026-10-04T08:00:00Z' }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [LeadTimeDefaultsComponent],
      providers: [
        provideNoopAnimations(),
        { provide: LeadTimeService, useValue: service },
        { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(LeadTimeDefaultsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const refresh = async () => { fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges(); };
  const input = (field: string) => el.querySelector(`[data-testid="ltd-${field}"] input`) as HTMLInputElement;

  beforeEach(() => { permissions = ['LEAD_TIME_DEFAULTS_MANAGE', 'INVENTORY_VIEW']; });

  it('shows the six defaults in the §11.5 order, each in days', async () => {
    await setup();
    const labels = Array.from(el.querySelectorAll('[data-testid^="ltd-row-"] label')).map(l => l.textContent!.trim());
    expect(labels).toEqual(['Pick & Pack Time', 'Shipping Lead Time', 'Sales Safety Buffer', 'Manufacturing Buffer',
      'Quality Inspection Time', 'Internal Transfer Time']);
    expect(input('shippingLeadTimeDays').value).toBe('3');
    expect(q('ltd-row-pickPackDays')!.textContent).toContain('days');
  });

  it('carries both §11.5 notes', async () => {
    await setup();
    expect(q('ltd-intro')!.textContent).toContain('These defaults apply to all products that don\'t have variant-level overrides.');
    expect(q('ltd-variant-note')!.textContent).toContain('Supplier lead time and manufacturing lead time are always set at the product/variant level');
  });

  it('says when the organization has saved nothing yet (system defaults)', async () => {
    await setup();
    expect(q('ltd-system-defaults')).not.toBeNull();
    await setup({ ...SYSTEM, pickPackDays: 2, isSaved: true, modifiedDate: '2026-10-01T10:00:00Z' });
    expect(q('ltd-system-defaults')).toBeNull();
    expect(q('ltd-last-changed')).not.toBeNull();
  });

  it('saves all six with LEAD_TIME_DEFAULTS_MANAGE, then shows them as saved', async () => {
    await setup();
    expect(component.dirty).toBeFalse();
    component.form.patchValue({ shippingLeadTimeDays: 5, manufacturingBufferDays: 2 });
    expect(component.dirty).toBeTrue();
    component.save();
    expect(service.updateDefaults).toHaveBeenCalledWith({
      pickPackDays: 1, shippingLeadTimeDays: 5, salesBufferDays: 1, manufacturingBufferDays: 2, qualityInspectionDays: 0,
      internalTransferDays: 0
    });
    await refresh();
    expect(component.dirty).toBeFalse();
    expect(q('ltd-system-defaults')).toBeNull();
  });

  it('refuses a value outside 0–365 or a fraction before saving', async () => {
    await setup();
    component.form.patchValue({ pickPackDays: 400 });
    component.save();
    expect(service.updateDefaults).not.toHaveBeenCalled();
    await refresh();
    expect(q('ltd-problem')!.textContent).toContain('between 0 and 365');
    component.form.patchValue({ pickPackDays: 1.5 });
    component.save();
    expect(service.updateDefaults).not.toHaveBeenCalled();
  });

  it('shows the server\'s refusal', async () => {
    await setup();
    service.updateDefaults.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Pick & pack days must be between 0 and 365.' } })));
    spyOn(component['messages'], 'add');
    component.form.patchValue({ pickPackDays: 2 });
    component.save();
    expect(component['messages'].add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error', detail: 'Pick & pack days must be between 0 and 365.' }));
  });

  it('is read-only without LEAD_TIME_DEFAULTS_MANAGE: inputs disabled, no Save, and nothing is sent', async () => {
    permissions = ['INVENTORY_VIEW'];
    await setup();
    expect(q('ltd-save')).toBeNull();
    expect(q('ltd-read-only')).not.toBeNull();
    expect(input('pickPackDays').disabled).toBeTrue();
    component.save();
    expect(service.updateDefaults).not.toHaveBeenCalled();
  });

  it('says so when the defaults cannot be loaded', async () => {
    await setup();
    service.getDefaults.and.returnValue(throwError(() => ({ status: 500 })));
    component.load();
    await refresh();
    expect(q('ltd-load-failed')).not.toBeNull();
  });
});
