import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { AllocationsComponent } from './allocations.component';
import {
  AllocationService, AllocationSummary, AvailabilityResult, DemandAllocationSummary
} from '../../../services/allocation.service';
import { InventoryService } from '../../../services/inventory.service';
import { AttachmentService } from '../../../services/attachment.service';
import { AuthService } from '../../service/auth.service';

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

function demand(overrides: Partial<DemandAllocationSummary> = {}): DemandAllocationSummary {
  return {
    uuid: 'd1', demandType: 'SALES_ORDER', demandUuid: 'so-1', demandLineUuid: null, reference: 'SO-2026-00008',
    variantUuid: 'v1', warehouseUuid: null, requiredQty: 10, reservedQty: 6, plannedQty: 0, consumedQty: 0, shortage: 4,
    requiredDate: '2026-10-01T00:00:00Z', priority: 2, status: 'OPEN',
    ...overrides
  };
}

function allocation(overrides: Partial<AllocationSummary> = {}): AllocationSummary {
  return {
    uuid: 'a1', demandRegistryUuid: 'd1', demandType: 'SALES_ORDER', demandUuid: 'so-1', demandLineUuid: null,
    demandReference: 'SO-2026-00008', variantUuid: 'v1', warehouseUuid: 'w1', allocatedQty: 6, consumedQty: 0,
    supplyType: 'ON_HAND', supplyUuid: null, supplyReference: null, allocationType: 'RESERVED', priorityScore: 1,
    requiredDate: '2026-10-01T00:00:00Z', status: 'ACTIVE', allocatedAt: '2026-09-26T09:00:00Z', releasedAt: null, releaseReason: null,
    ...overrides
  };
}

const AVAILABILITY: AvailabilityResult = {
  variantUuid: 'v1', warehouseUuid: null, onHand: 10, reserved: 6, available: 4, incoming: 2, openDemand: 10, unallocated: 4
};

describe('AllocationsComponent', () => {
  let fixture: ComponentFixture<AllocationsComponent>;
  let component: AllocationsComponent;
  let service: jasmine.SpyObj<AllocationService>;
  let inventory: jasmine.SpyObj<InventoryService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;
  const attachments = { resolveUrl: (u: string) => u } as unknown as AttachmentService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup() {
    service = jasmine.createSpyObj<AllocationService>('AllocationService', [
      'getAllocations', 'getAvailability', 'getDemands', 'run', 'release', 'reallocate', 'cancelDemand',
      'registerDemand', 'getRules', 'setRules'
    ]);
    service.getAvailability.and.returnValue(ok(AVAILABILITY));
    service.getDemands.and.returnValue(ok([demand(), demand({ uuid: 'd2', reference: 'PROD-2026-00001', demandType: 'PRODUCTION_MATERIAL', reservedQty: 0, shortage: 10 })]));
    service.getAllocations.and.returnValue(ok({ items: [allocation()], total: 1, page: 1, pageSize: 200 }));
    service.run.and.returnValue(ok({ variantUuid: 'v1', warehouseUuid: null, demandsEvaluated: 2, quantityReserved: 0, quantityPlanned: 0, shortage: 14, demands: [] }));
    service.release.and.returnValue(ok(null));
    service.reallocate.and.returnValue(ok(allocation({ uuid: 'a2', demandRegistryUuid: 'd2' })));
    service.cancelDemand.and.returnValue(ok(null));
    service.registerDemand.and.returnValue(ok(demand({ uuid: 'd3', reference: 'SO-NEW' })));
    service.getRules.and.returnValue(ok([]));
    service.setRules.and.callFake((rules: any) => ok(rules));

    inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getWarehouses', 'getProductById']);
    inventory.getProducts.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 500, totalPages: 0 }));
    inventory.getWarehouses.and.returnValue(ok([{ id: 1, uuid: 'w1', code: 'MAIN', name: 'Main', isActive: true }]));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [AllocationsComponent],
      providers: [
        provideNoopAnimations(),
        { provide: AllocationService, useValue: service },
        { provide: InventoryService, useValue: inventory },
        { provide: AttachmentService, useValue: attachments },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(AllocationsComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  function pickVariant() {
    component.onSelection({
      productUuid: 'p1', productName: 'Plain T-Shirt', variantId: 1, variantUuid: 'v1', variantSku: 'TS-1',
      variantName: 'Default', purchasePrice: 3, uomCode: 'PCS'
    });
    fixture.detectChanges();
  }

  beforeEach(() => { permissions = ['ALLOCATION_VIEW', 'ALLOCATION_RUN', 'ALLOCATION_ADMIN']; });

  it('loads the catalogue and waits for a product to be picked', async () => {
    await setup();

    expect(inventory.getProducts).toHaveBeenCalledOnceWith({ activeOnly: true, pageSize: 500 });
    expect(inventory.getWarehouses).toHaveBeenCalledTimes(1);
    expect(component.warehouseOptions.map(o => o.label)).toEqual(['All warehouses', 'Main']);
    expect(query('pick-first')).not.toBeNull();
    expect(service.getAvailability).not.toHaveBeenCalled();
  });

  it('shows availability, demands and allocations for the picked variant', async () => {
    await setup();
    pickVariant();

    expect(service.getAvailability).toHaveBeenCalledWith('v1', null);
    expect(service.getDemands).toHaveBeenCalledWith('v1');
    expect(service.getAllocations).toHaveBeenCalledWith({ variantUuid: 'v1', warehouseUuid: undefined, pageSize: 200 });
    expect(query('availability')!.textContent).toContain('10');
    expect(query('demand-SO-2026-00008')).not.toBeNull();
    expect(query('demand-PROD-2026-00001')!.textContent).toContain('Production material');
    expect(query('allocation-a1')!.textContent).toContain('RESERVED');
    expect(query('allocation-a1')!.textContent).toContain('Main');
  });

  it('runs the engine for the variant and warehouse in view, then reloads', async () => {
    await setup();
    pickVariant();
    component.selectedWarehouseUuid = 'w1';
    component.onWarehouseChange();
    fixture.detectChanges();

    (query('run')!.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(service.run).toHaveBeenCalledOnceWith('v1', 'w1');
    expect(service.getAvailability).toHaveBeenCalledWith('v1', 'w1');
    expect(query('last-run')!.textContent).toContain('2 demand(s) evaluated');
    expect(toasts.calls.mostRecent().args[0].severity).toBe('success');
  });

  it('hides the run and register buttons without ALLOCATION_RUN, and the admin actions without ALLOCATION_ADMIN', async () => {
    permissions = ['ALLOCATION_VIEW'];
    await setup();
    pickVariant();

    expect(query('run')).toBeNull();
    expect(query('register')).toBeNull();
    expect(query('release-a1')).toBeNull();
    expect(query('move-a1')).toBeNull();
    expect(component.canAdmin).toBeFalse();
  });

  it('releases an allocation only with a reason', async () => {
    await setup();
    pickVariant();

    component.openRelease(allocation());
    fixture.detectChanges();
    expect((query('confirm-release')!.querySelector('button') as HTMLButtonElement).disabled).toBeTrue();

    component.releaseReason = 'Held by mistake.';
    component.confirmRelease();

    expect(service.release).toHaveBeenCalledOnceWith('a1', 'Held by mistake.');
    expect(component.releaseTarget).toBeNull();
    expect(service.getAllocations).toHaveBeenCalledTimes(2);
  });

  it('offers only other open, short demands of the variant as move targets', async () => {
    await setup();
    pickVariant();

    component.openMove(allocation());

    expect(component.moveTargets.map(t => t.value)).toEqual(['d2']);
    expect(component.moveForm.value.quantity).toBe(6);

    component.moveForm.setValue({ toDemandUuid: 'd2', quantity: 3, reason: 'Board decision.' });
    component.confirmMove();

    expect(service.reallocate).toHaveBeenCalledOnceWith('a1', 'd2', 3, 'Board decision.');
    expect(component.moveTarget).toBeNull();
  });

  it('registers a demand for the picked variant and runs straight after', async () => {
    await setup();
    pickVariant();

    component.openRegister();
    component.registerForm.setValue({
      demandType: 'PRODUCTION_MATERIAL', reference: 'PROD-2026-00002', requiredQty: 5,
      requiredDate: new Date(2026, 9, 15), priority: 2, warehouseUuid: 'w1'
    });
    component.confirmRegister();

    const sent = service.registerDemand.calls.mostRecent().args[0];
    expect(sent.demandType).toBe('PRODUCTION_MATERIAL');
    expect(sent.reference).toBe('PROD-2026-00002');
    expect(sent.variantUuid).toBe('v1');
    expect(sent.warehouseUuid).toBe('w1');
    expect(sent.requiredQty).toBe(5);
    expect(sent.requiredDate).toBe('2026-10-15T00:00:00.000Z');
    expect(sent.allocate).toBeTrue();
    expect(component.registerVisible).toBeFalse();
  });

  it('loads the rules when the tab is opened and saves the edited set', async () => {
    await setup();
    expect(service.getRules).not.toHaveBeenCalled();

    component.onTabChange(1);
    fixture.detectChanges();
    expect(service.getRules).toHaveBeenCalledTimes(1);
    expect(component.rulesDraft).toEqual([]);

    component.addRule();
    expect(component.rulesValid).withContext('a rule needs a name').toBeFalse();
    component.rulesDraft[0].ruleName = 'Date first';
    component.saveRules();

    expect(service.setRules).toHaveBeenCalledTimes(1);
    expect(service.setRules.calls.mostRecent().args[0][0].ruleName).toBe('Date first');
    expect(toasts.calls.mostRecent().args[0].summary).toBe('Saved');
  });

  it('reports a failed load and can try again', async () => {
    await setup();
    service.getDemands.and.returnValue(throwError(() => ({ status: 500 })));
    pickVariant();

    expect(query('load-failed')).not.toBeNull();

    service.getDemands.and.returnValue(ok([]));
    component.refresh();
    fixture.detectChanges();
    expect(query('load-failed')).toBeNull();
  });
});
