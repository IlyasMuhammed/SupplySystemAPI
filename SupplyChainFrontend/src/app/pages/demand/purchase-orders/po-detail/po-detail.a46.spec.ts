import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { PoDetailComponent } from './po-detail.component';
import { DemandService, PoDetailModel, PoLineModel } from '../../../../services/demand.service';
import { WorkflowService } from '../../../../services/workflow.service';
import { SupplierService } from '../../../../services/supplier.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AttachmentPolicyService } from '../../../../services/attachment-policy.service';
import { TimelineService } from '../../../../services/timeline.service';
import { TenantService } from '../../../service/tenant.service';
import { provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';
import { MoneyService } from '../../../../services/money.service';
import { PricingSummary } from '../../../../services/purchase-pricing.service';

// A46 FE (C1, C3, C5, C10) - PO detail: order date, 2dp price with 4dp tooltip, converted-price icon, no-rate warning,
// and the PR -> PO pricing summary banner with highlighted unpriced lines.

function line(over: Partial<PoLineModel> = {}): PoLineModel {
  return {
    uuid: 'l1', lineNo: 1, itemDescription: 'Steel coil', quantity: 10, unitPrice: 12.3456, lineTotal: 123.46, qtyReceived: 0,
    qtyInvoiced: 0, qtyPending: 10, qtyPendingInvoice: 10, requiresInspection: false, priceSource: 1, ...over
  };
}
function po(over: Partial<PoDetailModel> = {}): PoDetailModel {
  return {
    uuid: 'po-1', poNumber: 'PO-2026-0100', supplierId: 's-1', supplierName: 'Gulf Metals', status: 'DRAFT', totalAmount: 123.46,
    createdBy: 1, createdDate: '2026-10-07T00:00:00', orderDate: '2026-09-30', lines: [line()], linkedPrUuids: [],
    currencyId: 'cur-usd', currencyCode: 'USD', ...over
  };
}

describe('PoDetailComponent - A46', () => {
  let fixture: ComponentFixture<PoDetailComponent>;
  let component: PoDetailComponent;

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const qa = (id: string): HTMLElement[] => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`));
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();
  function tab(sectionId: string): void { (q('anchor-' + sectionId) as HTMLButtonElement).click(); fixture.detectChanges(); }

  async function setup(model: PoDetailModel, summary?: PricingSummary) {
    const demand = jasmine.createSpyObj<DemandService>('DemandService', ['getPoById']);
    demand.getPoById.and.returnValue(of({ success: true, message: '', result: model } as any));
    const timeline = jasmine.createSpyObj<TimelineService>('TimelineService', ['getByTraceId', 'getByDocument']);
    timeline.getByDocument.and.returnValue(of({ success: true, result: null } as any));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl']);
    attachments.getAttachments.and.returnValue(of({ success: true, result: [] } as any));
    attachments.resolveUrl.and.callFake((u: string) => u);
    const policy = jasmine.createSpyObj<AttachmentPolicyService>('AttachmentPolicyService', ['ruleFor']);
    policy.ruleFor.and.returnValue(of(null));
    if (summary) history.replaceState({ pricingSummary: summary }, '');

    await TestBed.configureTestingModule({
      imports: [PoDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        provideTestOrgCurrencies(),
        { provide: DemandService, useValue: demand },
        { provide: WorkflowService, useValue: jasmine.createSpyObj('WorkflowService', ['getHistory', 'getApprovalDetail']) },
        { provide: SupplierService, useValue: jasmine.createSpyObj('SupplierService', ['getEligibleContacts']) },
        { provide: AttachmentService, useValue: attachments },
        { provide: AttachmentPolicyService, useValue: policy },
        { provide: TimelineService, useValue: timeline },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } },
        { provide: ActivatedRoute, useValue: { params: of({ uuid: 'po-1' }), snapshot: { paramMap: new Map([['uuid', 'po-1']]), queryParams: {} } } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PoDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => history.replaceState(null, ''));

  it('shows the order date (the stored calendar day, no timezone shift)', async () => {
    await setup(po());
    tab('sec-supplier');
    expect(text(q('po-order-date'))).toBe('30 Sep 2026');
  });

  it('A46-0267: $12.3456 reads 12.35 in the grid and 12.3456 in the tooltip', async () => {
    await setup(po());
    const cell = q('po-line-unit-price')!;
    expect(text(cell)).toBe('USD 12.35');
    // The tooltip is bound to the same price written at 4 decimals.
    const money = TestBed.inject(MoneyService);
    expect(money.format(12.3456, 'USD', component.unitCode4)).toBe('USD 12.3456');
    expect(money.format(12.3456, 'USD', component.unitCode2)).toBe('USD 12.35');
  });

  it('A46-0284/0285: a converted line shows the info icon with the original price/rate/date', async () => {
    await setup(po({ lines: [line({ originalCurrencyCode: 'EUR', originalCurrencyPrice: 11.4321, exchangeRateUsed: 1.08, exchangeRateDate: '2026-09-30' })] }));
    expect(q('po-line-converted-info')).toBeTruthy();
    expect(component.conversionTooltip(component.convertedInfo(component.po!.lines[0]))).toBe('Original: EUR 11.4321 @ 1.08 (2026-09-30)');
  });

  it('A46-0289: a same-currency line has no icon and no warning', async () => {
    await setup(po());
    expect(q('po-line-converted-info')).toBeNull();
    expect(q('po-line-currency-mismatch')).toBeNull();
  });

  it('A46-0288: a line left without a converted price shows the no-rate warning', async () => {
    await setup(po({ lines: [line({ priceSource: 0, unitPrice: 0, lineTotal: 0, currencyMismatchWarning: true, pricingWarning: 'No exchange rate available for EUR â†’ USD on 2026-09-30. Enter price manually.' })] }));
    expect(text(q('po-line-currency-mismatch'))).toContain('No exchange rate available for EUR â†’ USD on 2026-09-30. Enter price manually.');
  });

  it('a Blanket-order line reads "Blanket"', async () => {
    await setup(po({ lines: [line({ priceSource: 4 })] }));
    expect(text(q('po-line-price-source'))).toBe('Blanket');
  });

  describe('PR -> PO pricing summary (C5 / BR-30)', () => {
    const summary: PricingSummary = { totalLines: 8, pricedCount: 5, unpricedCount: 3 };
    const mixed = () => po({ lines: [line({ uuid: 'a' }), line({ uuid: 'b', lineNo: 2, priceSource: 0, unitPrice: 0 }), line({ uuid: 'c', lineNo: 3, priceSource: 0 })] });

    it('shows the counts, then View Unpriced Lines highlights the lines that need a price', async () => {
      await setup(mixed(), summary);
      expect(text(q('pricing-summary-total'))).toBe('8 lines created from Purchase Request.');
      expect(text(q('pricing-summary-priced'))).toBe('5 lines priced automatically from Supplier Pricelist.');
      expect(text(q('pricing-summary-unpriced'))).toBe('3 lines require manual pricing (highlighted in yellow).');
      expect(fixture.nativeElement.querySelectorAll('tr.unpriced-line').length).toBe(0);

      (q('view-unpriced-lines')!.querySelector('button') ?? q('view-unpriced-lines')!).click();
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelectorAll('tr.unpriced-line').length).toBe(2);
    });

    it('no summary (a PO opened normally) shows no banner', async () => {
      await setup(mixed());
      expect(q('pricing-summary')).toBeNull();
    });

    it('all priced: no manual-pricing line and no button', async () => {
      await setup(po(), { totalLines: 2, pricedCount: 2, unpricedCount: 0 });
      expect(q('pricing-summary-unpriced')).toBeNull();
      expect(q('view-unpriced-lines')).toBeNull();
    });
  });
});
