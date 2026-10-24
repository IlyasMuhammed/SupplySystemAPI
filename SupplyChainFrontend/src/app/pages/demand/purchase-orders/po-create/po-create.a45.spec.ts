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

// A45 FE-B — PO line auto-pricing: resolve-price on variant/qty/date/supplier change, source badge, override,
// C7 re-lookup rules, order-date banner, and "resolution never blocks saving".

function ok<T>(result: T) { return of({ success: true, message: '', result } as any); }

const FOUND = (price: number, basis: 'OrderDate' | 'DeliveryDate' = 'OrderDate'): ResolvePriceResult =>
  ({ found: true, unitPrice: price, supplierPricelistUuid: 'pl-1', dateBasisUsed: basis, currencyId: 'cur-usd' });
const NOT_FOUND: ResolvePriceResult = { found: false, warningMessage: 'No configured price found for Widget-X.' };

describe('PoCreateComponent — A45 auto-pricing', () => {
  let fixture: ComponentFixture<PoCreateComponent>;
  let component: PoCreateComponent;
  let demand: jasmine.SpyObj<DemandService>;
  let pricing: jasmine.SpyObj<PurchasePricingService>;
  let toastAdd: jasmine.Spy;
  /** The re-lookup offers currently open (the toast stack is redrawn from them, one toast per offer). */
  const offerMsgs = (): any[] => [...(component.pricing as any).pending.values()].map((p: any) => p.msg);

  const line = () => component.lines.at(0);
  const sel = (variantUuid = 'v-1') => ({ productUuid: 'p-1', variantUuid, productName: 'Widget', variantName: 'X', uomCode: 'EA', purchasePrice: 9 } as any);

  /** Pick a variant on line 0 and let the debounced lookup run. */
  function pick(result: ResolvePriceResult | null, variant = 'v-1') {
    pricing.resolvePrice.and.returnValue(of(result));
    component.onLineVariantSelected(0, sel(variant));
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
    toastAdd = spyOn((component as any).messageService, 'addAll').and.callThrough();
    component.form.patchValue({ supplierId: 's-1', supplierName: 'Acme' });
  });

  it('TS-01: variant pick resolves and auto-fills the price as Pricelist (request carries supplier/qty/dates/currency)', fakeAsync(() => {
    component.form.patchValue({ deliveryDate: new Date(2026, 10, 20) });
    pick(FOUND(12.5));
    expect(pricing.resolvePrice).toHaveBeenCalledTimes(1);
    const req = pricing.resolvePrice.calls.mostRecent().args[0];
    expect(req.variantUuid).toBe('v-1');
    expect(req.productUuid).toBe('p-1');
    expect(req.supplierUuid).toBe('s-1');
    expect(req.quantity).toBe(1);
    expect(req.orderDate).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(req.expectedDeliveryDate).toBe('2026-11-20');
    expect(req.currencyId).toBe('cur-usd');
    expect(line().get('unitPrice')!.value).toBe(12.5);
    expect(line().get('priceSource')!.value).toBe(PriceSource.Pricelist);
    expect(line().get('systemResolvedPrice')!.value).toBe(12.5);
    expect(line().get('supplierPricelistUuid')!.value).toBe('pl-1');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="po-line-price-source"]').textContent.trim()).toBe('Pricelist');
  }));

  it('debounces rapid changes into one request', fakeAsync(() => {
    pricing.resolvePrice.and.returnValue(of(FOUND(5)));
    component.onLineVariantSelected(0, sel());
    line().get('quantity')!.setValue(2);
    line().get('quantity')!.setValue(3);
    tick(400);
    expect(pricing.resolvePrice).toHaveBeenCalledTimes(1);
    expect(pricing.resolvePrice.calls.mostRecent().args[0].quantity).toBe(3);
  }));

  it('TS-12: not found shows the inline warning; [Enter Manually] dismisses it, sets Manual and focuses the price', fakeAsync(() => {
    pick(NOT_FOUND);
    expect(line().get('priceSource')!.value).toBe(PriceSource.Manual);
    fixture.detectChanges();
    const banner = fixture.nativeElement.querySelector('[data-testid="po-line-price-warning"]') as HTMLElement;
    expect(banner.textContent).toContain('No configured price found for Widget-X.');
    const el = document.getElementById('unitPrice-0') as HTMLElement;
    spyOn(el, 'focus');
    (fixture.nativeElement.querySelector('[data-testid="po-line-enter-manually"] button') as HTMLButtonElement).click();
    tick();
    expect(line().get('pricingWarning')!.value).toBeNull();
    expect(line().get('priceSource')!.value).toBe(PriceSource.Manual);
    expect(el.focus).toHaveBeenCalled();
  }));

  it('warning banner auto-dismisses when a valid price is typed', fakeAsync(() => {
    pick(NOT_FOUND);
    line().get('unitPrice')!.setValue(7);
    expect(line().get('pricingWarning')!.value).toBeNull();
    expect(line().get('priceSource')!.value).toBe(PriceSource.Manual);
  }));

  it('[Create RFQ] navigates to the RFQ create route with supplier/variant/qty', fakeAsync(() => {
    const nav = TestBed.inject(Router).navigate as jasmine.Spy;
    pick(NOT_FOUND);
    component.pricing.createRfq(line() as any);
    const [cmds, extras] = nav.calls.mostRecent().args as any[];
    expect(cmds).toEqual(['/portal/pages/demand/quotations/create']);
    expect(extras.queryParams).toEqual(jasmine.objectContaining({ supplierId: 's-1', variantUuid: 'v-1', productId: 'p-1', qty: 1 }));
  }));

  it('TS-10: editing an auto price -> Override, system price kept; reason + provenance are sent on save', fakeAsync(() => {
    pick(FOUND(12.5));
    line().get('unitPrice')!.setValue(11);
    expect(line().get('priceSource')!.value).toBe(PriceSource.Override);
    expect(line().get('systemResolvedPrice')!.value).toBe(12.5);
    line().get('priceOverrideReason')!.setValue('Spot discount');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="po-line-override-reason"]')).toBeTruthy();

    component.onSubmit();
    const l = (demand.createPo.calls.mostRecent().args[0] as CreatePoRequest).lines![0];
    expect(l.priceSource).toBe(2);
    expect(l.systemResolvedPrice).toBe(12.5);
    expect(l.supplierPricelistUuid).toBe('pl-1');
    expect(l.priceOverrideReason).toBe('Spot discount');
    expect(l.unitPrice).toBe(11);
  }));

  it('typing the system price back restores Pricelist', fakeAsync(() => {
    pick(FOUND(12.5));
    line().get('unitPrice')!.setValue(11);
    line().get('unitPrice')!.setValue(12.5);
    expect(line().get('priceSource')!.value).toBe(PriceSource.Pricelist);
  }));

  it('C7 qty change, Pricelist source: silent price update', fakeAsync(() => {
    pick(FOUND(12.5));
    pricing.resolvePrice.and.returnValue(of(FOUND(10)));
    line().get('quantity')!.setValue(100);
    tick(400);
    expect(line().get('unitPrice')!.value).toBe(10);
    expect(line().get('priceSource')!.value).toBe(PriceSource.Pricelist);
    expect(offerMsgs().length).toBe(0);
  }));

  it('C7 qty change, Override source: Keep/Update offer; Update takes the system price as Pricelist', fakeAsync(() => {
    pick(FOUND(12.5));
    line().get('unitPrice')!.setValue(11); // Override
    pricing.resolvePrice.and.returnValue(of(FOUND(10)));
    line().get('quantity')!.setValue(100);
    tick(400);
    const call = offerMsgs()[0];
    expect(call.detail).toContain('System price for qty 100 is');
    expect(call.detail).toContain('Keep your price of');
    expect(line().get('unitPrice')!.value).toBe(11);          // untouched until the buyer chooses
    component.pricing.acceptOffer(call);
    expect(line().get('unitPrice')!.value).toBe(10);
    expect(line().get('priceSource')!.value).toBe(PriceSource.Pricelist);
  }));

  it('C7 qty change, Manual source: Keep retains price and source; Apply switches to Pricelist', fakeAsync(() => {
    pick(NOT_FOUND);
    line().get('unitPrice')!.setValue(20); // Manual
    pricing.resolvePrice.and.returnValue(of(FOUND(15)));
    line().get('quantity')!.setValue(50);
    tick(400);
    const msg = offerMsgs()[0];
    expect(msg.detail).toContain('A configured price of');
    component.pricing.keepOffer(msg);
    expect(line().get('unitPrice')!.value).toBe(20);
    expect(line().get('priceSource')!.value).toBe(PriceSource.Manual);

    line().get('quantity')!.setValue(60);
    tick(400);
    const msg2 = offerMsgs().pop()!;
    component.pricing.acceptOffer(msg2);
    expect(line().get('unitPrice')!.value).toBe(15);
    expect(line().get('priceSource')!.value).toBe(PriceSource.Pricelist);
  }));

  it('C7: RFQ-sourced lines are never re-looked-up', fakeAsync(() => {
    pick(FOUND(12.5));
    line().get('priceSource')!.setValue(PriceSource.Rfq);
    pricing.resolvePrice.calls.reset();
    line().get('quantity')!.setValue(500);
    tick(400);
    expect(pricing.resolvePrice).not.toHaveBeenCalled();
  }));

  it('C7: line date change re-looks-up only when the matched record used the delivery date', fakeAsync(() => {
    pick(FOUND(12.5, 'OrderDate'));
    pricing.resolvePrice.calls.reset();
    line().get('requiredDate')!.setValue(new Date(2026, 11, 1));
    tick(400);
    expect(pricing.resolvePrice).not.toHaveBeenCalled();

    pick(FOUND(12.5, 'DeliveryDate'), 'v-2');
    pricing.resolvePrice.calls.reset();
    pricing.resolvePrice.and.returnValue(of(FOUND(9, 'DeliveryDate')));
    line().get('requiredDate')!.setValue(new Date(2026, 11, 5));
    tick(400);
    expect(pricing.resolvePrice).toHaveBeenCalledTimes(1);
    expect(pricing.resolvePrice.calls.mostRecent().args[0].expectedDeliveryDate).toBe('2026-12-05');
    expect(line().get('unitPrice')!.value).toBe(9);
  }));

  it('supplier change re-resolves Pricelist lines for the new supplier', fakeAsync(() => {
    pick(FOUND(12.5));
    pricing.resolvePrice.and.returnValue(of(FOUND(8)));
    component.form.patchValue({ supplierId: 's-2' });
    tick(400);
    expect(pricing.resolvePrice.calls.mostRecent().args[0].supplierUuid).toBe('s-2');
    expect(line().get('unitPrice')!.value).toBe(8);
  }));

  it('order-date change: banner counts OrderDate-basis lines; Re-lookup All re-resolves, Keep dismisses', fakeAsync(() => {
    pick(FOUND(12.5, 'OrderDate'));
    line().get('unitPrice')!.setValue(11); // Override is still re-looked-up on explicit Re-lookup All
    component.pricing.setOrderDate(new Date(2026, 10, 15));
    expect(component.pricing.orderDateBanner?.count).toBe(1);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="order-date-banner"]').textContent).toContain('1 line(s) may have different prices');

    pricing.resolvePrice.calls.reset();
    pricing.resolvePrice.and.returnValue(of(FOUND(7)));
    component.pricing.reLookupAll();
    tick(400);
    expect(pricing.resolvePrice.calls.mostRecent().args[0].orderDate).toBe('2026-11-15');
    expect(line().get('unitPrice')!.value).toBe(7);
    expect(component.pricing.orderDateBanner).toBeNull();

    component.pricing.setOrderDate(new Date(2026, 10, 16));
    component.pricing.keepCurrentPrices();
    expect(component.pricing.orderDateBanner).toBeNull();
    expect(line().get('unitPrice')!.value).toBe(7);
  }));

  it('a failed resolve never blocks saving: the line stays Manual and the PO is created', fakeAsync(() => {
    pick(null);
    expect(line().get('priceSource')!.value).toBe(PriceSource.Manual);
    expect(line().get('pricingWarning')!.value).toBeNull();
    line().patchValue({ itemDescription: 'Widget', unitPrice: 14 });
    component.onSubmit();
    const l = (demand.createPo.calls.mostRecent().args[0] as CreatePoRequest).lines![0];
    expect(l.priceSource).toBe(0);
    expect(l.unitPrice).toBe(14);
    tick(1500);
  }));

  it('manual lines without a variant are sent as Manual with no provenance', () => {
    line().patchValue({ itemDescription: 'Loose item', unitPrice: 3 });
    component.onSubmit();
    const l = (demand.createPo.calls.mostRecent().args[0] as CreatePoRequest).lines![0];
    expect(l.priceSource).toBe(0);
    expect(l.systemResolvedPrice).toBeUndefined();
    expect(l.supplierPricelistUuid).toBeUndefined();
    expect(pricing.resolvePrice).not.toHaveBeenCalled();
  });
});
