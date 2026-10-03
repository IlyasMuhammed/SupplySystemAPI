import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { WritableSignal, signal } from '@angular/core';
import { MessageService } from 'primeng/api';

import { SaleQuotationLineEditorComponent } from './sale-quotation-line-editor.component';
import { SalesPreorderService, SaleQuotation, SaleQuotationLine, RejectionReason } from '../../../../services/sales-preorder.service';
import { InventoryService } from '../../../../services/inventory.service';
import { FinanceSetupService, TaxCodeModel } from '../../../../services/finance-setup.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { TenantService } from '../../../service/tenant.service';
import { AuthService } from '../../../service/auth.service';
import { ok, fail, quotation, qLine } from '../sale-quotation.fixtures.spec';

function taxCode(uuid: string, code: string, rate: number, opts: Partial<TaxCodeModel> = {}): TaxCodeModel {
  return { uuid, code, name: code, description: null, ratePercent: rate, usage: 'BOTH', isDefault: false, isActive: true, ...opts };
}
const GST17 = taxCode('gst17', 'GST17', 17, { isDefault: true, name: 'GST standard' });
const RED7 = taxCode('red7', 'RED7', 7.5);

function reason(uuid: string, code: string, description: string): RejectionReason {
  return { uuid, code, description, isActive: true, isSystem: true, displayOrder: 10, createdDate: '2026-10-01T00:00:00Z' };
}
const REASONS = [reason('r-oos', 'OOS', 'Out of stock'), reason('r-dis', 'DIS', 'Discontinued')];

describe('SaleQuotationLineEditorComponent (A32-PC-12)', () => {
  let fixture: ComponentFixture<SaleQuotationLineEditorComponent>;
  let component: SaleQuotationLineEditorComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;
  let setupSvc: jasmine.SpyObj<FinanceSetupService>;
  let pricing: jasmine.SpyObj<PricingRuleService>;
  let tenant: WritableSignal<any>;
  let permissions: string[];

  const auth = { hasPermission: (c: string) => permissions.includes(c) } as unknown as AuthService;

  async function setup(opts: { q?: SaleQuotation; line?: SaleQuotationLine | null; codes?: TaxCodeModel[] } = {}) {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['addQuotationLine', 'updateQuotationLine', 'getRejectionReasons']);
    service.getRejectionReasons.and.returnValue(ok(REASONS));
    service.addQuotationLine.and.returnValue(ok('l-new'));
    service.updateQuotationLine.and.returnValue(ok(null));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts']);
    inventory.getProducts.and.returnValue(ok({ data: [], totalRecords: 0 }));
    setupSvc = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes']);
    setupSvc.getTaxCodes.and.returnValue(ok(opts.codes ?? [GST17, RED7]));
    pricing = jasmine.createSpyObj<PricingRuleService>('PricingRuleService', ['resolvePrice']);
    pricing.resolvePrice.and.returnValue(ok({ found: false }));
    tenant = signal({ baseCurrency: 'cur-usd' });

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleQuotationLineEditorComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesPreorderService, useValue: service },
        { provide: InventoryService, useValue: inventory },
        { provide: FinanceSetupService, useValue: setupSvc },
        { provide: PricingRuleService, useValue: pricing },
        { provide: TenantService, useValue: { tenant, hasFeature: () => true } },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleQuotationLineEditorComponent);
    component = fixture.componentInstance;
    component.quotation = opts.q ?? quotation();
    component.line = opts.line ?? null;
    fixture.detectChanges();
  }

  const el = (testId: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);

  function pick(variantUuid = 'v-new', uomCode: string | null = 'KG') {
    component.onVariantSelected({
      productUuid: 'p-x', productName: 'Mild Steel Plate', variantId: 1, variantUuid, variantSku: 'MSP-1',
      variantName: 'Default', purchasePrice: null, uomCode
    });
  }

  beforeEach(() => { permissions = ['SALE_QUOTATION_EDIT', 'INVENTORY_VIEW']; });

  // ── Tax codes — as on the sale order form ──────────────────────────────────

  describe('tax code, as on the sale order form', () => {
    it('starts a new line on the default sales code, written CODE · rate%', async () => {
      await setup();
      expect(setupSvc.getTaxCodes).toHaveBeenCalledWith('SALES');
      expect(component.form.value.taxCodeUuid).toBe('gst17');
      expect(component.form.value.taxPercent).toBe(17);
      expect(component.taxCodeOptions.map(o => o.label)).toEqual(['GST17 · 17% — GST standard', 'RED7 · 7.5%']);
    });

    it('takes the picked code\'s rate and will not let it be typed over; no code, a typed rate', async () => {
      await setup();
      component.form.patchValue({ taxCodeUuid: 'red7' });
      component.onTaxCodeChange();
      expect(component.form.value.taxPercent).toBe(7.5);
      expect(component.hasTaxCode).toBeTrue();

      component.form.patchValue({ taxCodeUuid: null });
      component.onTaxCodeChange();
      expect(component.hasTaxCode).toBeFalse();
    });

    it('takes a percentage when the organization has no codes', async () => {
      await setup({ codes: [] });
      fixture.detectChanges();
      expect(component.showTaxCodes).toBeFalse();
      expect(el('tax-code')).toBeNull();
      expect(el('tax')).not.toBeNull();
    });

    it('keeps an edited line\'s retired code visible as "(no longer active)" and will not save on it', async () => {
      const line = qLine({ taxCodeUuid: 'gst16', taxCode: 'GST16', taxPercent: 16 });
      await setup({ line });
      expect(component.taxCodeOptions.map(o => o.label)).toContain('GST16 · 16% (no longer active)');
      expect(component.form.value.taxCodeUuid).withContext('an existing line keeps its code').toBe('gst16');
      expect(component.hasRetiredTaxCode).toBeTrue();

      component.save();
      expect(service.updateQuotationLine).not.toHaveBeenCalled();
      expect(component.error).toContain('GST16');
    });
  });

  // ── Totals ─────────────────────────────────────────────────────────────────

  describe('totals, computed as the server does', () => {
    it('totals the line in exact decimals, rounded half away from zero', async () => {
      await setup();
      pick();
      component.form.patchValue({ quantity: 3, unitPrice: 2.5, taxCodeUuid: 'gst17', taxPercent: 17, discountPercent: 0 });
      expect(component.lineTotal).toBe(8.78);      // 8.775 — a double would give 8.77

      component.form.patchValue({ discountPercent: 10 });
      expect(component.lineTotal).toBe(7.9);       // 7.8975
    });

    it('projects the quotation\'s grand total with this line in it, the edited line replacing its old self', async () => {
      const q = quotation({ lines: [
        qLine({ uuid: 'a', quantity: 2, unitPrice: 10, taxPercent: 0 }),
        qLine({ uuid: 'b', lineNumber: 2, lineType: 'REJECTED', quantity: 0, unitPrice: 0 })
      ] });
      await setup({ q });
      pick();
      component.form.patchValue({ quantity: 3, unitPrice: 2.5, taxPercent: 17, taxCodeUuid: 'gst17' });
      expect(component.projectedGrandTotal).toBe(28.78);

      await setup({ q, line: q.lines[0] });
      component.form.patchValue({ quantity: 1 });
      expect(component.projectedGrandTotal).withContext('line a at its new quantity, once').toBe(10);
    });

    it('has no total until there is a price', async () => {
      await setup();
      pick();
      component.form.patchValue({ quantity: 3, unitPrice: null });
      expect(component.lineTotal).toBeNull();
    });
  });

  // ── Line types ─────────────────────────────────────────────────────────────

  describe('NORMAL', () => {
    it('needs an item and a quantity above zero', async () => {
      await setup();
      component.form.patchValue({ quantity: 5 });
      component.save();
      expect(service.addQuotationLine).not.toHaveBeenCalled();

      pick();
      component.form.patchValue({ quantity: 0 });
      component.save();
      expect(service.addQuotationLine).not.toHaveBeenCalled();
    });

    it('adds the line with what was entered, the date as a calendar day, and says it is saved', async () => {
      await setup();
      const saved = jasmine.createSpy('saved');
      component.saved.subscribe(saved);
      pick('v-new', 'KG');
      component.form.patchValue({ quantity: 12.5, unitPrice: 4.25, discountPercent: 5, promisedDeliveryDate: new Date(2026, 9, 20) });
      component.save();

      expect(service.addQuotationLine).toHaveBeenCalledOnceWith('sq-1', jasmine.objectContaining({
        lineType: 'NORMAL', variantUuid: 'v-new', productDescription: 'Mild Steel Plate', quantity: 12.5, uomCode: 'KG',
        unitPrice: 4.25, discountPercent: 5, taxPercent: 17, taxCodeUuid: 'gst17', promisedDeliveryDate: '2026-10-20',
        rejectionReasonUuid: null, alternativeForLineUuid: null
      }));
      expect(saved).toHaveBeenCalledWith('l-new');
    });

    it('leaves a blank price for the pricing rules to fill on save', async () => {
      await setup();
      pick();
      component.form.patchValue({ quantity: 2, unitPrice: null });
      component.save();
      expect(service.addQuotationLine.calls.mostRecent().args[1].unitPrice).toBeNull();
    });

    it('suggests the pricing rules\' price for this customer at the validity start, when it is in the quotation\'s currency', async () => {
      await setup();
      pricing.resolvePrice.and.returnValue(ok({ found: true, unitPrice: 48, currencyId: 'cur-usd' }));
      pick();
      component.form.patchValue({ quantity: 200 });
      component.refreshPrice();
      expect(pricing.resolvePrice).toHaveBeenCalledWith('v-new', 'p-1', 200, '2026-10-03');
      expect(component.form.value.unitPrice).toBe(48);

      pricing.resolvePrice.and.returnValue(ok({ found: true, unitPrice: 13000, currencyId: 'cur-pkr' }));
      component.form.patchValue({ unitPrice: null });
      component.refreshPrice();
      expect(component.form.value.unitPrice).withContext('another currency: left to the server').toBeNull();
    });

    it('never overwrites a price the user typed', async () => {
      await setup();
      pricing.resolvePrice.and.returnValue(ok({ found: true, unitPrice: 48, currencyId: 'cur-usd' }));
      pick();
      component.form.patchValue({ quantity: 200, unitPrice: 45 });
      component.form.get('unitPrice')!.markAsDirty();
      component.refreshPrice();
      expect(component.form.value.unitPrice).toBe(45);
    });

    it('updates an existing line, showing its saved date as the same calendar day', async () => {
      const line = qLine({ promisedDeliveryDate: '2026-10-20T00:00:00' });
      await setup({ line });
      expect((component.form.value.promisedDeliveryDate as Date).getDate()).toBe(20);
      component.form.patchValue({ quantity: 450 });
      component.save();
      expect(service.updateQuotationLine).toHaveBeenCalledOnceWith('sq-1', 'l-1', jasmine.objectContaining({
        lineType: 'NORMAL', variantUuid: 'v-rod', quantity: 450, unitPrice: 2.5, promisedDeliveryDate: '2026-10-20'
      }));
    });

    it('shows the server\'s refusal and does not report the line saved', async () => {
      await setup();
      const saved = jasmine.createSpy('saved');
      component.saved.subscribe(saved);
      service.addQuotationLine.and.returnValue(fail(400, 'Quantity is below the minimum for this item.'));
      pick();
      component.form.patchValue({ quantity: 1 });
      component.save();
      expect(component.error).toBe('Quantity is below the minimum for this item.');
      expect(saved).not.toHaveBeenCalled();
    });
  });

  describe('REJECTED', () => {
    it('asks for a reason from the rejection-reasons list, and notes, instead of a price', async () => {
      await setup();
      component.setType('REJECTED');
      fixture.detectChanges();
      expect(service.getRejectionReasons).toHaveBeenCalled();
      expect(component.reasonOptions.map(o => o.label)).toEqual(['OOS — Out of stock', 'DIS — Discontinued']);
      expect(el('rejection-reason')).not.toBeNull();
      expect(el('rejection-notes')).not.toBeNull();
      expect(el('unit-price')).toBeNull();
      expect(el('alternative-for')).toBeNull();
    });

    it('needs a reason, and sends no price or tax', async () => {
      await setup();
      component.setType('REJECTED');
      component.form.patchValue({ productDescription: 'Titanium Sheet', quantity: 100 });
      component.save();
      expect(service.addQuotationLine).not.toHaveBeenCalled();

      component.form.patchValue({ rejectionReasonUuid: 'r-dis', rejectionNotes: 'Grade discontinued' });
      component.save();
      expect(service.addQuotationLine).toHaveBeenCalledOnceWith('sq-1', jasmine.objectContaining({
        lineType: 'REJECTED', productDescription: 'Titanium Sheet', rejectionReasonUuid: 'r-dis', rejectionNotes: 'Grade discontinued',
        unitPrice: null, discountPercent: 0, taxPercent: 0, taxCodeUuid: null, alternativeForLineUuid: null
      }));
    });

    it('can reject a line that names no catalog item, by its description', async () => {
      await setup();
      component.setType('REJECTED');
      component.form.patchValue({ rejectionReasonUuid: 'r-oos' });
      component.save();
      expect(service.addQuotationLine).withContext('needs some description').not.toHaveBeenCalled();

      component.form.patchValue({ productDescription: 'Custom bracket' });
      component.save();
      expect(service.addQuotationLine.calls.mostRecent().args[1].variantUuid).toBeNull();
    });
  });

  describe('ALTERNATIVE', () => {
    it('can only point at a REJECTED line of this quotation', async () => {
      await setup();
      component.setType('ALTERNATIVE');
      fixture.detectChanges();
      expect(component.alternativeForOptions).toEqual([{ label: 'Line 3 — Titanium Sheet', value: 'l-3' }]);
      expect(el('alternative-for')).not.toBeNull();
      expect(el('rejection-reason')).toBeNull();
    });

    it('needs the line it replaces, and sends it', async () => {
      await setup();
      component.setType('ALTERNATIVE');
      pick('v-316');
      component.form.patchValue({ quantity: 200, unitPrice: 8.5, alternativeNotes: 'Higher grade' });
      component.save();
      expect(service.addQuotationLine).not.toHaveBeenCalled();

      component.form.patchValue({ alternativeForLineUuid: 'l-3' });
      component.save();
      expect(service.addQuotationLine).toHaveBeenCalledOnceWith('sq-1', jasmine.objectContaining({
        lineType: 'ALTERNATIVE', variantUuid: 'v-316', alternativeForLineUuid: 'l-3', alternativeNotes: 'Higher grade',
        rejectionReasonUuid: null
      }));
    });

    it('is not offered while the quotation has no rejected line', async () => {
      await setup({ q: quotation({ lines: [qLine()] }) });
      expect(component.typeOptions.find(o => o.value === 'ALTERNATIVE')!.disabled).toBeTrue();
    });

    it('will not turn a rejected line that has alternatives into another type', async () => {
      const q = quotation();
      await setup({ q, line: q.lines.find(l => l.uuid === 'l-3')! });
      expect(component.typeOptions.filter(o => o.disabled).map(o => o.value)).toEqual(['NORMAL', 'ALTERNATIVE']);
    });
  });

  describe('DRAFT only', () => {
    it('saves nothing on a quotation that is no longer a draft', async () => {
      await setup({ q: quotation({ status: 'SENT', isEditable: false }) });
      pick();
      component.form.patchValue({ quantity: 2, unitPrice: 3 });
      component.save();
      expect(service.addQuotationLine).not.toHaveBeenCalled();
      expect(component.readOnly).toBeTrue();
    });
  });
});
