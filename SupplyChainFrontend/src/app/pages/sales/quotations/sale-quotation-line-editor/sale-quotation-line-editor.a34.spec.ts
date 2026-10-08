import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { signal } from '@angular/core';
import { MessageService } from 'primeng/api';

import { SaleQuotationLineEditorComponent } from './sale-quotation-line-editor.component';
import { SalesPreorderService, SaleQuotation, SaleQuotationLine } from '../../../../services/sales-preorder.service';
import { InventoryService } from '../../../../services/inventory.service';
import { FinanceSetupService } from '../../../../services/finance-setup.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { LeadTimeService, LeadTimeResultModel } from '../../../../services/lead-time.service';
import { TenantService } from '../../../service/tenant.service';
import { AuthService } from '../../../service/auth.service';
import { LeadTimePopoverComponent } from '../../lead-time-popover/lead-time-popover.component';
import { ok, quotation, qLine } from '../sale-quotation.fixtures.spec';

// A34 PC-07/08/09 in the quotation line editor — the manual date is promisedDeliveryDate (saved with the line). A saved
// line, unchanged, calculates through its own endpoint (POST …/sale-quotations/{uuid}/lines/{line}/lead-time), which
// stores the calculation; a new or changed line uses POST api/lead-time/calculate, and Apply writes the earliest date as
// the promised date, since nothing else would keep it (API-CONTRACT.md §5.2).

const LEAD: LeadTimeResultModel = {
  totalLeadTimeDays: 4, earliestDeliveryDate: '2026-10-08T00:00:00', routeCategory: 'STOCK', calculatedAt: '2026-10-04T08:00:00Z',
  components: [{ code: 'PICK_PACK', name: 'Pick & pack', days: 1, source: 'ORG_DEFAULT' }]
};

describe('SaleQuotationLineEditorComponent — A34 lead time', () => {
  let fixture: ComponentFixture<SaleQuotationLineEditorComponent>;
  let component: SaleQuotationLineEditorComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;
  let leadTimes: jasmine.SpyObj<LeadTimeService>;
  let permissions: string[];

  const auth = { hasPermission: (c: string) => permissions.includes(c) } as unknown as AuthService;

  async function setup(opts: { q?: SaleQuotation; line?: SaleQuotationLine | null } = {}) {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService',
      ['addQuotationLine', 'updateQuotationLine', 'getRejectionReasons', 'calculateQuotationLineLeadTime']);
    service.getRejectionReasons.and.returnValue(ok([]));
    service.addQuotationLine.and.returnValue(ok('l-new'));
    service.updateQuotationLine.and.returnValue(ok(null));
    service.calculateQuotationLineLeadTime.and.returnValue(ok({
      line: qLine({ calculatedLeadTimeDays: 4, calculatedDeliveryDate: '2026-10-08T00:00:00', leadTimeCalculatedAt: '2026-10-04T08:00:00Z' }),
      leadTime: LEAD
    }));
    leadTimes = jasmine.createSpyObj<LeadTimeService>('LeadTimeService', ['calculate']);
    leadTimes.calculate.and.returnValue(ok(LEAD));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts']);
    inventory.getProducts.and.returnValue(ok({ data: [], totalRecords: 0 }));
    const setupSvc = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes']);
    setupSvc.getTaxCodes.and.returnValue(ok([]));
    const pricing = jasmine.createSpyObj<PricingRuleService>('PricingRuleService', ['resolvePrice']);
    pricing.resolvePrice.and.returnValue(ok({ found: false }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleQuotationLineEditorComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesPreorderService, useValue: service },
        { provide: LeadTimeService, useValue: leadTimes },
        { provide: InventoryService, useValue: inventory },
        { provide: FinanceSetupService, useValue: setupSvc },
        { provide: PricingRuleService, useValue: pricing },
        { provide: TenantService, useValue: { tenant: signal({ baseCurrency: 'cur-usd' }), hasFeature: () => true } },
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

  function click(testId: string) {
    const target = el(testId) as HTMLElement;
    expect(target).withContext(testId).not.toBeNull();
    (target.tagName === 'P-BUTTON' ? target.querySelector('button')! : target).click();
    fixture.detectChanges();
  }

  beforeEach(() => { permissions = ['SALE_QUOTATION_EDIT', 'INVENTORY_VIEW']; });

  it('calculates an unchanged saved line through its own endpoint, and shows the stored date as calculated', async () => {
    await setup({ line: qLine() });
    expect(service.calculateQuotationLineLeadTime).not.toHaveBeenCalled();

    click('lt-calculate');

    expect(service.calculateQuotationLineLeadTime).toHaveBeenCalledOnceWith('sq-1', 'l-1');
    expect(leadTimes.calculate).not.toHaveBeenCalled();
    expect(el('lt-total')!.textContent).toContain('Total: 4 days');
    expect(el('lt-indicator')!.textContent).toContain('⏱ Calculated');
  });

  it('calculates a new line with the unsaved endpoint, and Apply makes the earliest date the promised date', async () => {
    await setup();
    component.onVariantSelected({ productUuid: 'p-x', productName: 'Plate', variantId: 1, variantUuid: 'v-new', variantSku: 'P-1',
                                  variantName: 'Default', purchasePrice: null, uomCode: 'KG' });
    component.form.get('quantity')!.setValue(20);
    fixture.detectChanges();

    click('lt-calculate');
    expect(leadTimes.calculate).toHaveBeenCalledOnceWith({ variantUuid: 'v-new', quantity: 20, routeUuid: null, requestedDate: null });

    click('lt-apply');
    expect(component.form.get('promisedDeliveryDate')!.value).toEqual(new Date(2026, 9, 8));
    expect(component.buildRequest().promisedDeliveryDate).toBe('2026-10-08');
  });

  it('uses the unsaved endpoint once a saved line\'s quantity has changed', async () => {
    await setup({ line: qLine() });
    component.form.get('quantity')!.setValue(750);
    fixture.detectChanges();
    click('lt-calculate');
    expect(service.calculateQuotationLineLeadTime).not.toHaveBeenCalled();
    expect(leadTimes.calculate.calls.mostRecent().args[0]).toEqual({ variantUuid: 'v-rod', quantity: 750, routeUuid: null, requestedDate: null });
  });

  it('writes an overridden date into the promised date, and × clears it', async () => {
    await setup({ line: qLine({ promisedDeliveryDate: '2026-10-12T00:00:00', calculatedDeliveryDate: '2026-10-08T00:00:00' }) });
    expect(el('lt-indicator')!.textContent).toContain('✎ Manual');

    click('lt-set-date');
    const popover = fixture.debugElement.query(By.directive(LeadTimePopoverComponent)).componentInstance as LeadTimePopoverComponent;
    popover.overrideValue = new Date(2026, 9, 14);
    fixture.detectChanges();
    click('lt-override-save');
    expect(component.buildRequest().promisedDeliveryDate).toBe('2026-10-14');

    click('lt-clear');
    expect(component.form.get('promisedDeliveryDate')!.value).toBeNull();
    expect(service.updateQuotationLine).not.toHaveBeenCalled();
  });

  it('has no ⏱ on a rejected line, a quotation that is no longer a draft, or without SALE_QUOTATION_EDIT', async () => {
    await setup({ line: qLine({ lineType: 'REJECTED' }) });
    expect(el('lt-calculate')).toBeNull();

    await setup({ q: quotation({ status: 'SENT', isEditable: false }), line: qLine() });
    expect(el('lt-calculate')).toBeNull();
    expect(el('lt-set-date')).toBeNull();

    permissions = ['INVENTORY_VIEW'];
    await setup({ line: qLine() });
    expect(el('lt-calculate')).toBeNull();
  });
});
