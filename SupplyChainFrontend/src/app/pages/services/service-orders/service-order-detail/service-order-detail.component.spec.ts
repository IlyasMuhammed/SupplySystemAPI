import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ConfirmationService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { COMPLETE_CONFIRM_MESSAGE, ServiceOrderDetailComponent } from './service-order-detail.component';
import { ServiceMaterial, ServiceOrderDetail, ServiceOrderService } from '../../../../services/service-order.service';
import { InventoryService } from '../../../../services/inventory.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { UserService } from '../../../../services/user.service';
import { TimelineService } from '../../../../services/timeline.service';
import { AuthService } from '../../../service/auth.service';

const ok = <T>(result: T) => of({ success: true, message: '', result } as any);
const ALL = ['SERVICE_ORDER_VIEW', 'SERVICE_ORDER_EDIT', 'SERVICE_ORDER_COMPLETE', 'SERVICE_ORDER_CANCEL'];

function material(overrides: Partial<ServiceMaterial> = {}): ServiceMaterial {
  return {
    uuid: 'm1', productUuid: 'p1', variantUuid: 'v1', productName: 'Copper pipe', variantName: null, sku: 'PIPE', sourceType: 'STOCK',
    requiredQuantity: 5, netQuantity: 5, scrapAllowance: 0, reservedQuantity: 5, issuedQuantity: 0, consumedQuantity: 0, returnedQuantity: 0,
    shortageQuantity: 0, availableQuantity: 20, uom: 'M', isCritical: false, isAdhoc: false, status: 'FULLY_RESERVED',
    requiredDate: '2026-10-12', canRemove: false, ...overrides
  };
}

function detail(overrides: Partial<ServiceOrderDetail> = {}): ServiceOrderDetail {
  return {
    uuid: 'so-1', serviceNumber: 'SVC-2026-0001', serviceProductName: 'AC installation', customerUuid: 'c-1', customerName: 'Cool Air Ltd',
    scheduledDate: '2026-10-12', scheduledTime: '09:30', assignedUserId: 7, assignedUserName: 'Usman Khan', status: 'DRAFT', priority: 1,
    materialReadiness: 'NOT_CHECKED', quantity: 1, serviceProductUuid: 'p-svc', serviceVariantUuid: 'v-svc', warehouseUuid: 'w-1',
    warehouseName: 'Main', estimatedHours: 2, sourceType: 'MANUAL', invoicingPolicy: 'FIXED_PRICE', billingModel: 'INCLUSIVE',
    customerSignature: false, traceId: 'trace-1', rowVersion: 'AAAAAAAAB9E=', createdAt: '2026-10-10T08:00:00Z', updatedAt: '2026-10-10T08:00:00Z',
    materials: [], ledger: [], allowedActions: ['EDIT', 'PLAN', 'CANCEL'], ...overrides
  };
}

// A36-P2-13 / P3-09 / P3-10 / P4-05 / P4-06 — the service order object page.
describe('ServiceOrderDetailComponent', () => {
  let fixture: ComponentFixture<ServiceOrderDetailComponent>;
  let component: ServiceOrderDetailComponent;
  let service: jasmine.SpyObj<ServiceOrderService>;
  let perms: string[];
  let confirm: jasmine.Spy;

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const qa = (id: string): HTMLElement[] => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`));
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  async function setup(o: ServiceOrderDetail, permissions = ALL) {
    perms = permissions;
    service = jasmine.createSpyObj<ServiceOrderService>('ServiceOrderService', [
      'getById', 'update', 'plan', 'start', 'complete', 'cancel', 'close', 'addMaterial', 'removeMaterial', 'allocate'
    ]);
    service.getById.and.returnValue(ok(o));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getWarehouses', 'getProductById']);
    inventory.getProducts.and.returnValue(ok({ data: [
      { id: 2, uuid: 'p-pipe', sku: 'PIPE', name: 'Copper pipe', productType: 'STOCK' },
      { id: 3, uuid: 'p-svc', sku: 'SVC', name: 'AC installation', productType: 'SERVICE' }
    ] }));
    inventory.getWarehouses.and.returnValue(ok([]));
    inventory.getProductById.and.returnValue(ok({ variants: [] }));
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(ok({ data: [] }));
    const timeline = jasmine.createSpyObj<TimelineService>('TimelineService', ['getByTraceId', 'getByDocument']);
    timeline.getByTraceId.and.returnValue(ok({ traceId: 'trace-1', events: [] }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ServiceOrderDetailComponent],
      providers: [
        provideRouter([]), provideNoopAnimations(), provideHttpClient(), provideHttpClientTesting(),
        { provide: ServiceOrderService, useValue: service },
        { provide: InventoryService, useValue: inventory },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: UserService, useValue: jasmine.createSpyObj('UserService', ['getUsers']) },
        { provide: TimelineService, useValue: timeline },
        { provide: AuthService, useValue: {
            hasPermission: (c: string) => perms.includes(c), getUserData: () => ({ userId: 7, firstName: 'Usman' }), getRoles: () => ok([])
        } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', 'so-1']]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ServiceOrderDetailComponent);
    component = fixture.componentInstance;
    confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm').and.callFake((c: any) => { c.accept(); return null as any; });
    fixture.detectChanges();
  }

  describe('command bar', () => {
    it('offers exactly the allowed actions', async () => {
      await setup(detail());
      expect(q('plan')).not.toBeNull();
      expect(q('edit')).not.toBeNull();
      expect(q('cancel')).not.toBeNull();
      expect(q('start')).toBeNull();
      expect(q('complete')).toBeNull();
      expect(q('close')).toBeNull();
    });

    it('also needs the permission for each action', async () => {
      await setup(detail(), ['SERVICE_ORDER_VIEW', 'SERVICE_ORDER_EDIT']);
      expect(q('plan')).not.toBeNull();
      expect(q('cancel')).withContext('no SERVICE_ORDER_CANCEL').toBeNull();
    });

    it('a completed order offers Close', async () => {
      await setup(detail({ status: 'COMPLETED', allowedActions: ['EDIT', 'CLOSE'] }));
      expect(q('close')).not.toBeNull();
      expect(q('plan')).toBeNull();
    });

    it('Plan replaces the page with the detail the server answers with', async () => {
      await setup(detail());
      service.plan.and.returnValue(ok(detail({ status: 'PLANNED', allowedActions: ['EDIT', 'CANCEL'] })));
      component.plan();
      fixture.detectChanges();
      expect(service.plan).toHaveBeenCalledWith('so-1');
      expect(text(q('status'))).toBe('Planned');
      expect(q('plan')).toBeNull();
    });

    it('Cancel needs a reason', async () => {
      await setup(detail());
      service.cancel.and.returnValue(ok(detail({ status: 'CANCELLED', allowedActions: [] })));
      component.openCancel();
      component.confirmCancel();
      expect(service.cancel).not.toHaveBeenCalled();
      component.cancelReason = ' customer away ';
      component.confirmCancel();
      expect(service.cancel).toHaveBeenCalledWith('so-1', { reason: 'customer away' });
      expect(component.cancelVisible).toBeFalse();
    });
  });

  describe('header', () => {
    it('shows Materials as the current stage, with a warning, while waiting', async () => {
      await setup(detail({ status: 'WAITING', allowedActions: [] }));
      const stages = component.stages;
      expect(stages.map(s => s.state)).toEqual(['done', 'done', 'on', 'todo', 'todo', 'todo', 'todo']);
      expect(stages[2].sub).toContain('Waiting');
    });

    it('marks a cancelled order red', async () => {
      await setup(detail({ status: 'CANCELLED', allowedActions: [] }));
      const last = component.stages[component.stages.length - 1];
      expect(last.label).toBe('Cancelled');
      expect(last.state).toBe('bad');
    });

    it('links to the source sale order', async () => {
      await setup(detail({ sourceType: 'SALES_ORDER', sourceUuid: 'sale-1', sourceReference: 'SO-2026-0600' }));
      const link = q('source-link') as HTMLAnchorElement;
      expect(text(link)).toBe('SO-2026-0600');
      expect(link.getAttribute('href')).toContain('/portal/pages/sales/orders/sale-1');
    });
  });

  describe('details edit', () => {
    it('sends every field with the row version while DRAFT/PLANNED', async () => {
      await setup(detail());
      service.update.and.returnValue(ok(detail({ notes: 'x' })));
      component.startEdit();
      component.edit.quantity = 3;
      component.saveEdit();
      const body = service.update.calls.mostRecent().args[1];
      expect(body.quantity).toBe(3);
      expect(body.rowVersion).toBe('AAAAAAAAB9E=');
      expect(body.scheduledDate).toBe('2026-10-12');
      expect(body.scheduledTime).toBe('09:30');
    });

    it('only the notes change later', async () => {
      await setup(detail({ status: 'IN_PROGRESS', allowedActions: ['EDIT', 'COMPLETE', 'ADD_MATERIAL', 'CANCEL'] }));
      component.startEdit();
      fixture.detectChanges();
      expect(q('details-edit')).toBeNull();
      expect(q('edit-notes')).not.toBeNull();
      service.update.and.returnValue(ok(detail({ status: 'IN_PROGRESS' })));
      component.edit.quantity = 99;
      component.edit.notes = 'Customer asked for evening';
      component.saveEdit();
      const body = service.update.calls.mostRecent().args[1];
      expect(body.quantity).toBe(1);
      expect(body.notes).toBe('Customer asked for evening');
    });

    it('reloads when someone else saved first (409)', async () => {
      await setup(detail());
      service.update.and.returnValue(throwError(() => ({ status: 409, error: { message: 'Concurrency' } })));
      component.startEdit();
      component.saveEdit();
      expect(service.getById).toHaveBeenCalledTimes(2);
    });
  });

  describe('materials', () => {
    it('splits available and short materials, and shows the supply requirement of a subcontract line', async () => {
      await setup(detail({ status: 'MATERIAL_PENDING', allowedActions: ['EDIT', 'CANCEL'], materials: [
        material(),
        material({ uuid: 'm2', productName: 'Ducting labour', sourceType: 'SUBCONTRACT', shortageQuantity: 2, reservedQuantity: 0,
                   supplyRequirementNumber: 'SR-0009', supplyRequirementStatus: 'OPEN', status: 'PENDING' })
      ] }));
      expect(component.availableMaterials.map(m => m.uuid)).toEqual(['m1']);
      expect(component.shortageMaterials.map(m => m.uuid)).toEqual(['m2']);
      expect(text(q('material-m2')!.querySelector('[data-testid="supply"]'))).toContain('SR-0009');
      expect(q('reserve-all')).not.toBeNull();
    });

    it('Reserve all available posts to /materials/allocate', async () => {
      await setup(detail({ status: 'PLANNED', allowedActions: ['EDIT'], materials: [material()] }));
      service.allocate.and.returnValue(ok(detail({ status: 'READY', materials: [material()] })));
      component.reserveAll();
      expect(service.allocate).toHaveBeenCalledWith('so-1');
      expect(component.order!.status).toBe('READY');
    });

    it('offers Reserve all available while in progress (issues stock held for a resumed job)', async () => {
      await setup(detail({ status: 'IN_PROGRESS', allowedActions: ['EDIT', 'ADD_MATERIAL', 'COMPLETE', 'CANCEL'], materials: [material()] }));
      expect(component.canReserve).toBeTrue();
    });

    it('adds an ad-hoc material while in progress, from non-service products', async () => {
      await setup(detail({ status: 'IN_PROGRESS', allowedActions: ['ADD_MATERIAL', 'COMPLETE'] }));
      expect(q('adhoc-panel')).not.toBeNull();
      expect(component.materialProducts.map(p => p.uuid)).toEqual(['p-pipe']);
      expect(component.canSubmitAdhoc).toBeFalse();
      component.onAdhocPicked({ productUuid: 'p-pipe', variantUuid: 'v-pipe' } as any);
      component.adhoc.quantity = 3;
      component.adhoc.notes = 'longer run';
      service.addMaterial.and.returnValue(ok(detail({ status: 'IN_PROGRESS', allowedActions: ['ADD_MATERIAL', 'COMPLETE'],
        materials: [material({ uuid: 'm9', isAdhoc: true, canRemove: true })] })));
      component.addAdhoc();
      expect(service.addMaterial).toHaveBeenCalledWith('so-1', { variantUuid: 'v-pipe', quantity: 3, notes: 'longer run' });
      expect(component.adhoc.variantUuid).toBeNull();
      fixture.detectChanges();
      expect(q('adhoc-pill')).not.toBeNull();
    });

    it('has no ad-hoc panel without ADD_MATERIAL', async () => {
      await setup(detail({ status: 'READY', allowedActions: ['START', 'CANCEL'] }));
      expect(q('adhoc-panel')).toBeNull();
    });

    it('removes a material only where the server allows it', async () => {
      await setup(detail({ status: 'IN_PROGRESS', allowedActions: ['ADD_MATERIAL'], materials: [
        material(), material({ uuid: 'm9', isAdhoc: true, canRemove: true })
      ] }));
      expect(qa('remove-material').length).toBe(1);
      service.removeMaterial.and.returnValue(ok(detail({ status: 'IN_PROGRESS', allowedActions: ['ADD_MATERIAL'], materials: [material()] })));
      qa('remove-material')[0].querySelector('button')!.click();
      expect(confirm).toHaveBeenCalled();
      expect(service.removeMaterial).toHaveBeenCalledWith('so-1', 'm9');
    });
  });

  describe('completion', () => {
    const inProgress = (overrides: Partial<ServiceOrderDetail> = {}) => detail({
      status: 'IN_PROGRESS', allowedActions: ['COMPLETE', 'ADD_MATERIAL', 'CANCEL'],
      materials: [material({ issuedQuantity: 5, status: 'ISSUED' }), material({ uuid: 'm2', issuedQuantity: 0 })], ...overrides
    });

    it('lists issued materials, defaults consumed to issued and works out the return', async () => {
      await setup(inProgress());
      expect(q('sec-completion')).not.toBeNull();
      expect(component.completionRows.map(r => r.material.uuid)).toEqual(['m1']);
      expect(component.completionRows[0].consumed).toBe(5);
      component.completionRows[0].consumed = 3.5;
      expect(component.returnOf(component.completionRows[0])).toBe(1.5);
    });

    it('refuses consumed above issued or below zero', async () => {
      await setup(inProgress());
      component.completionRows[0].consumed = 6;
      expect(component.rowError(component.completionRows[0])).toBe('More than was issued');
      expect(component.canSubmitCompletion).toBeFalse();
      component.completionRows[0].consumed = -1;
      expect(component.rowError(component.completionRows[0])).toBe('Cannot be negative');
      component.completionRows[0].consumed = 5;
      expect(component.canSubmitCompletion).toBeTrue();
    });

    it('requires actual hours for time & material', async () => {
      await setup(inProgress({ invoicingPolicy: 'TIME_AND_MATERIAL' }));
      expect(component.canSubmitCompletion).toBeFalse();
      expect(component.completionErrors.join(' ')).toContain('Actual hours are required');
      component.actualHours = 3;
      expect(component.canSubmitCompletion).toBeTrue();
    });

    it('confirms with the FSD message, then posts the consumption', async () => {
      await setup(inProgress());
      service.complete.and.returnValue(ok(detail({ status: 'COMPLETED', allowedActions: ['CLOSE'] })));
      component.completionRows[0].consumed = 4;
      component.actualHours = 2.5;
      component.customerSignature = true;
      component.completionNotes = ' done ';
      component.confirmComplete();
      expect(confirm.calls.mostRecent().args[0].message).toBe(COMPLETE_CONFIRM_MESSAGE);
      expect(service.complete).toHaveBeenCalledWith('so-1', {
        consumedMaterials: [{ smrUuid: 'm1', consumedQuantity: 4 }], actualHours: 2.5, completionNotes: 'done', customerSignature: true
      });
      expect(component.order!.status).toBe('COMPLETED');
    });

    it('is hidden before work starts', async () => {
      await setup(detail({ status: 'READY', allowedActions: ['START'] }));
      expect(q('sec-completion')).toBeNull();
      expect(component.sections.map(s => s.id)).toEqual(['sec-details', 'sec-materials', 'sec-ledger']);
    });
  });

  describe('ledger', () => {
    it('shows consumption in the danger tone and returns (negative) in green, with the net per product', async () => {
      await setup(detail({ status: 'COMPLETED', allowedActions: ['CLOSE'], ledger: [
        { id: 1, entryType: 'DEBIT', productUuid: 'p1', variantUuid: 'v1', productName: 'Copper pipe', quantity: 5, uom: 'M', warehouseName: 'Main',
          sourceDocumentType: 'SERVICE_ISSUE', sourceDocumentNumber: 'SMI-1', movementType: 'SERVICE_ISSUE', transactionDate: '2026-10-12T09:00:00Z' },
        { id: 2, entryType: 'DEBIT', productUuid: 'p1', variantUuid: 'v1', productName: 'Copper pipe', quantity: -1.5, uom: 'M', warehouseName: 'Main',
          sourceDocumentType: 'SERVICE_RETURN', sourceDocumentNumber: 'SMI-2', movementType: 'SERVICE_RETURN', transactionDate: '2026-10-12T15:00:00Z' }
      ] }));
      const qty = qa('ledger-qty');
      expect(qty[0].classList).toContain('qty-out');
      expect(qty[1].classList).toContain('qty-back');
      const pills = qa('ledger-movement');
      expect(pills.map(text)).toEqual(['Consumed', 'Returned']);
      expect(pills[0].classList).toContain('er');
      expect(pills[1].classList).toContain('ok');
      expect(component.netByProduct).toEqual([{ variantUuid: 'v1', productName: 'Copper pipe', uom: 'M', netQuantity: 3.5 }]);
    });

    it('says so when nothing was consumed', async () => {
      await setup(detail());
      expect(text(q('ledger-empty'))).toBe('No material consumption for this service.');
    });
  });
});
