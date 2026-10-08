import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SaleInquiryLinesComponent } from './sale-inquiry-lines.component';
import { SalesPreorderService, SaleInquiry, SaleInquiryLine } from '../../../../services/sales-preorder.service';
import { InventoryService } from '../../../../services/inventory.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { LeadTimeResultModel } from '../../../../services/lead-time.service';
import { LeadTimePopoverComponent } from '../../lead-time-popover/lead-time-popover.component';

// A34 PC-07/08/09 on the inquiry's lines — ⏱ calls the line's own endpoint (POST …/sale-inquiries/{uuid}/lines/{line}/lead-time,
// SALE_INQUIRY_EDIT, API-CONTRACT.md §5.2); the manual date is the line's estimatedDeliveryDate, stored with the evaluation
// (PUT …/lines/{line}), which the server keeps only for statuses that carry a date.

const LEAD: LeadTimeResultModel = {
  totalLeadTimeDays: 6, earliestDeliveryDate: '2026-10-10T00:00:00', routeCategory: 'STOCK', calculatedAt: '2026-10-04T08:00:00Z',
  components: [{ code: 'SUPPLIER', name: 'Supplier lead time', days: 0, source: 'IN_STOCK' }]
};

function line(overrides: Partial<SaleInquiryLine> = {}): SaleInquiryLine {
  return {
    uuid: 'l-1', lineNumber: 1, productDescription: 'Steel Rod 10mm', variantUuid: 'v-1', requestedQuantity: 500,
    requestedUomCode: 'KG', requestedDeliveryDate: '2026-10-20T00:00:00', lineStatus: 'CAN_SUPPLY',
    estimatedDeliveryDate: '2026-10-15T00:00:00', requiresProcurement: false, ...overrides
  };
}

function inquiry(overrides: Partial<SaleInquiry> = {}): SaleInquiry {
  return {
    uuid: 'inq-1', traceId: 't', inquiryNumber: 'INQ-2026-00042', partnerId: 'p-1', status: 'UNDER_REVIEW',
    receivedDate: '2026-10-01', createdBy: 7, createdDate: '', allowedNextStatuses: [], isEditable: true, quotations: [],
    lines: [
      line(),
      line({ uuid: 'l-2', lineNumber: 2, productDescription: 'Rubber Gasket', variantUuid: 'v-2', lineStatus: 'PENDING', estimatedDeliveryDate: null }),
      line({ uuid: 'l-3', lineNumber: 3, productDescription: 'Custom part', variantUuid: null, lineStatus: 'PENDING', estimatedDeliveryDate: null })
    ],
    ...overrides
  };
}

describe('SaleInquiryLinesComponent — A34 lead time', () => {
  let fixture: ComponentFixture<SaleInquiryLinesComponent>;
  let component: SaleInquiryLinesComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;
  let changed: jasmine.Spy;
  let toasts: jasmine.Spy;

  async function setup(inq: SaleInquiry = inquiry(), canEdit = true) {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService',
      ['getRejectionReasons', 'addInquiryLine', 'updateInquiryLine', 'deleteInquiryLine', 'calculateInquiryLineLeadTime']);
    service.getRejectionReasons.and.returnValue(of({ success: true, message: '', result: [] } as any));
    service.updateInquiryLine.and.returnValue(of({ success: true, message: '' } as any));
    service.calculateInquiryLineLeadTime.and.returnValue(of({ success: true, message: '', result: {
      line: line({ calculatedLeadTimeDays: 6, calculatedDeliveryDate: '2026-10-10T00:00:00', leadTimeCalculatedAt: '2026-10-04T08:00:00Z' }),
      leadTime: LEAD } } as any));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getProductById']);
    inventory.getProducts.and.returnValue(of({ success: true, message: '', result: { data: [], totalRecords: 0 } } as any));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['resolveUrl']);

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleInquiryLinesComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesPreorderService, useValue: service },
        { provide: InventoryService, useValue: inventory },
        { provide: AttachmentService, useValue: attachments }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleInquiryLinesComponent);
    component = fixture.componentInstance;
    component.inquiry = inq;
    component.canEdit = canEdit;
    changed = jasmine.createSpy('changed');
    component.changed.subscribe(changed);
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  function cells(): HTMLElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('[data-testid="line-delivery"]'));
  }

  function clickIn(parent: HTMLElement, testId: string) {
    const el = parent.querySelector(`[data-testid="${testId}"]`) as HTMLElement;
    expect(el).withContext(testId).not.toBeNull();
    (el.tagName === 'P-BUTTON' ? el.querySelector('button')! : el).click();
    fixture.detectChanges();
  }

  it('shows each line\'s delivery date, the estimate reading as manual', async () => {
    await setup();
    expect(cells().length).toBe(3);
    expect(cells()[0].textContent).toContain('15 Oct 2026');
    expect(cells()[0].textContent).toContain('✎ Manual');
    expect(cells()[1].textContent).toContain('— Not calculated');
  });

  it('calculates a catalogue line on demand through the line\'s own endpoint, and shows the stored result', async () => {
    await setup();
    expect(service.calculateInquiryLineLeadTime).not.toHaveBeenCalled();
    expect(cells()[2].querySelector('[data-testid="lt-calculate"]')).withContext('free text has no variant').toBeNull();

    clickIn(cells()[1], 'lt-calculate');

    expect(service.calculateInquiryLineLeadTime).toHaveBeenCalledOnceWith('inq-1', 'l-2');
    expect(cells()[1].querySelector('[data-testid="lt-total"]')!.textContent).toContain('Total: 6 days');
    expect(component.inquiry.lines[1].calculatedDeliveryDate).toBe('2026-10-10T00:00:00');
    expect(changed).not.toHaveBeenCalled();
  });

  it('stores an overridden date as the estimate through the evaluation, keeping the rest of the line', async () => {
    await setup();
    clickIn(cells()[0], 'lt-set-date');
    const popover = fixture.debugElement.queryAll(By.directive(LeadTimePopoverComponent))[0].componentInstance as LeadTimePopoverComponent;
    popover.overrideValue = new Date(2026, 9, 18);
    fixture.detectChanges();
    clickIn(cells()[0], 'lt-override-save');

    expect(service.updateInquiryLine).toHaveBeenCalledTimes(1);
    const [uuid, lineUuid, req] = service.updateInquiryLine.calls.mostRecent().args;
    expect([uuid, lineUuid]).toEqual(['inq-1', 'l-1']);
    expect(req.estimatedDeliveryDate).toBe('2026-10-18');
    expect(req.lineStatus).toBe('CAN_SUPPLY');
    expect(req.requestedQuantity).toBe(500);
    expect(changed).toHaveBeenCalled();
  });

  it('clears the estimate with ×, so the calculated date stands', async () => {
    await setup(inquiry({ lines: [line({ calculatedDeliveryDate: '2026-10-10T00:00:00', calculatedLeadTimeDays: 6 })] }));
    clickIn(cells()[0], 'lt-clear');
    expect(service.updateInquiryLine.calls.mostRecent().args[2].estimatedDeliveryDate).toBeNull();
  });

  it('shows the server\'s refusal when the estimate cannot be stored', async () => {
    await setup();
    service.updateInquiryLine.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Say when it can be delivered.' } })));
    clickIn(cells()[0], 'lt-clear');
    expect(toasts.calls.mostRecent().args[0].detail).toBe('Say when it can be delivered.');
    expect(changed).not.toHaveBeenCalled();
  });

  it('offers no date on a pending line, which carries none, and says how to set one', async () => {
    await setup();
    expect(cells()[1].querySelector('[data-testid="lt-set-date"]')).toBeNull();
    clickIn(cells()[1], 'lt-calculate');
    expect(cells()[1].querySelector('[data-testid="lt-override-note"]')!.textContent).toContain('Evaluate the line');
  });

  it('lets the evaluation leave the estimate blank when the line has a calculated date (contract §5.2)', async () => {
    const calculated = line({ estimatedDeliveryDate: null, calculatedDeliveryDate: '2026-10-10T00:00:00', calculatedLeadTimeDays: 6 });
    await setup(inquiry({ lines: [calculated, line({ uuid: 'l-2', lineNumber: 2, estimatedDeliveryDate: null })] }));

    component.openEdit(component.inquiry.lines[0]);
    expect(component.problems.estimatedDeliveryDate).toBeUndefined();
    expect(component.estimatedDateRequired).toBeFalse();
    expect(component.calculatedDateHint).toContain('10 Oct 2026');

    component.openEdit(component.inquiry.lines[1]);
    expect(component.problems.estimatedDeliveryDate).toBeDefined();
    expect(component.calculatedDateHint).toBeNull();
  });

  it('calculates nothing and changes nothing for a reader, or on a quoted inquiry', async () => {
    await setup(inquiry(), false);
    expect(cells()[1].querySelector('[data-testid="lt-calculate"]')).toBeNull();
    expect(cells()[0].querySelector('[data-testid="lt-set-date"]')).toBeNull();

    await setup(inquiry({ status: 'QUOTED' }), true);
    expect(cells()[1].querySelector('[data-testid="lt-calculate"]')).toBeNull();
  });
});
