import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';
import { FeaturesListComponent } from './features-list.component';
import { FeatureDefinition, FeaturesAdminService } from '../../../services/features-admin.service';

/** A51 P2 - super admin Features page: status dropdown + visible toggle, instant save, rollback on error. */
describe('FeaturesListComponent', () => {
  let fixture: ComponentFixture<FeaturesListComponent>;
  let c: FeaturesListComponent;
  let api: jasmine.SpyObj<FeaturesAdminService>;
  let messages: MessageService;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  const feature = (code: string, name: string, extra: Partial<FeatureDefinition> = {}): FeatureDefinition => ({
    id: 'id-' + code, featureCode: code, featureName: name, category: 'MODULE', description: null, isCore: false, displayOrder: 1,
    isAvailable: true, status: 'Live', isVisible: true, icon: null, ...extra
  });
  const ok = <T>(result: T) => of({ success: true, message: '', result });
  const rows = () => [
    feature('MODULE_SUPPLIERS', 'Supplier Management'),
    feature('MODULE_POS', 'Point of Sale', { status: 'ComingSoon', isVisible: false }),
    feature('MODULE_ECOMMERCE', 'E-commerce', { isAvailable: false })
  ];

  async function setup(listResult: any = ok(rows())) {
    api = jasmine.createSpyObj<FeaturesAdminService>('FeaturesAdminService', ['listFeatures', 'setStatus', 'setVisibility']);
    api.listFeatures.and.returnValue(listResult);
    await TestBed.configureTestingModule({
      imports: [FeaturesListComponent],
      providers: [provideRouter([]), provideNoopAnimations(), MessageService, { provide: FeaturesAdminService, useValue: api }]
    }).compileComponents();
    messages = TestBed.inject(MessageService);
    fixture = TestBed.createComponent(FeaturesListComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('lists every feature with its code, status control and visible toggle', async () => {
    await setup();
    expect(c.rows.length).toBe(3);
    expect(q('row-MODULE_POS').textContent).toContain('MODULE_POS');
    expect(q('status-MODULE_POS')).toBeTruthy();
    expect(q('visible-MODULE_POS')).toBeTruthy();
    expect(q('row-MODULE_ECOMMERCE').textContent).toContain('Not built');
  });

  it('shows a retryable error when the list fails', async () => {
    await setup(throwError(() => ({ error: { message: 'boom' } })));
    expect(q('error')).toBeTruthy();
  });

  it('filters by code or name', async () => {
    await setup();
    c.search = 'point';
    expect(c.filtered.map(r => r.featureCode)).toEqual(['MODULE_POS']);
    c.search = 'supplier';
    expect(c.filtered.map(r => r.featureCode)).toEqual(['MODULE_SUPPLIERS']);
  });

  it('a status change saves at once and keeps the new value', async () => {
    await setup();
    const row = c.rows[0];
    api.setStatus.and.returnValue(ok({ ...row, status: 'Beta' }));
    c.changeStatus(row, 'Beta');
    expect(api.setStatus).toHaveBeenCalledWith('MODULE_SUPPLIERS', 'Beta');
    expect(row.status).toBe('Beta');
    expect(c.busy.has('MODULE_SUPPLIERS')).toBeFalse();
  });

  it('a failed status save rolls the row back and tells the user', async () => {
    await setup();
    const row = c.rows[0];
    const toast = spyOn(messages, 'add');
    api.setStatus.and.returnValue(throwError(() => ({ error: { message: 'Status must be one of: Live.' } })));
    c.changeStatus(row, 'Deprecated');
    expect(row.status).toBe('Live');
    expect(c.busy.size).toBe(0);
    expect(toast).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error' }));
  });

  it('a visibility change saves at once and rolls back on error', async () => {
    await setup();
    const row = c.rows[0];
    api.setVisibility.and.returnValue(ok({ ...row, isVisible: false }));
    c.changeVisible(row, false);
    expect(api.setVisibility).toHaveBeenCalledWith('MODULE_SUPPLIERS', false);
    expect(row.isVisible).toBeFalse();

    api.setVisibility.and.returnValue(throwError(() => ({ error: { message: 'no' } })));
    c.changeVisible(row, true);
    expect(row.isVisible).toBeFalse();
  });

  it('choosing the value a row already has sends nothing', async () => {
    await setup();
    c.changeStatus(c.rows[0], 'Live');
    c.changeVisible(c.rows[0], true);
    expect(api.setStatus).not.toHaveBeenCalled();
    expect(api.setVisibility).not.toHaveBeenCalled();
  });
});
