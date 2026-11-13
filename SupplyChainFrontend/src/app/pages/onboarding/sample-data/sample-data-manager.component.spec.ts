import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';
import { SampleDataManagerComponent, countRows } from './sample-data-manager.component';
import { OnboardingDemoService, SampleDataPlanModel } from '../../../services/onboarding-demo.service';
import { DemoRefsStore } from '../../../shared/components/demo-badge/demo-refs.store';

const ok = <T>(result: T) => of({ success: true, message: '', result });
const COUNTS = { customers: 5, vendors: 3, products: 20, saleOrders: 2, purchaseOrders: 1, openingStock: 20 };
const ZERO = { customers: 0, vendors: 0, products: 0, saleOrders: 0, purchaseOrders: 0, openingStock: 0 };

function plan(over: Partial<SampleDataPlanModel> = {}): SampleDataPlanModel {
  return { hasDemo: false, canLoad: true, reason: null, loadedAt: null, counts: COUNTS, current: ZERO, notes: [], ...over };
}

describe('SampleDataManagerComponent (A50 tasks 309–311)', () => {
  let fixture: ComponentFixture<SampleDataManagerComponent>;
  let c: SampleDataManagerComponent;
  let api: jasmine.SpyObj<OnboardingDemoService>;
  let refs: jasmine.SpyObj<DemoRefsStore>;
  let toasts: jasmine.Spy;
  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  async function setup(first: any) {
    api = jasmine.createSpyObj<OnboardingDemoService>('OnboardingDemoService', ['getSampleData', 'loadSampleData', 'deleteSampleData']);
    api.getSampleData.and.returnValue(first);
    refs = jasmine.createSpyObj<DemoRefsStore>('DemoRefsStore', ['invalidate']);
    await TestBed.configureTestingModule({
      imports: [SampleDataManagerComponent],
      providers: [provideRouter([]), provideNoopAnimations(),
        { provide: OnboardingDemoService, useValue: api }, { provide: DemoRefsStore, useValue: refs }]
    }).compileComponents();
    fixture = TestBed.createComponent(SampleDataManagerComponent);
    c = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  it('shows what a load creates, in a fixed order, and notes', async () => {
    await setup(ok(plan({ notes: ['Opening stock is skipped.'] })));
    expect(c.planRows.map(r => r.key)).toEqual(['customers', 'vendors', 'products', 'openingStock', 'saleOrders', 'purchaseOrders']);
    expect(q('plan-customers')?.textContent).toContain('5');
    expect(q('sdm-status')?.textContent).toContain('Opening stock is skipped.');
    expect(c.canLoad).toBeTrue();
  });

  it('load: the confirm dialog lists the records, then loads, invalidates the badges and reloads', async () => {
    await setup(ok(plan()));
    (q('load')?.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(document.querySelector('[data-testid="load-dialog"]')?.textContent).toContain('Customers');
    api.loadSampleData.and.returnValue(ok({ created: COUNTS, total: 51, notes: [] }));
    c.confirmLoad();
    expect(refs.invalidate).toHaveBeenCalled();
    expect(api.getSampleData).toHaveBeenCalledTimes(2);
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ detail: '51 demo records were created.' }));
  });

  it('a load refused with 409 says so', async () => {
    await setup(ok(plan()));
    api.loadSampleData.and.returnValue(throwError(() => ({ status: 409, error: {} })));
    c.confirmLoad();
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'Sample data is already loaded. Remove it first.' }));
  });

  it('when loaded: Load is off with the reason, Delete is on', async () => {
    await setup(ok(plan({ hasDemo: true, canLoad: false, reason: 'Sample data is already loaded. Remove it first.', current: COUNTS })));
    expect(c.canLoad).toBeFalse();
    expect(q('sdm-reason')?.textContent).toContain('already loaded');
    expect(q('current-customers')).not.toBeNull();
    expect((q('delete')?.querySelector('button') as HTMLButtonElement).disabled).toBeFalse();
  });

  it('delete: shows the records kept because real data uses them', async () => {
    await setup(ok(plan({ hasDemo: true, canLoad: false, current: COUNTS })));
    api.deleteSampleData.and.returnValue(ok({
      deleted: { customers: 4, vendors: 3, openingStock: 0 }, total: 7,
      kept: [{ entityType: 'Customer', entityRef: 'c-1', name: null, reason: 'Used by real data.' }],
      notes: ['Acme Corporation (Demo) is used by sale order SO-2026-00012.']
    }));
    api.getSampleData.and.returnValue(ok(plan()));
    c.confirmDelete();
    fixture.detectChanges();
    expect(q('sdm-result')?.textContent).toContain('Removed 7 demo records');
    expect(q('kept-c-1')?.textContent).toContain('c-1');            // name is null for now: the ref is shown
    expect(q('kept-c-1')?.textContent).toContain('Used by real data.');
    expect(q('sdm-delete-note')?.textContent).toContain('SO-2026-00012');
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'warn', summary: 'Partly removed' }));
  });

  it('a LEGACY org cannot load and is told why', async () => {
    await setup(ok(plan({ canLoad: false, reason: 'Sample data can only be loaded into a new organization.' })));
    expect(c.canLoad).toBeFalse();
    expect(q('sdm-reason')?.textContent).toContain('new organization');
    expect((q('load')?.querySelector('button') as HTMLButtonElement).disabled).toBeTrue();
  });

  it('error state', async () => {
    await setup(throwError(() => ({ error: { message: 'Forbidden' } })));
    expect(q('sdm-error')?.textContent).toContain('Forbidden');
  });

  it('countRows keeps unknown keys after the known ones', () => {
    expect(countRows({ zeta: 1, customers: 2 }).map(r => r.key)).toEqual(['customers', 'zeta']);
    expect(countRows(null)).toEqual([]);
  });
});
