import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { BomFormComponent } from './bom-form.component';
import { BomService } from '../../../../services/bom.service';
import { InventoryService } from '../../../../services/inventory.service';
import { AttachmentService } from '../../../../services/attachment.service';

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

const SHIRT = { id: 1, uuid: 'p1', sku: 'TS-PRINT', name: 'Printed T-Shirt', status: 'ACTIVE', isBatchTracked: false, isSerialTracked: false,
  uomCode: 'PCS', productType: 'FINISHED_GOOD', supplyMethod: 'MANUFACTURE', createdDate: '', variantCount: 1 };
const PLAIN = { id: 2, uuid: 'mp1', sku: 'TS-PLAIN', name: 'Plain T-Shirt', status: 'ACTIVE', isBatchTracked: false, isSerialTracked: false,
  uomCode: 'PCS', productType: 'RAW_MATERIAL', supplyMethod: 'PURCHASE', createdDate: '', variantCount: 1 };

describe('BomFormComponent', () => {
  let fixture: ComponentFixture<BomFormComponent>;
  let component: BomFormComponent;
  let service: jasmine.SpyObj<BomService>;
  let inventory: jasmine.SpyObj<InventoryService>;
  let router: Router;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(uuid: string | null = null) {
    service = jasmine.createSpyObj<BomService>('BomService', ['createBom', 'updateBom', 'getBom']);
    service.createBom.and.returnValue(ok('b1'));
    service.updateBom.and.returnValue(ok(null));
    service.getBom.and.returnValue(ok({
      uuid: 'b1', bomNumber: 'BOM-2026-00001', productUuid: 'p1', productName: 'Printed T-Shirt', productSku: 'TS-PRINT',
      version: 1, status: 'DRAFT', baseQuantity: 10, baseUom: 'PCS', lineCount: 1, createdAt: '', updatedAt: '', traceId: 't', createdBy: 7,
      lines: [{ uuid: 'l1', sequence: 10, materialProductUuid: 'mp1', materialProductName: 'Plain T-Shirt', materialProductType: 'RAW_MATERIAL',
        materialSupplyMethod: 'PURCHASE', materialVariantUuid: 'mv1', materialSku: 'TS-PLAIN-1', materialVariantName: 'Default',
        quantity: 10, uom: 'PCS', scrapPercentage: 2, grossQuantity: 10.2, isCritical: true }]
    }));

    inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getManufacturableProducts', 'getProducts', 'getWarehouses', 'getProductById']);
    inventory.getManufacturableProducts.and.returnValue(ok({ data: [SHIRT], totalRecords: 1, page: 1, pageSize: 500, totalPages: 1 }));
    inventory.getProducts.and.returnValue(ok({ data: [PLAIN], totalRecords: 1, page: 1, pageSize: 500, totalPages: 1 }));
    inventory.getWarehouses.and.returnValue(ok([{ id: 1, uuid: 'w1', code: 'PLANT', name: 'Plant', isActive: true }]));
    inventory.getProductById.and.returnValue(ok({ ...SHIRT, variants: [{ uuid: 'pv1', sku: 'TS-PRINT-1', variantName: 'Default', isActive: true, isDefault: true, purchasePrice: 0 }] }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [BomFormComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: BomService, useValue: service },
        { provide: InventoryService, useValue: inventory },
        { provide: AttachmentService, useValue: { resolveUrl: (u: string) => u } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map(uuid ? [['uuid', uuid]] : []) } } }
      ]
    }).compileComponents();

    router = TestBed.inject(Router);
    spyOn(router, 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(BomFormComponent);
    component = fixture.componentInstance;
    spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  it('offers only manufacturable products as the output and production-available materials as inputs', async () => {
    await setup();

    expect(inventory.getManufacturableProducts).toHaveBeenCalledTimes(1);
    expect(inventory.getProducts).toHaveBeenCalledOnceWith({ activeOnly: true, pageSize: 500, availableFor: 'PRODUCTION' });
    expect(component.productOptions.map(o => o.value)).toEqual(['p1']);
    expect(component.lines.length).toBe(1, 'a new recipe starts with one empty line');
    expect(component.canSave).toBeFalse();
  });

  it('creates the recipe from the form, sending the lines in order', async () => {
    await setup();
    component.form.patchValue({ productUuid: 'p1', baseQuantity: 10, baseUom: 'PCS' });
    component.onMaterialSelected(0, { productUuid: 'mp1', productName: 'Plain T-Shirt', variantId: 5, variantUuid: 'mv1', variantSku: 'TS-PLAIN-1', variantName: 'Default', purchasePrice: 3, uomCode: 'PCS' });
    component.lines.at(0).patchValue({ quantity: 10, scrapPercentage: 2 });
    fixture.detectChanges();

    expect(component.canSave).toBeTrue();
    component.save();

    const sent = service.createBom.calls.mostRecent().args[0];
    expect(sent.productUuid).toBe('p1');
    expect(sent.baseQuantity).toBe(10);
    expect(sent.lines).toEqual([{ materialVariantUuid: 'mv1', quantity: 10, uom: 'PCS', scrapPercentage: 2, isCritical: true, notes: undefined, warehouseUuid: undefined, sequence: 10 }]);
    expect(router.navigate).toHaveBeenCalledWith(['/portal/pages/manufacturing/boms', 'b1']);
  });

  it('refuses the same material twice and the output as its own input before the server has to', async () => {
    await setup();
    component.form.patchValue({ productUuid: 'p1' });
    component.onMaterialSelected(0, { productUuid: 'mp1', productName: 'Plain', variantId: 5, variantUuid: 'mv1', variantSku: 's', variantName: 'Default', purchasePrice: 3, uomCode: 'PCS' });
    component.lines.at(0).patchValue({ quantity: 1 });
    component.addLine();
    component.onMaterialSelected(1, { productUuid: 'mp1', productName: 'Plain', variantId: 5, variantUuid: 'mv1', variantSku: 's', variantName: 'Default', purchasePrice: 3, uomCode: 'PCS' });
    component.lines.at(1).patchValue({ quantity: 2 });
    fixture.detectChanges();

    expect(component.lineProblem).toContain('more than one line');
    expect(component.canSave).toBeFalse();

    component.onMaterialSelected(1, { productUuid: 'p1', productName: 'Printed T-Shirt', variantId: 9, variantUuid: 'pv1', variantSku: 's', variantName: 'Default', purchasePrice: 0, uomCode: 'PCS' });
    fixture.detectChanges();
    expect(component.lineProblem).toContain('own recipe');
  });

  it('loads a draft for editing with its product fixed and saves the lines back', async () => {
    await setup('b1');

    expect(component.isEditMode).toBeTrue();
    expect(component.form.get('productUuid')?.disabled).toBeTrue();
    expect(component.form.getRawValue().productUuid).toBe('p1');
    expect(component.lines.length).toBe(1);
    expect(component.lines.at(0).value.materialVariantUuid).toBe('mv1');
    expect(component.lines.at(0).value.materialName).toBe('Plain T-Shirt – Default');

    component.lines.at(0).patchValue({ quantity: 12 });
    component.save();

    const sent = service.updateBom.calls.mostRecent().args;
    expect(sent[0]).toBe('b1');
    expect(sent[1].lines![0].quantity).toBe(12);
    expect(sent[1].clearEffectiveDates).toBeTrue();
    expect(router.navigate).toHaveBeenCalledWith(['/portal/pages/manufacturing/boms', 'b1']);
  });
});
