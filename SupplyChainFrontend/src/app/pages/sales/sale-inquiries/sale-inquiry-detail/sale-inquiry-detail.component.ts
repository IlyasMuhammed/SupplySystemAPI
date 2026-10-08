import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { SelectModule } from 'primeng/select';
import { DatePickerModule } from 'primeng/datepicker';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { TabViewModule } from 'primeng/tabview';
import { MessageService } from 'primeng/api';

import {
  SalesPreorderService, SaleInquiry, SaleInquiryStatus, UpdateSaleInquiryRequest, CreateQuotationFromInquiryRequest,
  SALES_ATTACHMENT_CODES
} from '../../../../services/sales-preorder.service';
import { UserService } from '../../../../services/user.service';
import { CurrenciesService, CurrencyModel } from '../../../../services/currencies.service';
import { MoneyService } from '../../../../services/money.service';
import { orgActiveCurrencyOptions } from '../../../../shared/doc-currency/doc-currency-picker';
import { AuthService } from '../../../service/auth.service';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';
import { SaleInquiryLinesComponent } from '../sale-inquiry-lines/sale-inquiry-lines.component';
import {
  AssigneeOption, INQUIRY_STATUS_COLOR, MAX, StatusColor, assigneeOptions$, displayDate, readDate, serverMessage, statusLabel,
  undecidedLineCount, writeDate
} from '../sale-inquiry.shared';

const TABS = { details: 0, lines: 1, attachments: 2 } as const;

/** What the Details tab edits (PUT header). Dates are picker values. */
export interface HeaderDraft {
  customerReference: string;
  customerReferenceDate: Date | null;
  receivedDate: Date | null;
  responseDeadline: Date | null;
  assignedToUserId: number | null;
  notes: string;
}

/** What the Create Quotation dialog asks for (POST …/create-quotation). */
export interface QuotationDraft {
  currencyId: string | null;
  validFrom: Date | null;
  validTo: Date | null;
  paymentTerms: string;
  deliveryTerms: string;
  notes: string;
  internalNotes: string;
}

/**
 * A32-PB-09/11 — one sale inquiry (spec §10.2): its header, the Details / Lines / Attachments tabs and the status
 * actions. Transitions are driven by the server's allowedNextStatuses and need SALE_INQUIRY_EDIT; a transition that
 * cannot happen yet is shown disabled with the reason. QUOTED and DECLINED are read-only (BR-C1-06). "Create
 * Quotation" (REVIEW_COMPLETE, SALE_QUOTATION_CREATE) opens the new quotation (FE-QUO's sales/quotations/:uuid).
 */
@Component({
  selector: 'app-sale-inquiry-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, ButtonModule, TooltipModule, ToastModule, DialogModule, SelectModule,
    DatePickerModule, InputTextModule, TextareaModule, TabViewModule, AttachmentListComponent, SaleInquiryLinesComponent
  ],
  templateUrl: './sale-inquiry-detail.component.html',
  styleUrls: ['../sale-inquiry.shared.scss', './sale-inquiry-detail.component.scss'],
  providers: [MessageService]
})
export class SaleInquiryDetailComponent implements OnInit {
  readonly attachmentCode = SALES_ATTACHMENT_CODES.inquiry;
  readonly max = MAX;
  readonly displayDate = displayDate;
  readonly statusLabel = statusLabel;

  uuid = '';
  inquiry: SaleInquiry | null = null;
  isLoading = true;
  notFound = false;
  activeTab: number = TABS.details;
  busyStatus: SaleInquiryStatus | null = null;

  // Details tab — header edit
  editingHeader = false;
  header: HeaderDraft = SaleInquiryDetailComponent.emptyHeader();
  headerSubmitted = false;
  isSavingHeader = false;
  headerError = '';
  assigneeOptions: AssigneeOption[] = [];

  // Decline
  declineDialogVisible = false;
  declineReason = '';
  declineSubmitted = false;

  // Create quotation
  quotationDialogVisible = false;
  quotation: QuotationDraft = SaleInquiryDetailComponent.emptyQuotation();
  quotationSubmitted = false;
  isCreatingQuotation = false;
  quotationError = '';
  currencyOptions: { label: string; value: string }[] = [];
  private currenciesRequested = false;
  /** A35 D-1 — the global catalogue; the dialog offers the org's active currencies plus the inquiry's own. */
  private currencyCatalog: CurrencyModel[] = [];
  private readonly money = inject(MoneyService);

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private service: SalesPreorderService,
    private userService: UserService,
    private currenciesService: CurrenciesService,
    public authService: AuthService,
    private messageService: MessageService
  ) {}

  private static emptyHeader(): HeaderDraft {
    return { customerReference: '', customerReferenceDate: null, receivedDate: null, responseDeadline: null, assignedToUserId: null, notes: '' };
  }

  private static today(): Date {
    const now = new Date();
    return new Date(now.getFullYear(), now.getMonth(), now.getDate());
  }

  private static emptyQuotation(): QuotationDraft {
    const from = SaleInquiryDetailComponent.today();
    const to = new Date(from.getFullYear(), from.getMonth(), from.getDate() + 30);
    return { currencyId: null, validFrom: from, validTo: to, paymentTerms: '', deliveryTerms: '', notes: '', internalNotes: '' };
  }

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    const tab = this.route.snapshot.queryParamMap?.get('tab') as keyof typeof TABS | null;
    if (tab && tab in TABS) this.activeTab = TABS[tab];
    this.load();
  }

  load() {
    if (!this.uuid) { this.isLoading = false; this.notFound = true; return; }
    this.isLoading = true;

    this.service.getInquiry(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.inquiry = res.success && res.result ? res.result : null;
        this.notFound = !this.inquiry;
      },
      error: (err) => {
        this.isLoading = false;
        this.inquiry = null;
        this.notFound = err?.status === 404;
        if (!this.notFound) this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load the inquiry.' });
      }
    });
  }

  // ── What this user may do, and what the inquiry allows ──────────────────────

  get holdsEdit(): boolean { return this.authService.hasPermission('SALE_INQUIRY_EDIT'); }

  /** Header and lines can change: the inquiry is open (not QUOTED/DECLINED) and the user may edit. */
  get canEdit(): boolean { return !!this.inquiry?.isEditable && this.holdsEdit; }

  private allowed(status: SaleInquiryStatus): boolean {
    return !!this.inquiry?.allowedNextStatuses?.includes(status);
  }

  private get status(): SaleInquiryStatus | undefined { return this.inquiry?.status; }

  get showStartReview(): boolean { return this.canEdit && this.status === 'RECEIVED'; }
  get showComplete(): boolean { return this.canEdit && (this.status === 'RECEIVED' || this.status === 'UNDER_REVIEW'); }
  get showDecline(): boolean {
    return this.canEdit && (this.status === 'RECEIVED' || this.status === 'UNDER_REVIEW' || this.status === 'REVIEW_COMPLETE');
  }

  get undecidedCount(): number { return undecidedLineCount(this.inquiry?.lines ?? []); }

  get startReviewBlocker(): string | null {
    if (this.allowed('UNDER_REVIEW')) return null;
    if (!this.inquiry?.lines.length) return 'Add at least one line before starting the review.';
    return 'The review cannot be started right now.';
  }

  get completeBlocker(): string | null {
    if (this.allowed('REVIEW_COMPLETE')) return null;
    if (this.status === 'RECEIVED') return 'Start the review first.';
    if (!this.inquiry?.lines.length) return 'Add at least one line first.';
    const n = this.undecidedCount;
    if (n > 0) return `${n} line${n === 1 ? ' is' : 's are'} still undecided (Pending or Under Review) — evaluate every line first.`;
    return 'The review cannot be completed right now.';
  }

  get declineBlocker(): string | null {
    if (this.allowed('DECLINED')) return null;
    if (this.status === 'RECEIVED') return 'Start the review first — an inquiry is declined once its review has begun.';
    return 'The inquiry cannot be declined right now.';
  }

  get canCreateQuotation(): boolean {
    return this.status === 'REVIEW_COMPLETE' && this.authService.hasPermission('SALE_QUOTATION_CREATE');
  }

  // ── Display ─────────────────────────────────────────────────────────────────

  statusColor(status: SaleInquiryStatus): StatusColor { return INQUIRY_STATUS_COLOR[status] ?? 'grey'; }

  get assigneeName(): string {
    const i = this.inquiry;
    if (!i?.assignedToUserId) return 'Unassigned';
    return i.assignedToUserName || `User #${i.assignedToUserId}`;
  }

  // ── Status changes ──────────────────────────────────────────────────────────

  changeStatus(status: 'UNDER_REVIEW' | 'REVIEW_COMPLETE') {
    if (!this.inquiry || this.busyStatus) return;
    this.busyStatus = status;
    this.service.changeInquiryStatus(this.uuid, { status }).subscribe({
      next: (res) => {
        this.busyStatus = null;
        this.messageService.add({
          severity: 'success',
          summary: status === 'UNDER_REVIEW' ? 'Review started' : 'Review complete',
          detail: status === 'REVIEW_COMPLETE' ? 'A quotation can now be created from this inquiry.' : undefined
        });
        if (res?.result) this.inquiry = res.result; else this.load();
      },
      error: (err) => {
        this.busyStatus = null;
        this.messageService.add({ severity: 'error', summary: 'Not changed', detail: serverMessage(err, 'The status could not be changed.') });
      }
    });
  }

  openDeclineDialog() {
    if (!this.showDecline || this.declineBlocker) return;
    this.declineReason = '';
    this.declineSubmitted = false;
    this.declineDialogVisible = true;
  }

  get declineProblem(): string | null {
    const reason = this.declineReason.trim();
    if (!reason) return 'Say why the inquiry is declined.';
    if (reason.length > MAX.declineReason) return `At most ${MAX.declineReason} characters.`;
    return null;
  }

  decline() {
    this.declineSubmitted = true;
    if (this.declineProblem || this.busyStatus) return;
    this.busyStatus = 'DECLINED';
    this.service.changeInquiryStatus(this.uuid, { status: 'DECLINED', reason: this.declineReason.trim() }).subscribe({
      next: (res) => {
        this.busyStatus = null;
        this.declineDialogVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Inquiry declined' });
        if (res?.result) this.inquiry = res.result; else this.load();
      },
      error: (err) => {
        this.busyStatus = null;
        this.messageService.add({ severity: 'error', summary: 'Not declined', detail: serverMessage(err, 'The inquiry could not be declined.') });
      }
    });
  }

  // ── Details tab: the header ─────────────────────────────────────────────────

  startHeaderEdit() {
    const i = this.inquiry;
    if (!i || !this.canEdit) return;
    this.header = {
      customerReference: i.customerReference ?? '',
      customerReferenceDate: readDate(i.customerReferenceDate),
      receivedDate: readDate(i.receivedDate),
      responseDeadline: readDate(i.responseDeadline),
      assignedToUserId: i.assignedToUserId ?? null,
      notes: i.notes ?? ''
    };
    this.headerSubmitted = false;
    this.headerError = '';
    this.editingHeader = true;
    assigneeOptions$(this.userService, this.authService, { id: i.assignedToUserId, name: i.assignedToUserName })
      .subscribe(options => this.assigneeOptions = options);
  }

  cancelHeaderEdit() { this.editingHeader = false; }

  get headerProblem(): string | null {
    const h = this.header;
    if (!h.receivedDate) return 'Say when the inquiry was received.';
    if (h.responseDeadline && h.responseDeadline < h.receivedDate) return 'The response deadline cannot be before the day it was received.';
    if (h.customerReference.trim().length > MAX.customerReference) return `The customer reference is at most ${MAX.customerReference} characters.`;
    if (h.notes.trim().length > MAX.headerNotes) return `Notes are at most ${MAX.headerNotes} characters.`;
    return null;
  }

  buildHeaderRequest(): UpdateSaleInquiryRequest {
    const h = this.header;
    return {
      customerReference: h.customerReference.trim() || null,
      customerReferenceDate: writeDate(h.customerReferenceDate),
      receivedDate: writeDate(h.receivedDate)!,
      responseDeadline: writeDate(h.responseDeadline),
      assignedToUserId: h.assignedToUserId ?? null,
      notes: h.notes.trim() || null
    };
  }

  saveHeader() {
    this.headerSubmitted = true;
    if (this.headerProblem || this.isSavingHeader || !this.canEdit) return;
    this.isSavingHeader = true;
    this.headerError = '';
    this.service.updateInquiry(this.uuid, this.buildHeaderRequest()).subscribe({
      next: () => {
        this.isSavingHeader = false;
        this.editingHeader = false;
        this.messageService.add({ severity: 'success', summary: 'Inquiry saved' });
        this.load();
      },
      error: (err) => {
        this.isSavingHeader = false;
        this.headerError = serverMessage(err, 'The inquiry could not be saved.');
      }
    });
  }

  // ── Create quotation ────────────────────────────────────────────────────────

  openQuotationDialog() {
    if (!this.canCreateQuotation) return;
    this.quotation = SaleInquiryDetailComponent.emptyQuotation();
    this.quotationSubmitted = false;
    this.quotationError = '';
    this.quotationDialogVisible = true;
    if (!this.currenciesRequested) {
      this.currenciesRequested = true;
      this.currenciesService.getAll().subscribe({
        next: (res) => {
          this.currencyCatalog = res.result ?? [];
          this.rebuildCurrencyOptions();
        },
        error: () => { this.currenciesRequested = false; }
      });
      this.money.load().subscribe(() => this.rebuildCurrencyOptions());
    }
  }

  /** Org-active currencies, keeping the inquiry's own (the server's default for the quotation) even if deactivated since. */
  private rebuildCurrencyOptions() {
    const keep = this.quotation.currencyId ?? this.inquiry?.currencyId ?? null;
    this.currencyOptions = orgActiveCurrencyOptions(this.money, this.currencyCatalog, keep);
  }

  /** Supplied lines (CAN_SUPPLY / PARTIAL) with no catalogue item cannot be quoted (contract §4.1). */
  get unidentifiedLines(): number[] {
    return (this.inquiry?.lines ?? [])
      .filter(l => (l.lineStatus === 'CAN_SUPPLY' || l.lineStatus === 'PARTIAL') && !l.variantUuid)
      .map(l => l.lineNumber);
  }

  get quotationProblem(): string | null {
    const q = this.quotation;
    if (!q.validTo) return 'Say until when the quotation is valid.';
    if (q.validFrom && q.validTo < q.validFrom) return 'The quotation cannot expire before it starts.';
    const missing = this.unidentifiedLines;
    if (missing.length) {
      return `Identify the catalogue item first on ${missing.map(n => `line ${n}`).join(', ')}: a line that can be supplied needs one to be quoted.`;
    }
    return null;
  }

  buildQuotationRequest(): CreateQuotationFromInquiryRequest {
    const q = this.quotation;
    return {
      currencyId: q.currencyId || null,
      validFrom: writeDate(q.validFrom),
      validTo: writeDate(q.validTo)!,
      paymentTerms: q.paymentTerms.trim() || null,
      deliveryTerms: q.deliveryTerms.trim() || null,
      notes: q.notes.trim() || null,
      internalNotes: q.internalNotes.trim() || null
    };
  }

  createQuotation() {
    this.quotationSubmitted = true;
    if (this.quotationProblem || this.isCreatingQuotation || !this.canCreateQuotation) return;
    this.isCreatingQuotation = true;
    this.quotationError = '';
    this.service.createQuotationFromInquiry(this.uuid, this.buildQuotationRequest()).subscribe({
      next: (res) => {
        this.isCreatingQuotation = false;
        this.quotationDialogVisible = false;
        if (res.result) this.router.navigate(['/portal/pages/sales/quotations', res.result]);
        else this.load();
      },
      error: (err) => {
        this.isCreatingQuotation = false;
        this.quotationError = serverMessage(err, 'The quotation could not be created.');
      }
    });
  }
}
