import { of } from 'rxjs';
import { PurchaseRequiredComponent } from './purchase-required.component';
import { PurchaseRequiredLine } from '../../../services/purchase-required.service';

// The "Create purchase order" dialog's Required-by date: today or later only. Built directly — the rules under test
// are plain component state, no template or HTTP needed.
describe('PurchaseRequiredComponent — Required by date', () => {
  let component: PurchaseRequiredComponent;
  const startOfToday = () => { const d = new Date(); d.setHours(0, 0, 0, 0); return d; };
  const line = (earliest: Date): PurchaseRequiredLine => ({
    variantUuid: 'v-1', productName: 'Steel rod', variantName: 'Default', sku: 'SR-1', uom: 'EA',
    totalShortageQty: 5, affectedPoCount: 1, earliestRequiredDate: earliest.toISOString(),
    defaultSupplierId: 's-1', defaultSupplierName: 'AA Traders', currentStockOnHand: 0
  } as unknown as PurchaseRequiredLine);

  beforeEach(() => {
    const service = { getList: () => of({ result: [] }), createPurchaseOrder: () => of({}) };
    const auth = { hasPermission: () => true };
    const messages = { add: () => {} };
    component = new PurchaseRequiredComponent(service as any, {} as any, auth as any, messages as any);
  });

  it('offers today as the earliest date in the picker', () => {
    component.openCreateDialog(line(new Date()));
    expect(component.today.getTime()).toBe(startOfToday().getTime());
  });

  it('starts at today when the earliest production need is already past', () => {
    const lastWeek = new Date(Date.now() - 7 * 86400000);
    component.openCreateDialog(line(lastWeek));
    expect(component.createRequiredBy!.getTime()).toBe(startOfToday().getTime());
    expect(component.requiredByInPast).toBeFalse();
  });

  it('keeps a future earliest date', () => {
    const nextWeek = new Date(Date.now() + 7 * 86400000);
    component.openCreateDialog(line(nextWeek));
    expect(component.createRequiredBy!.getTime()).toBe(nextWeek.getTime());
  });

  it('refuses to create with a past date typed in', () => {
    component.openCreateDialog(line(new Date(Date.now() + 86400000)));
    component.createUnitPrice = 10;
    expect(component.canSubmitCreate).toBeTrue();

    component.createRequiredBy = new Date(Date.now() - 86400000);
    expect(component.requiredByInPast).toBeTrue();
    expect(component.canSubmitCreate).toBeFalse();
  });
});
