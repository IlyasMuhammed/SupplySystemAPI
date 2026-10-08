import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { LeadTimesTabComponent } from './lead-times-tab.component';
import {
  LeadTimeService, ManufacturingLeadTimeNodeModel, VariantLeadTimeComponentModel, VariantLeadTimesModel
} from '../../../../../services/lead-time.service';
import { ProductVariantModel } from '../../../../../services/inventory.service';

type Cat = 'STOCK' | 'MANUFACTURE';

function component(code: string, field: string, name: string, days: number, source: string,
                   over: Partial<VariantLeadTimeComponentModel> = {}): VariantLeadTimeComponentModel {
  return {
    code, field, name, storedDays: null, resolvedDays: days, source, defaultDays: days, defaultSource: source,
    visible: true, includedInTotal: true, ...over
  };
}

/** API-CONTRACT §4.5, as INV builds it: always all eight; visibility and includedInTotal judged on the route. */
function model(category: Cat, requiresShipping = true, variantUuid = 'v-1'): VariantLeadTimesModel {
  const mfg = category === 'MANUFACTURE';
  const components = [
    component('SUPPLIER', 'supplierLeadTimeDays', 'Supplier lead time', 7, 'SUPPLIER_RATE',
      { detail: 'ACME Ltd (preferred)', includedInTotal: !mfg }),
    component('MANUFACTURING', 'manufacturingLeadTimeDays', 'Manufacturing', 5, 'VARIANT',
      { storedDays: 5, defaultDays: 1, defaultSource: 'SYSTEM_DEFAULT', visible: mfg, includedInTotal: mfg }),
    component('MFG_BUFFER', 'manufacturingBufferDays', 'Manufacturing buffer', 0, 'ORG_DEFAULT', { visible: mfg, includedInTotal: mfg }),
    component('QC', 'qualityInspectionDays', 'Quality inspection', 0, 'ORG_DEFAULT'),
    component('TRANSFER', 'internalTransferDays', 'Internal transfer', 0, 'ORG_DEFAULT'),
    component('PICK_PACK', 'pickPackDays', 'Pick & pack', 2, 'VARIANT', { storedDays: 2, defaultDays: 1, defaultSource: 'ORG_DEFAULT' }),
    component('SHIPPING', 'shippingLeadTimeDays', 'Shipping', 3, 'ORG_DEFAULT', { visible: requiresShipping, includedInTotal: requiresShipping }),
    component('SALES_BUFFER', 'salesBufferDays', 'Sales safety buffer', 1, 'ORG_DEFAULT')
  ];
  return {
    variantUuid, productId: 7, sku: 'GA-1', variantName: 'Type A',
    routeUuid: mfg ? 'r-mfg' : 'r-ps', routeCode: mfg ? 'MFG_PICK_PACK_SHIP' : 'PICK_AND_SHIP',
    routeName: mfg ? 'Manufacture → Pick, Pack & Ship' : 'Pick & Ship', routeCategory: category, routeFromOrgDefault: false,
    requiresShipping, components,
    totalDays: components.filter(c => c.includedInTotal).reduce((s, c) => s + c.resolvedDays, 0)
  };
}

const variant = (uuid: string, name: string, isDefault = false) =>
  ({ id: 1, uuid, sku: 'SKU-' + uuid, variantName: name, purchasePrice: 1, isDefault, isActive: true } as ProductVariantModel);

const BOM: ManufacturingLeadTimeNodeModel = {
  variantUuid: 'v-1', displayName: 'Gear assembly — Type A', quantity: 10, levelDays: 2, levelDaysSource: 'VARIANT',
  bomUuid: 'b-1', bomNumber: 'BOM-0012', bomVersion: 2, totalDays: 9,
  inputs: [
    { variantUuid: 'm-1', displayName: 'Steel rod', requiredQty: 20, freeQty: 5, shortfallQty: 15, isManufactured: false,
      waitDays: 7, source: 'SUPPLIER_RATE', detail: 'ACME Ltd (preferred)' },
    { variantUuid: 'm-2', displayName: 'Bolt', requiredQty: 40, freeQty: 100, shortfallQty: 0, isManufactured: false,
      waitDays: 0, source: 'IN_STOCK' }
  ],
  warnings: ['No active BOM for Gear housing']
};

describe('LeadTimesTabComponent (A34-PB-07, D-26)', () => {
  let fixture: ComponentFixture<LeadTimesTabComponent>;
  let cmp: LeadTimesTabComponent;
  let service: jasmine.SpyObj<LeadTimeService>;
  let el: HTMLElement;
  let current: VariantLeadTimesModel;

  const ok = (result: unknown) => of({ success: true, message: '', result } as any);

  async function setup(m: VariantLeadTimesModel, inputs: { canEdit?: boolean; canCalculate?: boolean; productManufactured?: boolean;
                                                           variants?: ProductVariantModel[] } = {}) {
    current = m;
    service = jasmine.createSpyObj<LeadTimeService>('LeadTimeService',
      ['getVariantLeadTimes', 'updateVariantLeadTimes', 'calculateManufacturing']);
    service.getVariantLeadTimes.and.callFake(uuid => ok({ ...current, variantUuid: uuid }));
    service.updateVariantLeadTimes.and.callFake((uuid, req) => ok({ ...current, variantUuid: uuid, totalDays: 99 }));
    service.calculateManufacturing.and.returnValue(ok(BOM));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [LeadTimesTabComponent],
      providers: [provideNoopAnimations(), { provide: LeadTimeService, useValue: service }]
    }).compileComponents();
    fixture = TestBed.createComponent(LeadTimesTabComponent);
    cmp = fixture.componentInstance;
    fixture.componentRef.setInput('variants', inputs.variants ?? [variant('v-0', 'Type 0'), variant('v-1', 'Type A', true)]);
    fixture.componentRef.setInput('canEdit', inputs.canEdit ?? true);
    fixture.componentRef.setInput('canCalculate', inputs.canCalculate ?? true);
    fixture.componentRef.setInput('productManufactured', inputs.productManufactured ?? m.routeCategory === 'MANUFACTURE');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const rows = () => Array.from(el.querySelectorAll('[data-testid^="lt-row-"]')).map(r => r.getAttribute('data-testid')!.replace('lt-row-', ''));
  const refresh = async () => { fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges(); };
  const lastPut = () => service.updateVariantLeadTimes.calls.mostRecent().args[1] as Record<string, number | null>;

  it('opens on the default variant and shows only the components its route uses (BR-C3-04/05)', async () => {
    await setup(model('STOCK'));
    expect(service.getVariantLeadTimes).toHaveBeenCalledWith('v-1');
    expect(rows()).toEqual(['SUPPLIER', 'QC', 'TRANSFER', 'PICK_PACK', 'SHIPPING', 'SALES_BUFFER']);
    expect(q('lt-route')!.textContent).toContain('Pick & Ship');
    expect(q('lt-route')!.textContent).toContain('Stock');
    expect(q('lt-hidden-note')!.textContent).toContain('Manufacturing');
  });

  it('a MANUFACTURE route shows the manufacturing components; a route without SHIP hides shipping', async () => {
    await setup(model('MANUFACTURE', false));
    expect(rows()).toEqual(['SUPPLIER', 'MANUFACTURING', 'MFG_BUFFER', 'QC', 'TRANSFER', 'PICK_PACK', 'SALES_BUFFER']);
    expect(q('lt-hidden-note')!.textContent).toContain('Shipping');
  });

  it('shows each component\'s source badge and detail', async () => {
    await setup(model('STOCK'));
    expect(q('lt-source-SUPPLIER')!.textContent).toContain('Supplier');
    expect(q('lt-row-SUPPLIER')!.textContent).toContain('ACME Ltd (preferred)');
    expect(q('lt-source-PICK_PACK')!.textContent).toContain('Variant override');
    expect(q('lt-source-SHIPPING')!.textContent).toContain('Org default');
  });

  it('totals the counted components and follows edits live: an override, then a cleared one falling back to the default', async () => {
    await setup(model('STOCK'));
    expect(q('lt-total')!.textContent).toContain('13');
    cmp.onDaysChange('shippingLeadTimeDays', 5);
    await refresh();
    expect(q('lt-total')!.textContent).toContain('15');
    expect(q('lt-source-SHIPPING')!.textContent).toContain('Variant override');
    cmp.onDaysChange('pickPackDays', null);
    await refresh();
    expect(q('lt-total')!.textContent).toContain('14');
    expect(q('lt-source-PICK_PACK')!.textContent).toContain('Org default');
  });

  it('on a MANUFACTURE route the supplier lead is shown but not counted', async () => {
    await setup(model('MANUFACTURE'));
    expect(q('lt-total')!.textContent).toContain('11');
    expect(q('lt-row-SUPPLIER')!.textContent).toContain('not counted');
  });

  it('saves all eight values, keeping the hidden ones as stored (the PUT replaces all eight)', async () => {
    await setup(model('STOCK'));
    expect(cmp.dirty).toBeFalse();
    cmp.onDaysChange('qualityInspectionDays', 1);
    await refresh();
    expect(cmp.dirty).toBeTrue();
    cmp.save();
    expect(service.updateVariantLeadTimes).toHaveBeenCalledTimes(1);
    expect(service.updateVariantLeadTimes.calls.mostRecent().args[0]).toBe('v-1');
    expect(lastPut()).toEqual({
      supplierLeadTimeDays: null, manufacturingLeadTimeDays: 5, manufacturingBufferDays: null, qualityInspectionDays: 1,
      internalTransferDays: null, pickPackDays: 2, shippingLeadTimeDays: null, salesBufferDays: null
    });
    await refresh();
    expect(q('lt-total')!.textContent).toContain('99');
    expect(cmp.dirty).toBeFalse();
  });

  it('"Reset to org defaults" clears every override; Save then sends all eight as null', async () => {
    await setup(model('STOCK'));
    cmp.resetToDefaults();
    await refresh();
    expect(q('lt-source-PICK_PACK')!.textContent).toContain('Org default');
    cmp.save();
    expect(Object.values(lastPut()).every(v => v === null)).toBeTrue();
    expect(Object.keys(lastPut()).length).toBe(8);
  });

  it('Discard puts the saved values back', async () => {
    await setup(model('STOCK'));
    cmp.onDaysChange('pickPackDays', 9);
    cmp.discard();
    await refresh();
    expect(cmp.dirty).toBeFalse();
    expect(q('lt-total')!.textContent).toContain('13');
  });

  it('refuses a value outside 0–3650 before saving', async () => {
    await setup(model('STOCK'));
    cmp.onDaysChange('shippingLeadTimeDays', 4000);
    cmp.save();
    expect(service.updateVariantLeadTimes).not.toHaveBeenCalled();
    await refresh();
    expect(q('lt-problem')!.textContent).toContain('between 0 and 3650');
  });

  it('shows the server\'s refusal', async () => {
    await setup(model('STOCK'));
    service.updateVariantLeadTimes.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Shipping days must be between 0 and 3650.' } })));
    cmp.onDaysChange('shippingLeadTimeDays', 4);
    cmp.save();
    await refresh();
    expect(q('lt-save-error')!.textContent).toContain('Shipping days must be between 0 and 3650.');
  });

  it('is read-only without STOCK_MANAGE: no Save or Reset, and nothing is sent', async () => {
    await setup(model('STOCK'), { canEdit: false });
    expect(q('lt-save')).toBeNull();
    expect(q('lt-reset')).toBeNull();
    expect(q('lt-readonly')).not.toBeNull();
    expect(el.querySelector('[data-testid="lt-input-PICK_PACK"] input')!.hasAttribute('disabled')).toBeTrue();
    cmp.onDaysChange('pickPackDays', 4);
    cmp.save();
    expect(service.updateVariantLeadTimes).not.toHaveBeenCalled();
  });

  it('"Recalculate from BOM" shows the BOM-aware total and never writes', async () => {
    await setup(model('MANUFACTURE'));
    cmp.bomQuantity = 10;
    cmp.recalculate();
    await refresh();
    expect(service.calculateManufacturing).toHaveBeenCalledWith({ variantUuid: 'v-1', quantity: 10 });
    const result = q('lt-bom-result')!.textContent!;
    expect(result).toContain('9');
    expect(result).toContain('BOM-0012');
    expect(result).toContain('Steel rod');
    expect(q('lt-bom-warnings')!.textContent).toContain('No active BOM for Gear housing');
    expect(service.updateVariantLeadTimes).not.toHaveBeenCalled();
  });

  it('offers "Recalculate from BOM" only on a MANUFACTURE route of a manufactured product, with a calculate permission', async () => {
    await setup(model('STOCK'));
    expect(q('lt-recalc')).toBeNull();
    await setup(model('MANUFACTURE'), { canCalculate: false });
    expect(q('lt-recalc')).toBeNull();
    await setup(model('MANUFACTURE'), { productManufactured: false });
    expect(q('lt-recalc')).toBeNull();
    await setup(model('MANUFACTURE'));
    expect(q('lt-recalc')).not.toBeNull();
  });

  it('switching the variant loads its lead times and drops the previous BOM result', async () => {
    await setup(model('MANUFACTURE'));
    cmp.recalculate();
    cmp.selectVariant('v-0');
    await refresh();
    expect(service.getVariantLeadTimes).toHaveBeenCalledWith('v-0');
    expect(cmp.bomResult).toBeNull();
  });

  it('says so when the lead times cannot be loaded', async () => {
    await setup(model('STOCK'));
    service.getVariantLeadTimes.and.returnValue(throwError(() => ({ status: 403 })));
    cmp.selectVariant('v-0');
    await refresh();
    expect(q('lt-load-failed')).not.toBeNull();
  });
});
