import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { DeliveryCreateComponent } from './delivery-create.component';
import { LogisticsService } from '../../../../services/logistics.service';
import { DemandService, PoListItemModel, PoDetailModel } from '../../../../services/demand.service';
import { WarehouseService, SroListItemModel, SroDetailModel } from '../../../../services/warehouse.service';
import { MaterialService, MivListItem, MivDetail } from '../../../../services/material.service';
import { InventoryService, WarehouseModel } from '../../../../services/inventory.service';

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

function poRow(overrides: Partial<PoListItemModel> = {}): PoListItemModel {
  return {
    uuid: 'po-1', poNumber: 'PO-2026-00001', supplierId: 's1', supplierName: 'Acme',
    status: 'APPROVED', totalAmount: 1000, createdDate: '2026-09-01T00:00:00Z',
    ...overrides
  };
}

function sroRow(overrides: Partial<SroListItemModel> = {}): SroListItemModel {
  return {
    uuid: 'sro-1', sroNumber: 'SRO-2026-00001', sroType: 'DAMAGED', supplierId: 's1',
    supplierName: 'Acme', status: 'APPROVED', returnReason: 'Damaged',
    createdDate: '2026-09-01T00:00:00Z', totalLines: 1,
    ...overrides
  };
}

function mivRow(overrides: Partial<MivListItem> = {}): MivListItem {
  return {
    uuid: 'miv-1', issueNo: 'MIV-2026-00001', mirRequestNo: 'MIR-2026-00001', mirUuid: 'mir-1',
    status: 'POSTED', issueDate: '2026-09-01T00:00:00Z', totalValue: 500, totalLines: 1,
    createdDate: '2026-09-01T00:00:00Z',
    ...overrides
  };
}

function warehouse(overrides: Partial<WarehouseModel> = {}): WarehouseModel {
  return {
    id: 1, uuid: 'wh-1', code: 'MAIN', name: 'Main Warehouse', isActive: true,
    createdDate: '2026-01-01T00:00:00Z', ...overrides
  };
}

describe('DeliveryCreateComponent', () => {
  let fixture: ComponentFixture<DeliveryCreateComponent>;
  let component: DeliveryCreateComponent;
  let logistics: jasmine.SpyObj<LogisticsService>;
  let demand: jasmine.SpyObj<DemandService>;
  let warehouseSvc: jasmine.SpyObj<WarehouseService>;
  let material: jasmine.SpyObj<MaterialService>;
  let inventory: jasmine.SpyObj<InventoryService>;
  let router: Router;

  async function setup() {
    logistics = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'createDelivery', 'createDeliveryFromSource'
    ]);
    demand = jasmine.createSpyObj<DemandService>('DemandService', ['getPos', 'getPoById']);
    warehouseSvc = jasmine.createSpyObj<WarehouseService>('WarehouseService', ['getSros', 'getSroById']);
    material = jasmine.createSpyObj<MaterialService>('MaterialService', ['getMivs', 'getMiv']);
    inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getWarehouses']);

    demand.getPos.and.returnValue(ok({ data: [poRow()], totalRecords: 1, page: 1, pageSize: 200, totalPages: 1 }));
    warehouseSvc.getSros.and.returnValue(ok({ data: [sroRow()], totalRecords: 1, page: 1, pageSize: 200, totalPages: 1 }));
    material.getMivs.and.returnValue(ok({ data: [mivRow()], totalRecords: 1, page: 1, pageSize: 200, totalPages: 1 }));
    inventory.getWarehouses.and.returnValue(ok([warehouse(), warehouse({ uuid: 'wh-2', code: 'SITE', name: 'Site Store' })]));

    await TestBed.configureTestingModule({
      imports: [DeliveryCreateComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: logistics },
        { provide: DemandService, useValue: demand },
        { provide: WarehouseService, useValue: warehouseSvc },
        { provide: MaterialService, useValue: material },
        { provide: InventoryService, useValue: inventory }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(DeliveryCreateComponent);
    component = fixture.componentInstance;
    router = TestBed.inject(Router);
    fixture.detectChanges();
  }

  // ── Loading source options ───────────────────────────────────────────────────

  it('loads warehouses on open, for the transfer mode', async () => {
    await setup();
    expect(inventory.getWarehouses).toHaveBeenCalled();
    expect(component.warehouseOptions.length).toBe(2);
  });

  it('loads purchase orders filtered to statuses that can actually be advised', async () => {
    await setup();
    component.mode = 'PO';
    component.onModeChange();

    expect(demand.getPos).toHaveBeenCalled();
    expect(component.poOptions).toEqual([{ label: 'PO-2026-00001 — Acme', value: 'po-1' }]);
  });

  it('does not offer a purchase order in a status the server would refuse', async () => {
    await setup();
    demand.getPos.and.returnValue(ok({
      data: [poRow({ uuid: 'draft', status: 'DRAFT' }), poRow({ uuid: 'closed', status: 'CLOSED' })],
      totalRecords: 2, page: 1, pageSize: 200, totalPages: 1
    }));

    component.mode = 'PO';
    component.onModeChange();

    expect(component.poOptions.length).toBe(0);
  });

  it('loads supplier returns filtered to APPROVED', async () => {
    await setup();
    component.mode = 'SRO';
    component.onModeChange();

    expect(warehouseSvc.getSros).toHaveBeenCalledWith(
      jasmine.objectContaining({ status: 'APPROVED' }));
    expect(component.sroOptions).toEqual([{ label: 'SRO-2026-00001 — Acme', value: 'sro-1' }]);
  });

  it('loads material issue vouchers filtered to POSTED', async () => {
    await setup();
    component.mode = 'MIV';
    component.onModeChange();

    expect(material.getMivs).toHaveBeenCalledWith(
      jasmine.objectContaining({ status: 'POSTED' }));
    expect(component.mivOptions).toEqual([{ label: 'MIV-2026-00001 — MIR-2026-00001', value: 'miv-1' }]);
  });

  // ── Source preview ────────────────────────────────────────────────────────────

  it('previews a purchase order\'s lines before committing to them', async () => {
    await setup();
    demand.getPoById.and.returnValue(ok({
      uuid: 'po-1', poNumber: 'PO-2026-00001', supplierId: 's1', supplierName: 'Acme',
      status: 'APPROVED', totalAmount: 100, createdBy: 1, createdDate: '2026-09-01T00:00:00Z',
      lines: [{
        uuid: 'l1', lineNo: 1, itemDescription: 'Steel bolts', quantity: 50, unitPrice: 2,
        lineTotal: 100
      } as any]
    } as PoDetailModel));

    component.mode = 'PO';
    component.onModeChange();
    component.sourceUuid = 'po-1';
    component.onSourceSelected();

    expect(component.previewLines).toEqual([{ description: 'Steel bolts', qty: 50, uom: undefined }]);
    expect(component.previewTotalQty).toBe(50);
  });

  it('drops SRO lines with nothing left to return from the preview', async () => {
    await setup();
    warehouseSvc.getSroById.and.returnValue(ok({
      uuid: 'sro-1', sroNumber: 'SRO-2026-00001', sroType: 'DAMAGED', supplierId: 's1',
      supplierName: 'Acme', status: 'APPROVED', returnReason: 'Damaged',
      lines: [
        { uuid: 'l1', lineNo: 1, itemDescription: 'Broken crate', qtyToReturn: 3, returnReason: 'Damaged' },
        { uuid: 'l2', lineNo: 2, itemDescription: 'Already returned', qtyToReturn: 0, returnReason: 'Damaged' }
      ]
    } as SroDetailModel));

    component.mode = 'SRO';
    component.onModeChange();
    component.sourceUuid = 'sro-1';
    component.onSourceSelected();

    expect(component.previewLines).toEqual([{ description: 'Broken crate', qty: 3, uom: undefined }]);
  });

  it('drops MIV lines with nothing issued from the preview', async () => {
    await setup();
    material.getMiv.and.returnValue(ok({
      uuid: 'miv-1', issueNo: 'MIV-2026-00001', mirUuid: 'mir-1', mirRequestNo: 'MIR-2026-00001',
      status: 'POSTED', issueDate: '2026-09-01T00:00:00Z', totalValue: 100, createdBy: 1,
      createdDate: '2026-09-01T00:00:00Z',
      lines: [{ uuid: 'l1', mirLineUuid: 'ml1', variantUuid: 'v1', itemDescription: 'Cable', issuedQty: 10, unitCost: 5, lineValue: 50, batchSerials: [] }]
    } as MivDetail));

    component.mode = 'MIV';
    component.onModeChange();
    component.sourceUuid = 'miv-1';
    component.onSourceSelected();

    expect(component.previewLines).toEqual([{ description: 'Cable', qty: 10, uom: undefined }]);
  });

  // ── Manual line editing ───────────────────────────────────────────────────────

  it('starts with one blank line and can add and remove more', async () => {
    await setup();
    expect(component.lines.length).toBe(1);

    component.addLine();
    expect(component.lines.length).toBe(2);

    component.removeLine(0);
    expect(component.lines.length).toBe(1);
  });

  it('removing the last line leaves one blank one rather than none', async () => {
    await setup();
    component.removeLine(0);
    expect(component.lines.length).toBe(1);
    expect(component.lines[0].itemDescription).toBe('');
  });

  // ── Validation, mirroring the server's own rules ──────────────────────────────

  it('refuses to save manual with no real lines', async () => {
    await setup();
    component.mode = 'MANUAL';
    expect(component.validationError).toContain('at least one line');
  });

  it('refuses a line with no quantity', async () => {
    await setup();
    component.lines[0].itemDescription = 'Steel bolts';
    component.lines[0].qtyOrdered = 0;
    expect(component.validationError).toContain('greater than zero');
  });

  it('a manual delivery with a real line and a direction validates', async () => {
    await setup();
    component.lines[0].itemDescription = 'Steel bolts';
    component.lines[0].qtyOrdered = 10;
    expect(component.validationError).toBeNull();
  });

  it('refuses a transfer with no warehouses, or the same one twice', async () => {
    await setup();
    component.mode = 'TRANSFER';
    component.lines[0].itemDescription = 'Pallet';
    component.lines[0].qtyOrdered = 1;

    expect(component.validationError).toContain('both a source and a destination warehouse');

    component.shipFromWarehouseUuid = 'wh-1';
    component.shipToWarehouseUuid = 'wh-1';
    expect(component.validationError).toContain('must differ');

    component.shipToWarehouseUuid = 'wh-2';
    expect(component.validationError).toBeNull();
  });

  it('refuses a from-source mode with nothing selected', async () => {
    await setup();
    component.mode = 'PO';
    expect(component.validationError).toContain('purchase order');
  });

  // ── Saving ────────────────────────────────────────────────────────────────────

  it('creates a manual delivery with the direction and the typed lines', async () => {
    await setup();
    logistics.createDelivery.and.returnValue(ok('new-uuid'));

    component.lines[0].itemDescription = 'Steel bolts';
    component.lines[0].qtyOrdered = 10;
    component.lines[0].unitOfMeasure = 'EA';
    component.direction = 'OUTBOUND';

    const navigateSpy = spyOn(router, 'navigate');
    component.save();

    expect(logistics.createDelivery).toHaveBeenCalledWith(jasmine.objectContaining({
      sourceType: 'MANUAL',
      direction: 'OUTBOUND',
      lines: [jasmine.objectContaining({ itemDescription: 'Steel bolts', qtyOrdered: 10, unitOfMeasure: 'EA' })]
    }));
    expect(navigateSpy).toHaveBeenCalledWith(['/portal/pages/logistics/deliveries', 'new-uuid']);
  });

  it('omits an incomplete ship-to address rather than sending a partial one', async () => {
    await setup();
    logistics.createDelivery.and.returnValue(ok('new-uuid'));

    component.lines[0].itemDescription = 'Steel bolts';
    component.lines[0].qtyOrdered = 10;
    component.shipToAddress.line1 = 'Only a street, nothing else';

    component.save();

    const req = logistics.createDelivery.calls.mostRecent().args[0];
    expect(req.shipToAddress).toBeUndefined();
  });

  it('sends a ship-to address once it is actually complete', async () => {
    await setup();
    logistics.createDelivery.and.returnValue(ok('new-uuid'));

    component.lines[0].itemDescription = 'Steel bolts';
    component.lines[0].qtyOrdered = 10;
    component.shipToAddress = { line1: '12 Main St', cityName: 'Lahore', countryName: 'Pakistan' };

    component.save();

    const req = logistics.createDelivery.calls.mostRecent().args[0];
    expect(req.shipToAddress).toEqual({ line1: '12 Main St', cityName: 'Lahore', countryName: 'Pakistan' });
  });

  it('creates a transfer with both warehouses and no address at all', async () => {
    await setup();
    logistics.createDelivery.and.returnValue(ok('new-uuid'));

    component.mode = 'TRANSFER';
    component.shipFromWarehouseUuid = 'wh-1';
    component.shipToWarehouseUuid = 'wh-2';
    component.lines[0].itemDescription = 'Pallet';
    component.lines[0].qtyOrdered = 1;

    component.save();

    const req = logistics.createDelivery.calls.mostRecent().args[0];
    expect(req.sourceType).toBe('TRANSFER');
    expect(req.shipFromWarehouseUuid).toBe('wh-1');
    expect(req.shipToWarehouseUuid).toBe('wh-2');
    expect(req.direction).toBeUndefined();
    expect(req.shipToAddress).toBeUndefined();
  });

  it('creates a delivery from a purchase order, omitting lines so everything outstanding is advised', async () => {
    await setup();
    logistics.createDeliveryFromSource.and.returnValue(ok('new-uuid'));

    component.mode = 'PO';
    component.sourceUuid = 'po-1';

    component.save();

    expect(logistics.createDeliveryFromSource).toHaveBeenCalledWith(jasmine.objectContaining({
      sourceType: 'PO', sourceUuid: 'po-1'
    }));
    const req = logistics.createDeliveryFromSource.calls.mostRecent().args[0];
    expect(req.lines).toBeUndefined();
  });

  it('forwards a ship-to address on a from-source delivery too, not just MANUAL/TRANSFER', async () => {
    await setup();
    logistics.createDeliveryFromSource.and.returnValue(ok('new-uuid'));

    component.mode = 'MIV';
    component.sourceUuid = 'miv-1';
    component.shipToAddress = { line1: '221 Ferozepur Road', cityName: 'Lahore', countryName: 'Pakistan' };

    component.save();

    const req = logistics.createDeliveryFromSource.calls.mostRecent().args[0];
    expect(req.shipToAddress).toEqual({ line1: '221 Ferozepur Road', cityName: 'Lahore', countryName: 'Pakistan' });
  });

  it('forwards a ship-from address on a from-source delivery only when the toggle is on', async () => {
    await setup();
    logistics.createDeliveryFromSource.and.returnValue(ok('new-uuid'));

    component.mode = 'MIV';
    component.sourceUuid = 'miv-1';
    component.shipFromAddress = { line1: 'Plot 14', cityName: 'Faisalabad', countryName: 'Pakistan' };
    component.useShipFromAddress = false;

    component.save();
    expect(logistics.createDeliveryFromSource.calls.mostRecent().args[0].shipFromAddress).toBeUndefined();

    component.useShipFromAddress = true;
    component.save();
    expect(logistics.createDeliveryFromSource.calls.mostRecent().args[0].shipFromAddress)
      .toEqual({ line1: 'Plot 14', cityName: 'Faisalabad', countryName: 'Pakistan' });
  });

  it('does not call the server at all when the form is invalid', async () => {
    await setup();
    logistics.createDelivery.and.returnValue(ok('new-uuid'));

    component.save();

    expect(logistics.createDelivery).not.toHaveBeenCalled();
    expect(logistics.createDeliveryFromSource).not.toHaveBeenCalled();
  });

  it('shows the server\'s own refusal and stops saving, without navigating away', async () => {
    await setup();
    logistics.createDelivery.and.returnValue(
      throwError(() => ({ error: { message: 'A delivery must have at least one line.' } })));

    component.lines[0].itemDescription = 'Steel bolts';
    component.lines[0].qtyOrdered = 10;

    const navigateSpy = spyOn(router, 'navigate');
    component.save();

    expect(component.isSaving).toBeFalse();
    expect(navigateSpy).not.toHaveBeenCalled();
  });
});
