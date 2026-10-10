import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { BomManagerComponent } from './bom-manager.component';
import { BomDetail, BomListItem, BomService, bomUsageTag } from '../../../../../services/bom.service';
import { InventoryService } from '../../../../../services/inventory.service';
import { BusinessPartnerService } from '../../../../../services/business-partner.service';
import { AuthService } from '../../../../service/auth.service';
import { AttachmentService } from '../../../../../services/attachment.service';

/** A37 §3 (D-11, BOM-SHR-02/03/04) — BOM usage, preferFor on the list, read-only without FEATURE_BOM_MANAGEMENT. */
describe('BomManagerComponent — A37 usage and read-only mode', () => {
  let fixture: ComponentFixture<BomManagerComponent>;
  let component: BomManagerComponent;
  let el: HTMLElement;
  let bomService: jasmine.SpyObj<BomService>;

  const ok = (result: unknown) => of({ success: true, message: '', result } as any);
  const item = (uuid: string, status: string, bomUsage: string | null = null): BomListItem => ({
    uuid, bomNumber: 'BOM-' + uuid, productUuid: 'p-1', productName: 'Pump', productSku: 'P1', version: 1, status: status as any,
    baseQuantity: 1, baseUom: 'PCS', lineCount: 1, createdAt: '', updatedAt: '', bomUsage: bomUsage as any
  });
  const detail = (i: BomListItem): BomDetail => ({ ...i, traceId: 't', createdBy: 1, lines: [] });

  async function setup(opts: { boms?: BomListItem[]; readOnly?: boolean; isServiceBom?: boolean }) {
    const boms = opts.boms ?? [];
    bomService = jasmine.createSpyObj<BomService>('BomService', ['getBoms', 'getBom', 'createBom', 'updateBom', 'setUsage']);
    bomService.setUsage.and.returnValue(ok(null));
    bomService.getBoms.and.returnValue(ok({ data: boms }));
    bomService.getBom.and.callFake((uuid: string) => ok(detail(boms.find(b => b.uuid === uuid)!)));
    bomService.createBom.and.returnValue(ok('new-1'));
    bomService.updateBom.and.returnValue(ok(null));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts']);
    inventory.getProducts.and.returnValue(ok({ data: [] }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [BomManagerComponent],
      providers: [
        provideNoopAnimations(),
        { provide: BomService, useValue: bomService },
        { provide: InventoryService, useValue: inventory },
        { provide: BusinessPartnerService, useValue: { getPartners: () => ok({ data: [] }) } },
        { provide: AttachmentService, useValue: { resolveUrl: (u: string) => u } },
        { provide: AuthService, useValue: { hasPermission: () => true } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(BomManagerComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('isServiceBom', !!opts.isServiceBom);
    fixture.componentRef.setInput('isManufacturable', !opts.isServiceBom);
    fixture.componentRef.setInput('readOnly', !!opts.readOnly);
    fixture.componentRef.setInput('productUuid', 'p-1');
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('asks the list for this product\'s preferred usage (PRODUCTION / SERVICE)', async () => {
    await setup({});
    expect(bomService.getBoms).toHaveBeenCalledWith(jasmine.objectContaining({ productUuid: 'p-1', preferFor: 'PRODUCTION' }));
    await setup({ isServiceBom: true });
    expect(bomService.getBoms).toHaveBeenCalledWith(jasmine.objectContaining({ preferFor: 'SERVICE' }));
  });

  it('a new draft starts UNIVERSAL and sends the chosen usage', async () => {
    await setup({});
    expect(component.isNew).toBeTrue();
    expect(component.bomUsage).toBe('UNIVERSAL');
    expect(q('bom-usage')).not.toBeNull();
    component.onUsageChange('SERVICE_PREFERRED');
    component.lines.at(0).patchValue({ materialProductUuid: 'm-1', materialVariantUuid: 'mv-1', quantity: 1 });
    component.save();
    expect(bomService.createBom.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ bomUsage: 'SERVICE_PREFERRED' }));
    expect(bomService.updateBom).not.toHaveBeenCalled();
    expect(bomService.setUsage).not.toHaveBeenCalled();
  });

  it('on an ACTIVE BOM a usage change is saved on its own, at once', async () => {
    await setup({ boms: [item('b-1', 'ACTIVE', 'PRODUCTION_PREFERRED')] });
    expect(component.bomUsage).toBe('PRODUCTION_PREFERRED');
    expect(component.canChangeUsage).toBeTrue();
    component.onUsageChange('UNIVERSAL');
    expect(bomService.setUsage).toHaveBeenCalledWith('b-1', 'UNIVERSAL');
    expect(bomService.updateBom).not.toHaveBeenCalled();
  });

  it('an OBSOLETE BOM keeps its usage', async () => {
    await setup({ boms: [item('b-1', 'OBSOLETE')] });
    expect(component.canChangeUsage).toBeFalse();
    component.onUsageChange('SERVICE_PREFERRED');
    expect(bomService.updateBom).not.toHaveBeenCalled();
    expect(bomService.setUsage).not.toHaveBeenCalled();
  });

  it('tags non-universal BOMs in the list', async () => {
    await setup({ boms: [item('b-1', 'ACTIVE', 'SERVICE_PREFERRED'), item('b-2', 'DRAFT')] });
    expect(q('bom-usage-b-1')!.textContent).toContain('Service');
    expect(q('bom-usage-b-2')).toBeNull();
    expect(bomUsageTag('PRODUCTION_PREFERRED')).toBe('Production');
    expect(bomUsageTag('UNIVERSAL')).toBeNull();
  });

  it('read-only (no FEATURE_BOM_MANAGEMENT): no drafting, no actions, says so', async () => {
    await setup({ readOnly: true });
    expect(component.isNew).toBeFalse();
    expect(q('new-bom')).toBeNull();
    expect(q('bom-readonly')!.textContent).toContain('switched off');
    expect(component.needsFirstBom).toBeFalse();

    await setup({ readOnly: true, boms: [item('b-1', 'DRAFT')] });
    expect(component.canEdit).toBeFalse();
    expect(component.canSubmit).toBeFalse();
    expect(component.canChangeUsage).toBeFalse();
    component.startNewBom();
    expect(component.isNew).toBeFalse();
  });
});
