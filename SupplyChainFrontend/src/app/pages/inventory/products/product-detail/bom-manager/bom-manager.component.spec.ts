import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { BomManagerComponent } from './bom-manager.component';
import { BomService } from '../../../../../services/bom.service';
import { InventoryService } from '../../../../../services/inventory.service';
import { BusinessPartnerService } from '../../../../../services/business-partner.service';
import { AttachmentService } from '../../../../../services/attachment.service';
import { AuthService } from '../../../../service/auth.service';

/** A36-P1-09 (D-4) — service BOM lines: Source, subcontract supplier picker, labor hours; manufacturing lines stay STOCK. */
describe('BomManagerComponent — A36 service BOM line source', () => {
  let fixture: ComponentFixture<BomManagerComponent>;
  let component: BomManagerComponent;
  let el: HTMLElement;
  let bomService: jasmine.SpyObj<BomService>;
  let inventory: jasmine.SpyObj<InventoryService>;
  let partners: jasmine.SpyObj<BusinessPartnerService>;

  const ok = (result: unknown) => of({ success: true, message: '', result } as any);

  async function setup(isServiceBom: boolean) {
    bomService = jasmine.createSpyObj<BomService>('BomService', ['getBoms', 'getBom', 'createBom', 'updateBom']);
    bomService.getBoms.and.returnValue(ok({ data: [] }));
    bomService.createBom.and.returnValue(ok('bom-1'));
    inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts']);
    inventory.getProducts.and.returnValue(ok({ data: [] }));
    partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(ok({ data: [
      { uuid: 'bp-1', companyName: 'Acme Repairs', partnerType: 'VENDOR', isVendor: true }
    ] }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [BomManagerComponent],
      providers: [
        provideNoopAnimations(),
        { provide: BomService, useValue: bomService },
        { provide: InventoryService, useValue: inventory },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: AttachmentService, useValue: { resolveUrl: (u: string) => u } },
        { provide: AuthService, useValue: { hasPermission: () => true } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(BomManagerComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('isServiceBom', isServiceBom);
    fixture.componentRef.setInput('productUuid', 'p-svc');
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  function fillLine(index: number, patch: Record<string, unknown> = {}) {
    component.lines.at(index).patchValue({ materialProductUuid: 'm-1', materialVariantUuid: 'mv-1', quantity: 2, ...patch });
  }

  it('offers Source only on a service BOM; the supplier picker appears only for SUBCONTRACT', async () => {
    await setup(true);
    expect(el.querySelector('[data-testid="line-source-0"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="line-supplier-0"]')).toBeNull();

    component.lines.at(0).patchValue({ sourceType: 'SUBCONTRACT' });
    component.onSourceChange(0);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="line-supplier-0"]')).not.toBeNull();
    expect(component.vendorOptions).toEqual([{ label: 'Acme Repairs', value: 'bp-1' }]);
    expect(partners.getPartners).toHaveBeenCalledWith(jasmine.objectContaining({ isVendor: true }));

    component.lines.at(0).patchValue({ sourceType: 'INTERNAL_LABOR' });
    component.onSourceChange(0);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="line-supplier-0"]')).toBeNull();
    expect(el.querySelector('[data-testid="line-qty-label-0"]')!.textContent).toContain('Hours');
    expect(component.lines.at(0).get('uom')!.value).toBe('HR');
  });

  it('leaving SUBCONTRACT clears the supplier', async () => {
    await setup(true);
    component.lines.at(0).patchValue({ sourceType: 'SUBCONTRACT', subcontractSupplierUuid: 'bp-1' });
    component.lines.at(0).patchValue({ sourceType: 'STOCK' });
    component.onSourceChange(0);
    expect(component.lines.at(0).get('subcontractSupplierUuid')!.value).toBeNull();
  });

  it('requires a supplier on a SUBCONTRACT line, then sends sourceType and subcontractSupplierUuid', async () => {
    await setup(true);
    fillLine(0, { sourceType: 'SUBCONTRACT' });
    expect(component.lineProblem).toBe('Subcontract supplier is required for subcontracted BOM lines.');
    expect(component.canSave).toBeFalse();

    component.lines.at(0).patchValue({ subcontractSupplierUuid: 'bp-1' });
    component.addLine();
    component.lines.at(1).patchValue({ materialProductUuid: 'm-2', materialVariantUuid: 'mv-2', quantity: 3, sourceType: 'INTERNAL_LABOR' });
    component.save();

    const lines = bomService.createBom.calls.mostRecent().args[0].lines;
    expect(lines[0]).toEqual(jasmine.objectContaining({ sourceType: 'SUBCONTRACT', subcontractSupplierUuid: 'bp-1' }));
    expect(lines[1]).toEqual(jasmine.objectContaining({ sourceType: 'INTERNAL_LABOR', subcontractSupplierUuid: null }));
  });

  it('manufacturing BOMs keep their lines as they were: no Source, always STOCK, Production materials', async () => {
    await setup(false);
    expect(el.querySelector('[data-testid="line-source-0"]')).toBeNull();
    expect(partners.getPartners).not.toHaveBeenCalled();
    expect(inventory.getProducts).toHaveBeenCalledWith(jasmine.objectContaining({ availableFor: 'PRODUCTION' }));

    fillLine(0, { sourceType: 'SUBCONTRACT', subcontractSupplierUuid: 'bp-1' });
    component.save();
    const line = bomService.createBom.calls.mostRecent().args[0].lines[0];
    expect(line).toEqual(jasmine.objectContaining({ sourceType: 'STOCK', subcontractSupplierUuid: null }));
  });

  it('shows the server refusal message when the save is refused', async () => {
    await setup(true);
    bomService.createBom.and.returnValue(throwError(() => ({ error: { message: 'This product does not have service BOM enabled' } })));
    const toast = spyOn((component as any).messageService, 'add');
    fillLine(0);
    component.save();
    expect(toast).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'This product does not have service BOM enabled' }));
  });
});
