import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SaleInquiryDetailComponent } from './sale-inquiry-detail.component';
import {
  SalesPreorderService, SaleInquiry, SaleInquiryLine, UpdateSaleInquiryRequest, CreateQuotationFromInquiryRequest
} from '../../../../services/sales-preorder.service';
import { UserService } from '../../../../services/user.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { InventoryService } from '../../../../services/inventory.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AttachmentPolicyService } from '../../../../services/attachment-policy.service';
import { AuthService } from '../../../service/auth.service';

function line(overrides: Partial<SaleInquiryLine> = {}): SaleInquiryLine {
  return {
    uuid: 'l-1', lineNumber: 1, productDescription: 'Steel Rod 10mm', requestedQuantity: 500, requestedUomCode: 'KG',
    lineStatus: 'CAN_SUPPLY', estimatedDeliveryDate: '2026-10-15T00:00:00', requiresProcurement: false, variantUuid: 'v-1',
    ...overrides
  };
}

function inquiry(overrides: Partial<SaleInquiry> = {}): SaleInquiry {
  return {
    uuid: 'inq-1', traceId: 't', inquiryNumber: 'INQ-2026-00042', partnerId: 'p-1', partnerName: 'GlobalTech Co',
    customerReference: 'GT-RFQ-2026-118', customerReferenceDate: '2026-09-30T00:00:00', status: 'UNDER_REVIEW',
    receivedDate: '2026-10-01T00:00:00', responseDeadline: '2026-10-10T00:00:00', assignedToUserId: 9, assignedToUserName: 'John Smith',
    notes: null, createdBy: 7, createdDate: '2026-10-01T08:00:00', allowedNextStatuses: ['REVIEW_COMPLETE', 'DECLINED'],
    isEditable: true, lines: [line()], quotations: [], ...overrides
  };
}

describe('SaleInquiryDetailComponent (A32-PB-09/11)', () => {
  let fixture: ComponentFixture<SaleInquiryDetailComponent>;
  let component: SaleInquiryDetailComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;
  let navigate: jasmine.Spy;
  let permissions: string[];
  let el: HTMLElement;

  const auth = {
    hasPermission: (code: string) => permissions.includes(code),
    hasAnyPermission: (...codes: string[]) => codes.some(c => permissions.includes(c)),
    getUserData: () => ({ userId: 7, firstName: 'Ayesha', lastName: 'Khan' })
  } as unknown as AuthService;

  async function setup(inq: SaleInquiry | null = inquiry(), tab?: string) {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService',
      ['getInquiry', 'updateInquiry', 'changeInquiryStatus', 'createQuotationFromInquiry', 'getRejectionReasons',
       'addInquiryLine', 'updateInquiryLine', 'deleteInquiryLine']);
    service.getInquiry.and.returnValue(inq ? of({ success: true, message: '', result: inq } as any) : throwError(() => ({ status: 404 })));
    service.getRejectionReasons.and.returnValue(of({ success: true, message: '', result: [] } as any));
    service.updateInquiry.and.returnValue(of({ success: true, message: '' } as any));
    service.createQuotationFromInquiry.and.returnValue(of({ success: true, message: '', result: 'sq-1' } as any));
    const users = jasmine.createSpyObj<UserService>('UserService', ['getUsers']);
    users.getUsers.and.returnValue(of({ success: true, message: '', result: { items: [] } } as any));
    const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(of({ success: true, message: '', result: [{ id: 'cur-usd', name: 'US Dollar', code: 'USD' }] } as any));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getProductById']);
    inventory.getProducts.and.returnValue(of({ success: true, message: '', result: { data: [], totalRecords: 0 } } as any));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl', 'download']);
    attachments.getAttachments.and.returnValue(of({ success: true, message: '', result: [] } as any));
    const policy = jasmine.createSpyObj<AttachmentPolicyService>('AttachmentPolicyService', ['ruleFor']);
    policy.ruleFor.and.returnValue(of(null));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleInquiryDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesPreorderService, useValue: service },
        { provide: UserService, useValue: users },
        { provide: CurrenciesService, useValue: currencies },
        { provide: InventoryService, useValue: inventory },
        { provide: AttachmentService, useValue: attachments },
        { provide: AttachmentPolicyService, useValue: policy },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: {
          paramMap: convertToParamMap({ uuid: 'inq-1' }), queryParamMap: convertToParamMap(tab ? { tab } : {})
        } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleInquiryDetailComponent);
    component = fixture.componentInstance;
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  beforeEach(() => { permissions = ['SALE_INQUIRY_VIEW', 'SALE_INQUIRY_EDIT']; });

  // ── Header ─────────────────────────────────────────────────────────────────

  it('shows number, status, customer, reference and its date, received, deadline and assignee', async () => {
    await setup();
    const header = q('inquiry-header')!.textContent!;
    expect(header).toContain('INQ-2026-00042');
    expect(header).toContain('Under Review');
    expect(header).toContain('GlobalTech Co');
    expect(header).toContain('GT-RFQ-2026-118');
    expect(header).toContain('30 Sep 2026');
    expect(header).toContain('01 Oct 2026');
    expect(header).toContain('10 Oct 2026');
    expect(header).toContain('John Smith');
    expect(q('status-badge')!.classList).toContain('st-orange');
  });

  it('has Details, Lines and Attachments tabs, and opens on Lines when asked', async () => {
    await setup(inquiry(), 'lines');
    const tabs = Array.from(el.querySelectorAll('.p-tabview-nav li, [role="tab"]')).map(t => t.textContent!.trim()).filter(Boolean);
    expect(tabs.join('|')).toContain('Details');
    expect(tabs.join('|')).toContain('Lines');
    expect(tabs.join('|')).toContain('Attachments');
    expect(component.activeTab).toBe(1);
  });

  it('says so when the inquiry is not there', async () => {
    await setup(null);
    expect(component.notFound).toBeTrue();
    expect(q('not-found')).not.toBeNull();
  });

  // ── Transitions ────────────────────────────────────────────────────────────

  it('offers Mark Review Complete and Decline when allowed to someone who may edit', async () => {
    await setup();
    expect(q('action-complete')).not.toBeNull();
    expect(component.completeBlocker).toBeNull();
    expect(q('action-decline')).not.toBeNull();
    expect(component.declineBlocker).toBeNull();
  });

  it('disables Mark Review Complete with the reason while lines are undecided', async () => {
    await setup(inquiry({
      allowedNextStatuses: ['DECLINED'],
      lines: [line(), line({ uuid: 'l-2', lineNumber: 2, lineStatus: 'PENDING' }), line({ uuid: 'l-3', lineNumber: 3, lineStatus: 'UNDER_REVIEW' })]
    }));
    expect(q('action-complete')).not.toBeNull();
    expect(component.completeBlocker).toContain('2 lines');
    expect(q('action-complete')!.querySelector('button')!.disabled).toBeTrue();
    expect(q('complete-blocker')!.textContent).toContain('2 lines');
  });

  it('starts the review of a received inquiry once it has a line, and explains why not before', async () => {
    await setup(inquiry({ status: 'RECEIVED', allowedNextStatuses: [], lines: [] }));
    expect(q('action-start-review')).not.toBeNull();
    expect(component.startReviewBlocker).toContain('line');
    expect(component.declineBlocker).withContext('declining comes after review begins').not.toBeNull();

    await setup(inquiry({ status: 'RECEIVED', allowedNextStatuses: ['UNDER_REVIEW'] }));
    expect(component.startReviewBlocker).toBeNull();
    service.changeInquiryStatus.and.returnValue(of({ success: true, message: '', result: inquiry({ status: 'UNDER_REVIEW' }) } as any));
    component.changeStatus('UNDER_REVIEW');
    expect(service.changeInquiryStatus).toHaveBeenCalledWith('inq-1', { status: 'UNDER_REVIEW' });
    expect(component.inquiry!.status).toBe('UNDER_REVIEW');
  });

  it('marks the review complete, and shows the server\'s refusal when it says no', async () => {
    await setup();
    service.changeInquiryStatus.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Line 2 is still pending.' } })));
    const toast = spyOn(component['messageService'], 'add');
    component.changeStatus('REVIEW_COMPLETE');
    expect(service.changeInquiryStatus).toHaveBeenCalledWith('inq-1', { status: 'REVIEW_COMPLETE' });
    expect(toast).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'Line 2 is still pending.' }));
  });

  it('declines only with a reason', async () => {
    await setup();
    service.changeInquiryStatus.and.returnValue(of({ success: true, message: '', result: inquiry({ status: 'DECLINED', isEditable: false, allowedNextStatuses: [] }) } as any));
    component.openDeclineDialog();
    component.declineReason = '   ';
    component.decline();
    expect(service.changeInquiryStatus).not.toHaveBeenCalled();

    component.declineReason = ' Customer went elsewhere ';
    component.decline();
    expect(service.changeInquiryStatus).toHaveBeenCalledWith('inq-1', { status: 'DECLINED', reason: 'Customer went elsewhere' });
    expect(component.declineDialogVisible).toBeFalse();
  });

  it('hides every transition from someone who may only view', async () => {
    permissions = ['SALE_INQUIRY_VIEW'];
    await setup();
    expect(q('action-complete')).toBeNull();
    expect(q('action-decline')).toBeNull();
    expect(q('action-start-review')).toBeNull();
    expect(q('edit-header')).toBeNull();
  });

  // ── Read-only in QUOTED / DECLINED (BR-C1-06) ───────────────────────────────

  for (const status of ['QUOTED', 'DECLINED'] as const) {
    it(`is read-only once ${status}`, async () => {
      await setup(inquiry({ status, isEditable: false, allowedNextStatuses: [], declineReason: status === 'DECLINED' ? 'Too late' : null }));
      expect(component.canEdit).toBeFalse();
      expect(q('action-complete')).toBeNull();
      expect(q('action-decline')).toBeNull();
      expect(q('edit-header')).toBeNull();
      expect(q('read-only-note')).not.toBeNull();
    });
  }

  it('shows why a declined inquiry was declined', async () => {
    await setup(inquiry({ status: 'DECLINED', isEditable: false, allowedNextStatuses: [], declineReason: 'Customer went elsewhere' }));
    expect(q('decline-reason')!.textContent).toContain('Customer went elsewhere');
  });

  // ── Details tab: header edit ───────────────────────────────────────────────

  it('saves the header with the days picked and reloads', async () => {
    await setup();
    component.startHeaderEdit();
    expect(component.header.receivedDate?.getDate()).withContext('read as its own day').toBe(1);
    component.header.responseDeadline = new Date(2026, 9, 12);
    component.header.customerReference = ' GT-RFQ-2026-119 ';

    component.saveHeader();

    const req = service.updateInquiry.calls.mostRecent().args[1] as UpdateSaleInquiryRequest;
    expect(req).toEqual({
      customerReference: 'GT-RFQ-2026-119', customerReferenceDate: '2026-09-30', receivedDate: '2026-10-01',
      responseDeadline: '2026-10-12', assignedToUserId: 9, notes: null
    });
    expect(service.getInquiry).toHaveBeenCalledTimes(2);
    expect(component.editingHeader).toBeFalse();
  });

  // ── Create quotation ───────────────────────────────────────────────────────

  it('offers Create Quotation on a completed review, only with SALE_QUOTATION_CREATE', async () => {
    await setup(inquiry({ status: 'REVIEW_COMPLETE', allowedNextStatuses: ['DECLINED'] }));
    expect(q('action-create-quotation')).toBeNull();

    permissions = ['SALE_INQUIRY_VIEW', 'SALE_QUOTATION_CREATE'];
    await setup(inquiry({ status: 'REVIEW_COMPLETE', allowedNextStatuses: ['DECLINED'] }));
    expect(q('action-create-quotation')).not.toBeNull();

    await setup(inquiry({ status: 'UNDER_REVIEW' }));
    expect(q('action-create-quotation')).withContext('not before the review is complete').toBeNull();
  });

  it('creates the quotation with the days picked and opens it', async () => {
    permissions = ['SALE_INQUIRY_VIEW', 'SALE_QUOTATION_CREATE'];
    await setup(inquiry({ status: 'REVIEW_COMPLETE', allowedNextStatuses: ['DECLINED'] }));
    component.openQuotationDialog();
    component.quotation.validFrom = new Date(2026, 9, 1);
    component.quotation.validTo = new Date(2026, 9, 31);
    component.quotation.paymentTerms = ' Net 30 ';

    component.createQuotation();

    const req = service.createQuotationFromInquiry.calls.mostRecent().args[1] as CreateQuotationFromInquiryRequest;
    expect(req).toEqual(jasmine.objectContaining({ validFrom: '2026-10-01', validTo: '2026-10-31', paymentTerms: 'Net 30', currencyId: null }));
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/sales/quotations', 'sq-1']);
  });

  it('will not create a quotation valid until before it starts', async () => {
    permissions = ['SALE_INQUIRY_VIEW', 'SALE_QUOTATION_CREATE'];
    await setup(inquiry({ status: 'REVIEW_COMPLETE', allowedNextStatuses: ['DECLINED'] }));
    component.openQuotationDialog();
    component.quotation.validFrom = new Date(2026, 9, 10);
    component.quotation.validTo = new Date(2026, 9, 1);

    expect(component.quotationProblem).not.toBeNull();
    component.createQuotation();
    expect(service.createQuotationFromInquiry).not.toHaveBeenCalled();
  });

  it('names the supplied lines that still need a catalogue item before quoting (contract 4.1)', async () => {
    permissions = ['SALE_INQUIRY_VIEW', 'SALE_QUOTATION_CREATE'];
    await setup(inquiry({
      status: 'REVIEW_COMPLETE', allowedNextStatuses: ['DECLINED'],
      lines: [line(), line({ uuid: 'l-2', lineNumber: 2, variantUuid: null, lineStatus: 'PARTIAL', canSupplyQuantity: 5 }),
              line({ uuid: 'l-3', lineNumber: 3, variantUuid: null, lineStatus: 'CANNOT_SUPPLY' })]
    }));
    component.openQuotationDialog();
    component.quotation.validTo = new Date(2099, 0, 1);
    expect(component.quotationProblem).toContain('line 2');
    expect(component.quotationProblem).not.toContain('line 3');
  });

  it('links the quotations made from the inquiry', async () => {
    await setup(inquiry({ status: 'QUOTED', isEditable: false, allowedNextStatuses: [], quotations: [{ uuid: 'sq-1', number: 'SQ-2026-00007', status: 'DRAFT' }] }));
    expect(q('quotation-links')!.textContent).toContain('SQ-2026-00007');
  });

  // ── Attachments (A32-PB-11) ────────────────────────────────────────────────

  it('hands the Attachments tab to the shared panel under SALE_INQUIRY', async () => {
    await setup(inquiry(), 'attachments');
    fixture.detectChanges();
    const panel = el.querySelector('app-attachment-list');
    expect(panel).not.toBeNull();
    expect(component.attachmentCode).toBe('SALE_INQUIRY');
  });
});
