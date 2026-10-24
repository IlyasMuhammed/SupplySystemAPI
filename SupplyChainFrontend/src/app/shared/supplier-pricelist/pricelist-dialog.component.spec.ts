import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { PricelistDialogComponent } from './pricelist-dialog.component';
import { InventoryService } from '../../services/inventory.service';
import { MoneyService } from '../../services/money.service';
import { SupplierService } from '../../services/supplier.service';
import { PricelistItem, SupplierPricelistService } from '../../services/supplier-pricelist.service';

const USD = { currencyId: 'c-usd', code: 'USD', name: 'US Dollar', isActive: true, baseFor: ['PURCHASE'] };
const EUR = { currencyId: 'c-eur', code: 'EUR', name: 'Euro', isActive: true, baseFor: [] };

const existing = (over: Partial<PricelistItem> = {}): PricelistItem => ({
  uuid: 'u-1', productUuid: 'p-1', productName: 'Widget', supplierUuid: 's-1', supplierName: 'Acme', minQuantity: 5, unitPrice: 12.5,
  currencyId: 'c-eur', currencyCode: 'EUR', pricingDateBasis: 1, validFrom: '2026-03-01', validTo: '2026-08-31', priority: 2, isActive: true,
  rowVersion: 'RV1', ...over
});

describe('PricelistDialogComponent (A45 C4/C6)', () => {
  let fixture: ComponentFixture<PricelistDialogComponent>;
  let cmp: PricelistDialogComponent;
  let api: jasmine.SpyObj<SupplierPricelistService>;

  /** A46 C7 - supplier detail answers by id (defaultPurchaseCurrencyId). */
  let detailById: Record<string, any> = {};
  let detailCalls: string[] = [];

  async function setup(inputs: Partial<PricelistDialogComponent> = {}) {
    detailCalls = [];
    api = jasmine.createSpyObj<SupplierPricelistService>('SupplierPricelistService', ['create', 'update']);
    api.create.and.returnValue(of({ item: existing(), overlapWarning: 'Overlaps priority 0' }));
    api.update.and.returnValue(of({ item: existing() }));
    const inventory = { getProducts: () => of({ success: true, result: { data: [{ uuid: 'p-1', name: 'Widget', sku: 'W-1' }] } }) };
    const suppliers = {
      getSuppliers: () => of({ success: true, result: { data: [{ uuid: 's-1', supplierName: 'Acme' }, { uuid: 's-2', supplierName: 'Beta' }] } }),
      getSupplierById: (id: string) => { detailCalls.push(id); return of({ success: true, result: { uuid: id, ...(detailById[id] ?? {}) } }); }
    };
    const money = { load: () => of([USD, EUR]), currencies: () => [USD, EUR], find: () => null };

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PricelistDialogComponent],
      providers: [
        provideNoopAnimations(),
        { provide: SupplierPricelistService, useValue: api },
        { provide: InventoryService, useValue: inventory },
        { provide: SupplierService, useValue: suppliers },
        { provide: MoneyService, useValue: money }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PricelistDialogComponent);
    cmp = fixture.componentInstance;
    for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
    fixture.componentRef.setInput('visible', true);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const fill = (over: object = {}) => Object.assign(cmp.f, {
    productUuid: 'p-1', supplierUuid: 's-1', unitPrice: 9.5, validFrom: new Date(2026, 0, 1), validTo: new Date(2026, 11, 31), ...over
  });

  it('starts a new price with min qty 1, priority 0, order-date basis, active, and the purchase base currency', async () => {
    await setup();
    expect(cmp.f.minQuantity).toBe(1);
    expect(cmp.f.priority).toBe(0);
    expect(cmp.f.pricingDateBasis).toBe(0);
    expect(cmp.f.isActive).toBeTrue();
    expect(cmp.f.currencyId).toBe('c-usd');
  });

  it('fixes the product (hides its picker) when opened from a product, and the vendor when opened from a supplier', async () => {
    await setup({ fixedProductUuid: 'p-1', fixedProductName: 'Widget' });
    const el: HTMLElement = document.body;
    expect(el.querySelector('[data-testid="pd-product"]')).toBeNull();
    expect(el.querySelector('[data-testid="pd-product-fixed"]')?.textContent).toContain('Widget');
    expect(el.querySelector('[data-testid="pd-supplier"]')).not.toBeNull();
    expect(cmp.f.productUuid).toBe('p-1');

    await setup({ fixedSupplierUuid: 's-1', fixedSupplierName: 'Acme' });
    expect(document.body.querySelector('[data-testid="pd-supplier"]')).toBeNull();
    expect(document.body.querySelector('[data-testid="pd-product"]')).not.toBeNull();
    expect(cmp.f.supplierUuid).toBe('s-1');
  });

  it('refuses a zero price and shows the error after a save attempt without calling the API', async () => {
    await setup();
    fill({ unitPrice: 0 });
    cmp.save();
    expect(api.create).not.toHaveBeenCalled();
    expect(cmp.show('unitPrice')).toBeTrue();
    expect(cmp.errors.unitPrice).toContain('greater than 0');
  });

  it('shows a price error on blur, before any save', async () => {
    await setup();
    fill({ unitPrice: -2 });
    expect(cmp.show('unitPrice')).toBeFalse();
    cmp.touch('unitPrice');
    expect(cmp.show('unitPrice')).toBeTrue();
  });

  it('refuses valid-to before valid-from', async () => {
    await setup();
    fill({ validFrom: new Date(2026, 5, 1), validTo: new Date(2026, 4, 1) });
    cmp.save();
    expect(api.create).not.toHaveBeenCalled();
    expect(cmp.errors.validTo).toContain('on or after');
  });

  it('creates with yyyy-MM-dd dates, trimmed text and emits saved with the overlap warning', async () => {
    await setup();
    fill({ vendorProductCode: '  V-9 ', vendorProductName: '', notes: '  ' });
    const saved: any[] = [];
    cmp.saved.subscribe(s => saved.push(s));
    cmp.save();
    const body = api.create.calls.mostRecent().args[0];
    expect(body.validFrom).toBe('2026-01-01');
    expect(body.validTo).toBe('2026-12-31');
    expect(body.vendorProductCode).toBe('V-9');
    expect(body.vendorProductName).toBeNull();
    expect(body.notes).toBeNull();
    expect(body.currencyId).toBe('c-usd');
    expect(body.rowVersion).toBeUndefined();
    expect(saved[0].overlapWarning).toBe('Overlaps priority 0');
    expect(cmp.visible).toBeFalse();
  });

  it('edits through PUT with the row version', async () => {
    await setup({ item: existing() });
    expect(cmp.f.minQuantity).toBe(5);
    expect(cmp.f.currencyId).toBe('c-eur');
    expect(cmp.f.validFrom!.getFullYear()).toBe(2026);
    cmp.f.unitPrice = 13;
    cmp.save();
    expect(api.update).toHaveBeenCalled();
    const [uuid, body] = api.update.calls.mostRecent().args;
    expect(uuid).toBe('u-1');
    expect(body.rowVersion).toBe('RV1');
    expect(body.unitPrice).toBe(13);
    expect(api.create).not.toHaveBeenCalled();
  });

  it('shows a stale-row message on 409, stays open and asks the host to reload', async () => {
    await setup({ item: existing() });
    api.update.and.returnValue(throwError(() => ({ status: 409, error: {} })));
    let conflicts = 0;
    cmp.conflict.subscribe(() => conflicts++);
    cmp.save();
    expect(cmp.error).toContain('changed by someone else');
    expect(conflicts).toBe(1);
    expect(cmp.visible).toBeTrue();
  });

  it('shows the server message for a 400', async () => {
    await setup();
    fill();
    api.create.and.returnValue(throwError(() => ({ status: 400, error: { message: 'An identical active price already exists.' } })));
    cmp.save();
    expect(cmp.error).toBe('An identical active price already exists.');
  });

  // ── A46 C6/C7 - currency defaults from the vendor's default purchase currency ──────────────────────
  describe('currency default from the vendor (A46 C7)', () => {
    beforeEach(() => { detailById = { 's-1': { defaultPurchaseCurrencyId: 'c-eur' }, 's-2': {} }; });

    it('vendor fixed (opened from a supplier) with an EUR default -> EUR pre-selected', async () => {
      await setup({ fixedSupplierUuid: 's-1', fixedSupplierName: 'Acme' });
      expect(detailCalls).toEqual(['s-1']);
      expect(cmp.f.currencyId).toBe('c-eur');
    });

    it('vendor fixed with no default -> the org purchase base', async () => {
      detailById['s-1'] = {};
      await setup({ fixedSupplierUuid: 's-1', fixedSupplierName: 'Acme' });
      expect(cmp.f.currencyId).toBe('c-usd');
    });

    it('older server: preferredCurrency is the default', async () => {
      detailById['s-1'] = { preferredCurrency: 'c-eur' };
      await setup({ fixedSupplierUuid: 's-1', fixedSupplierName: 'Acme' });
      expect(cmp.f.currencyId).toBe('c-eur');
    });

    it('a default that is not an offered currency falls back to the base', async () => {
      detailById['s-1'] = { defaultPurchaseCurrencyId: 'c-gone' };
      await setup({ fixedSupplierUuid: 's-1', fixedSupplierName: 'Acme' });
      expect(cmp.f.currencyId).toBe('c-usd');
    });

    it('vendor picked in the dialog: currency follows each vendor, back to the base for one without a default', async () => {
      await setup();
      expect(detailCalls).toEqual([]);
      cmp.f.supplierUuid = 's-1'; cmp.onSupplierPicked('s-1');
      expect(cmp.f.currencyId).toBe('c-eur');
      cmp.f.supplierUuid = 's-2'; cmp.onSupplierPicked('s-2');
      expect(cmp.f.currencyId).toBe('c-usd');
    });

    it('the currency stays editable: a hand-picked currency is not overwritten by a later vendor change', async () => {
      await setup();
      cmp.f.currencyId = 'c-eur'; cmp.currencyPickedByHand = true;
      cmp.f.supplierUuid = 's-2'; cmp.onSupplierPicked('s-2');
      expect(cmp.f.currencyId).toBe('c-eur');
    });

    it('editing an existing price never re-defaults its currency', async () => {
      await setup({ item: existing(), fixedSupplierUuid: 's-1', fixedSupplierName: 'Acme' });
      expect(detailCalls).toEqual([]);
      expect(cmp.f.currencyId).toBe('c-eur');
    });

    it('a failed vendor lookup keeps the current currency', async () => {
      await setup();
      (cmp as any).suppliers.getSupplierById = () => throwError(() => ({ status: 500 }));
      cmp.f.supplierUuid = 's-1'; cmp.onSupplierPicked('s-1');
      expect(cmp.f.currencyId).toBe('c-usd');
    });
  });

  it('offers a variant choice only when variants are given', async () => {
    await setup({ fixedProductUuid: 'p-1', variants: [{ uuid: 'v-1', variantName: 'Red', sku: 'W-R' }] });
    expect(document.body.querySelector('[data-testid="pd-variant"]')).not.toBeNull();
    expect(cmp.variantOptions[0].label).toBe('Red (W-R)');
    cmp.f.variantUuid = 'v-1';
    fill();
    cmp.save();
    expect(api.create.calls.mostRecent().args[0].variantUuid).toBe('v-1');
  });
});
