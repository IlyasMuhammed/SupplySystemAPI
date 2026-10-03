import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SaleInquiryLinesComponent } from './sale-inquiry-lines.component';
import {
  SalesPreorderService, SaleInquiry, SaleInquiryLine, RejectionReason, UpdateSaleInquiryLineRequest, SaleInquiryLineRequest
} from '../../../../services/sales-preorder.service';
import { InventoryService } from '../../../../services/inventory.service';
import { AttachmentService } from '../../../../services/attachment.service';

function line(overrides: Partial<SaleInquiryLine> = {}): SaleInquiryLine {
  return {
    uuid: 'l-1', lineNumber: 1, productDescription: 'Copper Wire 2mm', requestedQuantity: 1000, requestedUomCode: 'M',
    requestedDeliveryDate: '2026-10-20T00:00:00', lineStatus: 'PENDING', requiresProcurement: false, ...overrides
  };
}

function reason(code: string, isActive = true): RejectionReason {
  return { uuid: `r-${code}`, code, description: `${code} description`, isActive, isSystem: true, displayOrder: 10, createdDate: '' };
}

function inquiry(overrides: Partial<SaleInquiry> = {}): SaleInquiry {
  return {
    uuid: 'inq-1', traceId: 't', inquiryNumber: 'INQ-2026-00042', partnerId: 'p-1', status: 'UNDER_REVIEW',
    receivedDate: '2026-10-01', createdBy: 7, createdDate: '', allowedNextStatuses: [], isEditable: true, quotations: [],
    lines: [
      line({ uuid: 'l-1', lineNumber: 1, productDescription: 'Steel Rod 10mm', requestedQuantity: 500, requestedUomCode: 'KG',
             lineStatus: 'CAN_SUPPLY', estimatedDeliveryDate: '2026-10-15' }),
      line({ uuid: 'l-2', lineNumber: 2, lineStatus: 'PARTIAL', canSupplyQuantity: 600, estimatedDeliveryDate: '2026-10-25' }),
      line({ uuid: 'l-3', lineNumber: 3, productDescription: 'Titanium Sheet', lineStatus: 'CANNOT_SUPPLY', rejectionReasonUuid: 'r-DIS',
             rejectionReasonCode: 'DIS', rejectionReasonDescription: 'Product discontinued', alternativeVariantUuid: 'v-alt',
             alternativeVariantName: 'Stainless Steel Sheet 316L' }),
      line({ uuid: 'l-4', lineNumber: 4, productDescription: 'Rubber Gasket', lineStatus: 'PENDING' })
    ],
    ...overrides
  };
}

describe('SaleInquiryLinesComponent (A32-PB-10)', () => {
  let fixture: ComponentFixture<SaleInquiryLinesComponent>;
  let component: SaleInquiryLinesComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;
  let inventory: jasmine.SpyObj<InventoryService>;
  let changed: jasmine.Spy;
  let el: HTMLElement;

  async function setup(inq: SaleInquiry = inquiry(), canEdit = true, reasons: RejectionReason[] = [reason('DIS'), reason('OOS')]) {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService',
      ['getRejectionReasons', 'addInquiryLine', 'updateInquiryLine', 'deleteInquiryLine']);
    service.getRejectionReasons.and.returnValue(of({ success: true, message: '', result: reasons } as any));
    service.updateInquiryLine.and.returnValue(of({ success: true, message: '' } as any));
    service.addInquiryLine.and.returnValue(of({ success: true, message: '', result: 'l-new' } as any));
    service.deleteInquiryLine.and.returnValue(of({ success: true, message: '' } as any));
    inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getProductById']);
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
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const all = (id: string) => Array.from(el.querySelectorAll(`[data-testid="${id}"]`)) as HTMLElement[];

  // ── The table ──────────────────────────────────────────────────────────────

  it('lists #, product, qty, UOM, requested delivery and a coloured status dot per line', async () => {
    await setup();
    const rows = all('line-row');
    expect(rows.length).toBe(4);
    expect(rows[0].textContent).toContain('Steel Rod 10mm');
    expect(rows[0].textContent).toContain('500');
    expect(rows[0].textContent).toContain('KG');
    expect(rows[1].textContent).toContain('20 Oct 2026');
    expect(all('line-status').map(s => s.querySelector('.dot')!.className))
      .toEqual(jasmine.arrayContaining([jasmine.stringContaining('st-green')]));
    const dots = all('line-status').map(s => s.querySelector('.dot')!.classList);
    expect(dots[0]).toContain('st-green');
    expect(dots[1]).toContain('st-yellow');
    expect(dots[2]).toContain('st-red');
    expect(dots[3]).toContain('st-grey');
  });

  it('expands evaluated lines to show can-supply, reason and alternative', async () => {
    await setup();
    const sub = all('line-detail').map(d => d.textContent!).join('|');
    expect(sub).toContain('Can supply: 600 M by 25 Oct 2026');
    expect(sub).toContain('Reason: DIS — Product discontinued');
    expect(sub).toContain('Alternative: Stainless Steel Sheet 316L');
  });

  it('offers no edit, add or delete on a read-only inquiry', async () => {
    await setup(inquiry({ isEditable: false, status: 'QUOTED' }), false);
    expect(all('edit-line').length).toBe(0);
    expect(all('delete-line').length).toBe(0);
    expect(all('add-line').length).toBe(0);
    expect(service.getRejectionReasons).not.toHaveBeenCalled();
  });

  // ── The evaluation drawer ──────────────────────────────────────────────────

  it('opens the drawer on a line, reading its saved values', async () => {
    await setup();
    component.openEdit(component.inquiry.lines[1]);
    expect(component.drawerVisible).toBeTrue();
    expect(component.draft.lineStatus).toBe('PARTIAL');
    expect(component.draft.canSupplyQuantity).toBe(600);
    expect(component.draft.estimatedDeliveryDate?.getDate()).toBe(25);
  });

  it('shows the fields each status needs, and only those', async () => {
    await setup();
    component.openEdit(component.inquiry.lines[3]);

    component.draft.lineStatus = 'PARTIAL';
    expect([component.shows('canSupplyQuantity'), component.shows('estimatedDeliveryDate'), component.shows('rejectionReasonUuid')])
      .toEqual([true, true, false]);

    component.draft.lineStatus = 'CANNOT_SUPPLY';
    expect([component.shows('canSupplyQuantity'), component.shows('rejectionReasonUuid'), component.shows('alternative')])
      .toEqual([false, true, true]);
    expect(component.shows('estimatedDeliveryDate')).withContext('no alternative yet').toBeFalse();
    component.draft.alternativeVariantUuid = 'v-alt';
    expect(component.shows('estimatedDeliveryDate')).toBeTrue();

    component.draft.lineStatus = 'CAN_SUPPLY';
    expect([component.shows('estimatedDeliveryDate'), component.shows('alternative')]).toEqual([true, false]);

    component.draft.lineStatus = 'PENDING';
    expect(component.shows('estimatedDeliveryDate')).toBeFalse();
  });

  it('offers only the active rejection reasons from the API', async () => {
    await setup(inquiry(), true, [reason('DIS'), reason('OOS'), reason('OLD', false)]);
    expect(service.getRejectionReasons).toHaveBeenCalledWith(false);
    component.openEdit(component.inquiry.lines[3]);
    expect(component.reasonOptions.map(o => o.value)).toEqual(['r-DIS', 'r-OOS']);
  });

  it('keeps a deactivated reason a line already has visible, marked as such', async () => {
    await setup(inquiry(), true, [reason('OOS')]);
    component.openEdit(component.inquiry.lines[2]);
    const dis = component.reasonOptions.find(o => o.value === 'r-DIS');
    expect(dis?.label).toContain('deactivated');
  });

  it('will not save CANNOT_SUPPLY without a reason (BR-C1-04) or a PARTIAL quantity out of range (BR-C1-05)', async () => {
    await setup();
    component.openEdit(component.inquiry.lines[3]);
    component.draft.lineStatus = 'CANNOT_SUPPLY';
    component.saveLine();
    expect(service.updateInquiryLine).not.toHaveBeenCalled();
    expect(component.problems.rejectionReasonUuid).toBeDefined();

    component.draft.lineStatus = 'PARTIAL';
    component.draft.canSupplyQuantity = 1500;
    component.draft.estimatedDeliveryDate = new Date(2026, 9, 25);
    component.saveLine();
    expect(service.updateInquiryLine).not.toHaveBeenCalled();
    expect(component.problems.canSupplyQuantity).toBeDefined();
  });

  it('saves an evaluation, closes the drawer and asks the page to reload', async () => {
    await setup();
    component.openEdit(component.inquiry.lines[3]);
    component.draft.lineStatus = 'CANNOT_SUPPLY';
    component.draft.rejectionReasonUuid = 'r-OOS';
    component.draft.rejectionNotes = 'No replenishment';

    component.saveLine();

    expect(service.updateInquiryLine).toHaveBeenCalledWith('inq-1', 'l-4', jasmine.objectContaining({
      lineStatus: 'CANNOT_SUPPLY', rejectionReasonUuid: 'r-OOS', rejectionNotes: 'No replenishment', canSupplyQuantity: null
    } as Partial<UpdateSaleInquiryLineRequest>));
    expect(component.drawerVisible).toBeFalse();
    expect(changed).toHaveBeenCalled();
  });

  it('keeps the drawer open with the server\'s message when it refuses', async () => {
    await setup();
    service.updateInquiryLine.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Rejection reason is not active.' } })));
    component.openEdit(component.inquiry.lines[2]);
    component.saveLine();
    expect(component.drawerVisible).toBeTrue();
    expect(component.saveError).toBe('Rejection reason is not active.');
    expect(changed).not.toHaveBeenCalled();
  });

  it('takes an alternative from the catalogue picker', async () => {
    await setup();
    component.openEdit(component.inquiry.lines[3]);
    component.onAlternativePicked({ productUuid: 'p-9', productName: 'Stainless Sheet', variantUuid: 'v-9', variantName: '316L', variantSku: 'SS-316L' } as any);
    expect([component.draft.alternativeProductUuid, component.draft.alternativeVariantUuid]).toEqual(['p-9', 'v-9']);
    expect(component.draft.alternativeLabel).toContain('Stainless Sheet');
  });

  // ── Add line ───────────────────────────────────────────────────────────────

  it('adds a free-text line with what the customer asked for', async () => {
    await setup();
    component.openAdd();
    component.addMode = 'TEXT';
    component.draft.productDescription = 'Bolt M8x40';
    component.draft.requestedQuantity = 10000;
    component.draft.requestedUomCode = 'EA';
    component.draft.requestedDeliveryDate = new Date(2026, 9, 10);

    component.saveLine();

    const req = service.addInquiryLine.calls.mostRecent().args[1] as SaleInquiryLineRequest;
    expect(req).toEqual({
      productUuid: null, variantUuid: null, productDescription: 'Bolt M8x40', requestedQuantity: 10000,
      requestedUomCode: 'EA', requestedDeliveryDate: '2026-10-10', notes: null
    });
    expect(changed).toHaveBeenCalled();
  });

  it('adds a catalogue line, filling description and UOM from the variant picked', async () => {
    await setup();
    component.openAdd();
    component.addMode = 'CATALOGUE';
    expect(inventory.getProducts).toHaveBeenCalled();
    component.onVariantPicked({ productUuid: 'p-1', productName: 'Bolt', variantUuid: 'v-1', variantName: 'M8x40', variantSku: 'BLT-M8', uomCode: 'EA' } as any);
    component.draft.requestedQuantity = 50;

    expect(component.draft.productDescription).toBe('Bolt — M8x40');
    expect(component.draft.requestedUomCode).toBe('EA');
    component.saveLine();
    expect(service.addInquiryLine.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({ productUuid: 'p-1', variantUuid: 'v-1' }));
  });

  it('needs a catalogue item in catalogue mode', async () => {
    await setup();
    component.openAdd();
    component.addMode = 'CATALOGUE';
    component.draft.productDescription = 'Bolt';
    component.draft.requestedQuantity = 5;
    component.saveLine();
    expect(service.addInquiryLine).not.toHaveBeenCalled();
    expect(component.problems.variantUuid).toBeDefined();
  });

  // ── Delete ─────────────────────────────────────────────────────────────────

  it('deletes a line and asks the page to reload', async () => {
    await setup();
    component.deleteLine(component.inquiry.lines[3]);
    expect(service.deleteInquiryLine).toHaveBeenCalledWith('inq-1', 'l-4');
    expect(changed).toHaveBeenCalled();
  });
});
