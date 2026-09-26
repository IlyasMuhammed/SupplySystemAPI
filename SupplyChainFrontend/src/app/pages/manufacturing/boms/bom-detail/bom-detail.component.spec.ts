import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { BomDetailComponent } from './bom-detail.component';
import { BomDetail, BomService, BomStatus } from '../../../../services/bom.service';
import { AuthService } from '../../../service/auth.service';

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

function bom(status: BomStatus, overrides: Partial<BomDetail> = {}): BomDetail {
  return {
    uuid: 'b1', bomNumber: 'BOM-2026-00001', productUuid: 'p1', productName: 'Printed T-Shirt', productSku: 'TS-PRINT',
    productVariantUuid: null, variantName: null, version: 1, status, baseQuantity: 1, baseUom: 'PCS',
    effectiveFrom: null, effectiveTo: null, warehouseUuid: null, warehouseName: null, lineCount: 1,
    createdAt: '2026-09-26T08:00:00Z', updatedAt: '2026-09-26T08:00:00Z', activatedAt: null,
    traceId: 't1', notes: null, createdBy: 7, submittedBy: null, submittedAt: null, approvedBy: null, approvedAt: null,
    rejectedBy: null, rejectedAt: null, rejectionReason: null, activatedBy: null, obsoletedBy: null, obsoletedAt: null,
    lines: [{
      uuid: 'l1', sequence: 10, materialProductUuid: 'mp1', materialProductName: 'Plain T-Shirt', materialProductType: 'RAW_MATERIAL',
      materialSupplyMethod: 'PURCHASE', materialVariantUuid: 'mv1', materialSku: 'TS-PLAIN-1', materialVariantName: 'Default',
      quantity: 1, uom: 'PCS', scrapPercentage: 0, grossQuantity: 1, isCritical: true
    }],
    ...overrides
  };
}

describe('BomDetailComponent', () => {
  let fixture: ComponentFixture<BomDetailComponent>;
  let component: BomDetailComponent;
  let service: jasmine.SpyObj<BomService>;
  let router: Router;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(detail: BomDetail) {
    service = jasmine.createSpyObj<BomService>('BomService', [
      'getBom', 'submit', 'approve', 'reject', 'activate', 'obsolete', 'newVersion', 'deleteBom', 'getCost', 'getVersions', 'compare'
    ]);
    service.getBom.and.returnValue(ok(detail));
    for (const m of ['submit', 'approve', 'reject', 'activate', 'obsolete'] as const) service[m].and.returnValue(ok(null));
    service.newVersion.and.returnValue(ok('b2'));
    service.getCost.and.returnValue(ok({ bomUuid: 'b1', version: 1, baseQuantity: 1, totalCost: 3.5, costPerUnit: 3.5, warnings: [], lines: [] }));
    service.getVersions.and.returnValue(ok([
      { uuid: 'b1', bomNumber: 'BOM-2026-00001', version: 1, status: detail.status, lineCount: 1, createdAt: '2026-09-26T08:00:00Z' },
      { uuid: 'b0', bomNumber: 'BOM-2026-00000', version: 0, status: 'OBSOLETE', lineCount: 1, createdAt: '2026-09-01T08:00:00Z' }
    ]));
    service.compare.and.returnValue(ok({
      leftUuid: 'b0', leftVersion: 0, leftStatus: 'OBSOLETE', rightUuid: 'b1', rightVersion: 1, rightStatus: detail.status,
      headerChanges: [], added: [], removed: [], changed: []
    }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [BomDetailComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: BomService, useValue: service },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', 'b1']]) } } }
      ]
    }).compileComponents();

    router = TestBed.inject(Router);
    spyOn(router, 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(BomDetailComponent);
    component = fixture.componentInstance;
    spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  beforeEach(() => {
    permissions = ['BOM_VIEW', 'BOM_CREATE', 'BOM_EDIT', 'BOM_SUBMIT', 'BOM_APPROVE', 'BOM_ACTIVATE', 'BOM_OBSOLETE'];
  });

  it('shows the recipe and, for a draft, only edit, delete and submit', async () => {
    await setup(bom('DRAFT'));

    expect(query('status')!.textContent).toContain('DRAFT');
    expect(query('line-10')!.textContent).toContain('Plain T-Shirt');
    expect(query('edit')).not.toBeNull();
    expect(query('delete')).not.toBeNull();
    expect(query('submit')).not.toBeNull();
    expect(query('approve')).toBeNull();
    expect(query('activate')).toBeNull();
    expect(query('new-version')).toBeNull();
  });

  it('offers approve and reject while submitted, activate once approved, obsolete and new version once active', async () => {
    await setup(bom('SUBMITTED', { submittedBy: 7, submittedAt: '2026-09-26T09:00:00Z' }));
    expect(query('approve')).not.toBeNull();
    expect(query('reject')).not.toBeNull();
    expect(query('edit')).toBeNull();

    await setup(bom('APPROVED'));
    expect(query('activate')).not.toBeNull();
    expect(query('approve')).toBeNull();

    await setup(bom('ACTIVE'));
    expect(query('obsolete')).not.toBeNull();
    expect(query('new-version')).not.toBeNull();
    expect(query('activate')).toBeNull();
  });

  it('hides every action the user is not allowed', async () => {
    permissions = ['BOM_VIEW'];
    await setup(bom('SUBMITTED'));

    expect(query('actions')!.querySelectorAll('button').length).toBe(0);
  });

  it('submits, then reloads', async () => {
    await setup(bom('DRAFT'));
    service.getBom.and.returnValue(ok(bom('SUBMITTED')));

    (query('submit')!.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(service.submit).toHaveBeenCalledOnceWith('b1');
    expect(service.getBom).toHaveBeenCalledTimes(2);
    expect(query('status')!.textContent).toContain('SUBMITTED');
  });

  it('rejects only with a reason and shows it afterwards', async () => {
    await setup(bom('SUBMITTED'));
    component.openReject();
    fixture.detectChanges();
    expect((query('confirm-reject')!.querySelector('button') as HTMLButtonElement).disabled).toBeTrue();

    component.rejectReason = 'Ink quantity is wrong.';
    service.getBom.and.returnValue(ok(bom('REJECTED', { rejectionReason: 'Ink quantity is wrong.' })));
    component.confirmReject();
    fixture.detectChanges();

    expect(service.reject).toHaveBeenCalledOnceWith('b1', 'Ink quantity is wrong.');
    expect(component.rejectVisible).toBeFalse();
    expect(query('rejection')!.textContent).toContain('Ink quantity is wrong.');
  });

  it('drafts a new version and goes to edit it', async () => {
    await setup(bom('ACTIVE'));

    component.newVersion();

    expect(service.newVersion).toHaveBeenCalledOnceWith('b1');
    expect(router.navigate).toHaveBeenCalledWith(['/portal/pages/manufacturing/boms', 'b2', 'edit']);
  });

  it('loads the cost and the versions only when their tabs are opened, and compares on request', async () => {
    await setup(bom('ACTIVE'));
    expect(service.getCost).not.toHaveBeenCalled();
    expect(service.getVersions).not.toHaveBeenCalled();

    component.onTabChange(1);
    fixture.detectChanges();
    expect(service.getCost).toHaveBeenCalledOnceWith('b1');
    expect(query('cost')!.textContent).toContain('3.50');

    component.onTabChange(2);
    fixture.detectChanges();
    expect(service.getVersions).toHaveBeenCalledOnceWith('p1', null);
    expect(component.compareOptions.map(o => o.value)).toEqual(['b0'], 'this version is not offered against itself');

    component.compareWith = 'b0';
    component.compare();
    expect(service.compare).toHaveBeenCalledOnceWith('b0', 'b1');
    expect(component.comparison?.rightVersion).toBe(1);
  });

  it('says when it could not load', async () => {
    await setup(bom('DRAFT'));
    service.getBom.and.returnValue(throwError(() => ({ status: 500 })));
    component.load();
    fixture.detectChanges();

    expect(query('load-failed')).not.toBeNull();
  });
});
