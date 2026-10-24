import { signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { PoCreateComponent } from './po-create.component';
import { DemandService, CreatePoRequest } from '../../../../services/demand.service';
import { SupplierService } from '../../../../services/supplier.service';
import { InventoryService } from '../../../../services/inventory.service';
import { RateCardService } from '../../../../services/rate-card.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AttachmentPolicyService } from '../../../../services/attachment-policy.service';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { PurchasePricingService, ResolvePriceResult, PriceSource } from '../../../../services/purchase-pricing.service';
import { TEST_PKR, TEST_USD, provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';

// A46 FE (C1, C3, C10) - PO create: order date, 4dp prices, currency-converted / no-rate resolve results.

function ok<T>(result: T) { return of({ success: true, message: '', result } as any); }

const FOUND = (price: number, extra: Partial<ResolvePriceResult> = {}): ResolvePriceResult =>
  ({ found: true, unitPrice: price, supplierPricelistUuid: 'pl-1', dateBasisUsed: 'OrderDate', currencyId: 'cur-usd', ...extra });

describe('PoCreateComponent - A46 order date, precision, currency conversion', () => {
  let fixture: ComponentFixture<PoCreateComponent>;
  let component: PoCreateComponent;
  let demand: jasmine.SpyObj<DemandService>;
  let pricing: jasmine.SpyObj<PurchasePricingService>;

  const line = () => component.lines.at(0);
  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const todayYmd = () => {
    const d = new Date(); const p = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
  };
  function pick(result: ResolvePriceResult | null, variant = 'v-1') {
    pricing.resolvePrice.and.returnValue(of(result));
    component.onLineVariantSelected(0, { productUuid: 'p-1', variantUuid: variant, productName: 'Widget', variantName: 'X', uomCode: 'EA', purchasePrice: 9 } as any);
    tick(400);
  }

  beforeEach(async () => {
    demand = jasmine.createSpyObj<DemandService>('DemandService',
      ['createPo', 'getPrs', 'getPrById', 'getQuotations', 'getQuotationById', 'getQuotationComparison']);
    demand.createPo.and.returnValue(ok('po-new'));
    demand.getPrs.and.returnValue(ok({ data: [], totalRecords: 0 }));
    demand.getQuotations.and.returnValue(ok({ data: [], totalRecords: 0 }));
    const suppliers = jasmine.createSpyObj<SupplierService>('SupplierService', ['getSuppliers', 'getSupplierById', 'getScoreSummary']);
    suppliers.getSuppliers.and.returnValue(ok({ data: [{ id: 1, uuid: 's-1', supplierName: 'Acme', isActive: true, supplierTypeIds: [], industryIds: [] }], totalRecords: 1 }));
    suppliers.getSupplierById.and.returnValue(ok({ uuid: 's-1', supplierName: 'Acme' }));
    suppliers.getScoreSummary.and.returnValue(of({ success: false, message: '', result: null } as any));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getWarehouses']);
    inventory.getProducts.and.returnValue(ok({ data: [] }));
    inventory.getWarehouses.and.returnValue(ok([]));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl']);
    attachments.getAttachments.and.returnValue(ok([]));
    const policy = jasmine.createSpyObj<AttachmentPolicyService>('AttachmentPolicyService', ['ruleFor']);
    policy.ruleFor.and.returnValue(of(null));
    pricing = jasmine.createSpyObj<PurchasePricingService>('PurchasePricingService', ['resolvePrice']);
    pricing.resolvePrice.and.returnValue(of(null));

    await TestBed.configureTestingModule({
      imports: [PoCreateComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        provideTestOrgCurrencies([TEST_PKR, TEST_USD]),
        { provide: DemandService, useValue: demand },
        { provide: SupplierService, useValue: suppliers },
        { provide: InventoryService, useValue: inventory },
        { provide: RateCardService, useValue: { getActiveRate: () => of({ success: false, message: '', result: null }) } },
        { provide: AttachmentService, useValue: attachments },
        { provide: AttachmentPolicyService, useValue: policy },
        { provide: PurchasePricingService, useValue: pricing },
        { provide: AuthService, useValue: { hasRole: () => false, hasPermission: () => true, getUserData: () => null } },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParams: {}, paramMap: new Map() }, queryParams: of({}) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PoCreateComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    component.form.patchValue({ supplierId: 's-1', supplierName: 'Acme' });
  });

  // ── C1 order date ──────────────────────────────────────────────────────────

  it('A46-0249: the order date picker renders, defaulting to today (local)', () => {
    expect(q('po-order-date')).toBeTruthy();
    const v = component.form.get('orderDate')!.value as Date;
    expect(v instanceof Date).toBeTrue();
    expect(component.form.get('orderDate')!.enabled).toBeTrue();
    const p = (n: number) => String(n).padStart(2, '0');
    expect(`${v.getFullYear()}-${p(v.getMonth() + 1)}-${p(v.getDate())}`).toBe(todayYmd());
  });

  it('sends orderDate as yyyy-MM-dd (local, no UTC day shift)', () => {
    component.form.get('orderDate')!.setValue(new Date(2026, 9, 1));   // 1 Oct 00:00 local
    component.form.patchValue({ title: 'T' });
    line().patchValue({ itemDescription: 'Widget', unitPrice: 5 });
    component.onSubmit();
    expect((demand.createPo.calls.mostRecent().args[0] as CreatePoRequest).orderDate).toBe('2026-10-01');
  });

  it('A46-0251: changing the order date shows the re-lookup banner for OrderDate-basis lines; resolve uses the PO order date, not today', fakeAsync(() => {
    pick(FOUND(12.5));
    component.form.get('orderDate')!.setValue(new Date(2026, 2, 5));
    expect(component.pricing.orderDateBanner?.count).toBe(1);
    fixture.detectChanges();
    expect(q('order-date-banner')!.textContent).toContain('1 line(s) may have different prices');

    pricing.resolvePrice.calls.reset();
    pricing.resolvePrice.and.returnValue(of(FOUND(7)));
    (q('order-date-relookup')!.querySelector('button') ?? q('order-date-relookup')!).click();
    tick(400);
    expect(pricing.resolvePrice.calls.mostRecent().args[0].orderDate).toBe('2026-03-05');
    expect(line().get('unitPrice')!.value).toBe(7);
    expect(component.pricing.orderDateBanner).toBeNull();
  }));

  it('Keep Current Prices dismisses the banner and keeps prices', fakeAsync(() => {
    pick(FOUND(12.5));
    component.form.get('orderDate')!.setValue(new Date(2026, 2, 5));
    fixture.detectChanges();
    (q('order-date-keep')!.querySelector('button') ?? q('order-date-keep')!).click();
    expect(component.pricing.orderDateBanner).toBeNull();
    expect(line().get('unitPrice')!.value).toBe(12.5);
  }));

  it('a new line priced after the order date was changed is resolved for that date', fakeAsync(() => {
    component.form.get('orderDate')!.setValue(new Date(2026, 5, 30));
    pick(FOUND(11));
    expect(pricing.resolvePrice.calls.mostRecent().args[0].orderDate).toBe('2026-06-30');
  }));

  // ── C3 precision ───────────────────────────────────────────────────────────

  it('A46-0268: a 4-decimal price is accepted and sent unrounded; the input allows 4 decimals', () => {
    line().patchValue({ itemDescription: 'Widget', unitPrice: 12.3456 });
    fixture.detectChanges();
    const input = fixture.nativeElement.querySelector('p-inputNumber[formControlName="unitPrice"], p-inputnumber[formcontrolname="unitPrice"]') as HTMLElement;
    expect(input).toBeTruthy();
    component.onSubmit();
    expect((demand.createPo.calls.mostRecent().args[0] as CreatePoRequest).lines![0].unitPrice).toBe(12.3456);
  });

  // ── C10 currency conversion ────────────────────────────────────────────────

  it('sends poCurrencyId (and the A45 currencyId) on resolve', fakeAsync(() => {
    pick(FOUND(12.5));
    const req = pricing.resolvePrice.calls.mostRecent().args[0];
    expect(req.poCurrencyId).toBe('cur-usd');
    expect(req.currencyId).toBe('cur-usd');
  }));

  it('A46-0287: a converted price shows the info icon with "Original: EUR 10 @ 1.08 (2026-10-01)"', fakeAsync(() => {
    pick(FOUND(10.8, { currencyConverted: true, originalPrice: 10, originalCurrencyCode: 'EUR', exchangeRateUsed: 1.08, exchangeRateDate: '2026-10-01' }));
    expect(line().get('unitPrice')!.value).toBe(10.8);
    expect(line().get('priceSource')!.value).toBe(PriceSource.Pricelist);
    fixture.detectChanges();
    const icon = q('po-line-converted-info')!;
    expect(icon).toBeTruthy();
    expect(component.conversionTooltip(line().get('conversionInfo')!.value)).toBe('Original: EUR 10 @ 1.08 (2026-10-01)');
  }));

  it('A46-0289: a same-currency result has no info icon and no warning', fakeAsync(() => {
    pick(FOUND(12.5));
    fixture.detectChanges();
    expect(q('po-line-converted-info')).toBeNull();
    expect(q('po-line-price-warning')).toBeNull();
    expect(line().get('conversionInfo')!.value).toBeNull();
  }));

  it('editing a converted price away from the system value drops the info icon', fakeAsync(() => {
    pick(FOUND(10.8, { currencyConverted: true, originalPrice: 10, originalCurrencyCode: 'EUR', exchangeRateUsed: 1.08, exchangeRateDate: '2026-10-01' }));
    line().get('unitPrice')!.setValue(11);
    fixture.detectChanges();
    expect(q('po-line-converted-info')).toBeNull();
  }));

  it('A46-0286/0288: no exchange rate -> warning banner, price left empty, source Manual', fakeAsync(() => {
    pick({ found: true, unitPrice: null, currencyMismatchWarning: true, originalCurrencyCode: 'EUR', currencyCode: 'EUR', effectiveDateUsed: '2026-10-01' });
    expect(line().get('priceSource')!.value).toBe(PriceSource.Manual);
    expect(line().get('systemResolvedPrice')!.value).toBeNull();
    fixture.detectChanges();
    const banner = q('po-line-price-warning')!;
    expect(banner).toBeTruthy();
    expect(banner.textContent).toContain(`No exchange rate available for EUR → USD on ${todayYmd()}. Enter price manually.`);
  }));

  it('no-rate after a Pricelist price empties the price and reverts to Manual', fakeAsync(() => {
    pick(FOUND(12.5));
    pricing.resolvePrice.and.returnValue(of({ found: true, unitPrice: null, currencyMismatchWarning: true, originalCurrencyCode: 'EUR' } as ResolvePriceResult));
    line().get('quantity')!.setValue(5);
    tick(400);
    expect(line().get('unitPrice')!.value).toBe(0);
    expect(line().get('priceSource')!.value).toBe(PriceSource.Manual);
    expect(line().get('pricingWarning')!.value).toContain('No exchange rate available for EUR');
  }));
});
