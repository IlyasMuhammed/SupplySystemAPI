import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { OrganizationFeaturesComponent } from './organization-features.component';
import { OrganizationFeatureModel, OrganizationsService } from '../../../services/organizations.service';

const f = (o: Partial<OrganizationFeatureModel>): OrganizationFeatureModel => ({
  featureCode: 'X', featureName: 'X', category: 'FEATURE', isCore: false, displayOrder: 0, isEnabled: true, ...o
});

const FEATURES: OrganizationFeatureModel[] = [
  f({ featureCode: 'MODULE_INVENTORY', featureName: 'Inventory', category: 'MODULE', displayOrder: 50, status: 'ALWAYS_ON', isAlwaysOn: true, isLicensed: true, parentModuleCode: null }),
  f({ featureCode: 'MODULE_MANUFACTURING', featureName: 'Manufacturing', category: 'MODULE', displayOrder: 115, status: 'GRACE', isEnabled: true, isLicensed: true, graceEndsAt: '2026-11-01T00:00:00Z' }),
  f({ featureCode: 'MODULE_POS', featureName: 'Point of Sale', category: 'MODULE', displayOrder: 200, status: 'COMING_SOON', isAvailable: false, isEnabled: false }),
  f({ featureCode: 'FEATURE_BOM_MANAGEMENT', featureName: 'BOM management', parentModuleCode: 'MODULE_INVENTORY', status: 'ACTIVE', displayOrder: 2 }),
  f({ featureCode: 'FEATURE_MASTER_LEDGERS', featureName: 'Master ledgers', parentModuleCode: 'MODULE_INVENTORY', status: 'NOT_LICENSED', isEnabled: false, displayOrder: 1 }),
  f({ featureCode: 'FEATURE_QUALITY_INSPECTION', featureName: 'Quality inspection', parentModuleCode: 'MODULE_MANUFACTURING', status: 'DISABLED' }),
  f({ featureCode: 'FEATURE_ORPHAN', featureName: 'Orphan', parentModuleCode: null })
];

describe('OrganizationFeaturesComponent — licence semantics (A37 §1.2)', () => {
  it('groups each module with its own features, in display order, with status labels', () => {
    const groups = OrganizationFeaturesComponent.group(FEATURES);
    expect(groups.map(g => g.title)).toEqual(['Inventory', 'Manufacturing', 'Point of Sale', 'Other screens & features']);
    expect(groups[0].items.map(r => r.f.featureCode)).toEqual(['FEATURE_MASTER_LEDGERS', 'FEATURE_BOM_MANAGEMENT']);
    expect(groups[0].module!.statusLabel).toBe('Always on');
    expect(groups[0].module!.alwaysOn).toBeTrue();
    expect(groups[0].items[0].statusLabel).toBe('Not licensed');
    expect(groups[1].module!.statusLabel).toBe('Grace period');
    expect(groups[1].items[0].statusLabel).toBe('Switched off by org');
    expect(groups[2].module!.comingSoon).toBeTrue();
    expect(groups[3].items.map(r => r.f.featureCode)).toEqual(['FEATURE_ORPHAN']);
  });

  it('falls back to the category sections for a server without parent codes', () => {
    const old = [f({ featureCode: 'MODULE_A', category: 'MODULE' }), f({ featureCode: 'SCREEN_B', category: 'SCREEN' })];
    expect(OrganizationFeaturesComponent.group(old).map(g => g.title)).toEqual(['Modules', 'Screens']);
  });

  describe('page', () => {
    let fixture: ComponentFixture<OrganizationFeaturesComponent>;
    let svc: jasmine.SpyObj<OrganizationsService>;

    beforeEach(async () => {
      svc = jasmine.createSpyObj<OrganizationsService>('OrganizationsService',
        ['getById', 'getFeatures', 'updateFeatures', 'applyPlanTemplate', 'getFeatureHistory']);
      svc.getById.and.returnValue(of({ success: true, message: '', result: { id: 'o1', orgCode: 'ACME', orgName: 'Acme', plan: 'STANDARD' } as any }));
      svc.getFeatures.and.callFake(() => of({ success: true, message: '', result: FEATURES.map(x => ({ ...x })) }));
      svc.updateFeatures.and.returnValue(of({ success: true, message: '', result: { updatedFeatures: [], autoEnabledDependencies: [] } }));
      svc.getFeatureHistory.and.returnValue(of({ success: true, message: '', result: [
        { performedAt: '2026-10-09T08:00:00Z', action: 'UNLICENSED', featureCode: 'MODULE_MANUFACTURING', performedByName: 'Root' },
        { performedAt: '2026-10-08T08:00:00Z', action: 'GRACE_EXPIRED', featureCode: 'MODULE_POS', performedByName: '' }
      ] }));

      await TestBed.configureTestingModule({
        imports: [OrganizationFeaturesComponent],
        providers: [
          provideNoopAnimations(), provideRouter([]),
          { provide: OrganizationsService, useValue: svc },
          { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'o1' } } } }
        ]
      }).compileComponents();
      fixture = TestBed.createComponent(OrganizationFeaturesComponent);
      fixture.detectChanges();
    });

    afterEach(() => fixture.destroy());

    const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

    it('renders module groups and status pills, and keeps the same row objects across change detection', () => {
      expect(q('group-MODULE_INVENTORY')).not.toBeNull();
      expect(q('status-MODULE_MANUFACTURING')!.textContent).toContain('Grace period');
      const before = fixture.componentInstance.groupedFeatures;
      fixture.detectChanges();
      expect(fixture.componentInstance.groupedFeatures).toBe(before);
    });

    it('a tick sets the licence and re-reads the statuses', () => {
      const cmp = fixture.componentInstance;
      const qi = cmp.features.find(x => x.featureCode === 'FEATURE_QUALITY_INSPECTION')!;
      cmp.onToggle(qi, false);
      expect(svc.updateFeatures).toHaveBeenCalledWith('o1', [{ featureCode: 'FEATURE_QUALITY_INSPECTION', isEnabled: false }]);
      expect(svc.getFeatures).toHaveBeenCalledTimes(2);
    });

    it('History tab loads GET …/features/history once and shows readable rows', () => {
      (q('tab-history') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(svc.getFeatureHistory).toHaveBeenCalledOnceWith('o1');
      const table = q('history-table')!;
      expect(table.textContent).toContain('Manufacturing');
      expect(table.textContent).toContain('Licence removed');
      expect(table.textContent).toContain('Grace ended');
      expect(table.textContent).toContain('System');
      (q('tab-features') as HTMLButtonElement).click();
      (q('tab-history') as HTMLButtonElement)?.click();
      expect(svc.getFeatureHistory).toHaveBeenCalledTimes(1);
    });
  });
});
