import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TableModule } from 'primeng/table';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { TooltipModule } from 'primeng/tooltip';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { InputNumberModule } from 'primeng/inputnumber';
import { MessageService, ConfirmationService } from 'primeng/api';
import { ProgressSpinnerModule } from 'primeng/progressspinner';
import {
  WarehouseService, SroDetailModel,
  ApproveSroRequest, RejectSroRequest, DispatchSroRequest,
  ConfirmReceiptSroRequest, ResolveSroRequest
} from '../../../../services/warehouse.service';
import { FinanceService, CreateDebitNoteRequest } from '../../../../services/finance.service';
import { ReportsService, AuditLogItemModel } from '../../../../services/reports.service';

@Component({
  selector: 'app-sro-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, ToastModule, TableModule, DialogModule,
    InputTextModule, TextareaModule, TooltipModule, DropdownModule,
    CalendarModule, InputNumberModule, ProgressSpinnerModule
  ],
  templateUrl: './sro-detail.component.html',
  styleUrls: ['./sro-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class SroDetailComponent implements OnInit {
  uuid = '';
  sro: SroDetailModel | null = null;
  isLoading = true;

  // ── Approve dialog ────────────────────────────────────────────────────────
  showApproveDialog = false;
  approveNotes      = '';
  isApproving       = false;

  // ── Reject dialog ─────────────────────────────────────────────────────────
  showRejectDialog = false;
  rejectReason     = '';
  isRejecting      = false;

  // ── Dispatch dialog ───────────────────────────────────────────────────────
  showDispatchDialog   = false;
  dispatchRmaNumber    = '';
  dispatchDate: Date | null = null;
  dispatchCarrier      = '';
  dispatchTrackingRef  = '';
  isDispatching        = false;

  // ── Confirm receipt dialog ────────────────────────────────────────────────
  showConfirmReceiptDialog = false;
  confirmReceiptNotes      = '';
  isConfirmingReceipt      = false;

  // ── Resolve dialog ────────────────────────────────────────────────────────
  showResolveDialog = false;
  resolveType: string = '';
  creditNoteNumber    = '';
  creditAmount: number | null = null;
  debitNoteNumber     = '';
  debitAmount: number | null = null;
  replacementPoUuid   = '';
  resolveNotes        = '';
  isResolving         = false;

  resolveTypeOptions = [
    { label: 'Credit Note',  value: 'CREDIT' },
    { label: 'Replacement',  value: 'REPLACEMENT' },
    { label: 'Debit Note',   value: 'DEBIT' }
  ];

  // ── Audit Trail ───────────────────────────────────────────────────────────
  auditTrail: AuditLogItemModel[] = [];
  isLoadingAudit = false;
  showAuditTrail = false;

  // ── Expect Replacement ────────────────────────────────────────────────────
  isExpectingReplacement = false;

  // ── Create Debit Note dialog ──────────────────────────────────────────────
  showDebitNoteDialog   = false;
  debitNoteReason       = '';
  debitNoteReasonDetail = '';
  debitNoteAmount: number | null = null;
  debitNoteNotes        = '';
  isCreatingDebitNote   = false;

  debitReasonOptions = [
    { label: 'Damaged',         value: 'DAMAGED' },
    { label: 'Defective',       value: 'DEFECTIVE' },
    { label: 'Quality Failure', value: 'QUALITY_FAIL' },
    { label: 'Wrong Item',      value: 'WRONG_ITEM' },
    { label: 'Short Expiry',    value: 'SHORT_EXPIRY' },
    { label: 'Spec Mismatch',   value: 'SPEC_MISMATCH' },
    { label: 'Wrong Quantity',  value: 'WRONG_QTY' },
    { label: 'Other',           value: 'OTHER' }
  ];

  constructor(
    private route: ActivatedRoute,
    private warehouseService: WarehouseService,
    private financeService: FinanceService,
    private reportsService: ReportsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.route.params.subscribe(p => { this.uuid = p['uuid']; this.load(); });
  }

  load() {
    this.isLoading = true;
    this.warehouseService.getSroById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.sro = res.success ? res.result : null;
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load supplier return order.' });
      }
    });
  }

  // ── Approve (DRAFT → APPROVED) ────────────────────────────────────────────

  openApproveDialog() { this.approveNotes = ''; this.showApproveDialog = true; }

  confirmApprove() {
    if (!this.sro) return;
    this.isApproving = true;
    const req: ApproveSroRequest = { notes: this.approveNotes || undefined };
    this.warehouseService.approveSro(this.sro.uuid, req).subscribe({
      next: (res) => {
        this.isApproving = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Approved', detail: 'Return order approved.' });
          this.showApproveDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isApproving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Approval failed.' });
      }
    });
  }

  // ── Reject (DRAFT → REJECTED) ─────────────────────────────────────────────

  openRejectDialog() { this.rejectReason = ''; this.showRejectDialog = true; }

  confirmReject() {
    if (!this.sro || !this.rejectReason.trim()) return;
    this.isRejecting = true;
    const req: RejectSroRequest = { reason: this.rejectReason };
    this.warehouseService.rejectSro(this.sro.uuid, req).subscribe({
      next: (res) => {
        this.isRejecting = false;
        if (res.success) {
          this.messageService.add({ severity: 'warn', summary: 'Rejected', detail: 'Return order rejected.' });
          this.showRejectDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isRejecting = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Rejection failed.' });
      }
    });
  }

  // ── Dispatch (APPROVED → DISPATCHED) ─────────────────────────────────────

  openDispatchDialog() {
    this.dispatchRmaNumber = ''; this.dispatchDate = null;
    this.dispatchCarrier   = ''; this.dispatchTrackingRef = '';
    this.showDispatchDialog = true;
  }

  confirmDispatch() {
    if (!this.sro || !this.dispatchDate) return;
    this.isDispatching = true;
    const req: DispatchSroRequest = {
      rmaNumber:          this.dispatchRmaNumber   || undefined,
      dispatchDate:       this.dispatchDate.toISOString().split('T')[0],
      dispatchCarrier:    this.dispatchCarrier     || undefined,
      dispatchTrackingRef:this.dispatchTrackingRef || undefined
    };
    this.warehouseService.dispatchSro(this.sro.uuid, req).subscribe({
      next: (res) => {
        this.isDispatching = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Dispatched', detail: 'Return dispatched to supplier.' });
          this.showDispatchDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isDispatching = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Dispatch failed.' });
      }
    });
  }

  // ── Confirm Receipt (DISPATCHED → SUPPLIER_RECEIVED) ──────────────────────

  openConfirmReceiptDialog() { this.confirmReceiptNotes = ''; this.showConfirmReceiptDialog = true; }

  confirmReceipt() {
    if (!this.sro) return;
    this.isConfirmingReceipt = true;
    const req: ConfirmReceiptSroRequest = { notes: this.confirmReceiptNotes || undefined };
    this.warehouseService.confirmReceiptSro(this.sro.uuid, req).subscribe({
      next: (res) => {
        this.isConfirmingReceipt = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Receipt Confirmed', detail: 'Supplier acknowledged receipt.' });
          this.showConfirmReceiptDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isConfirmingReceipt = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Confirmation failed.' });
      }
    });
  }

  // ── Resolve (SUPPLIER_RECEIVED → RESOLVED_*) ──────────────────────────────

  openResolveDialog() {
    this.resolveType = ''; this.creditNoteNumber = ''; this.creditAmount = null;
    this.debitNoteNumber = ''; this.debitAmount = null;
    this.replacementPoUuid = ''; this.resolveNotes = '';
    this.showResolveDialog = true;
  }

  confirmResolve() {
    if (!this.sro || !this.resolveType) return;
    this.isResolving = true;
    const req: ResolveSroRequest = {
      resolutionType:   this.resolveType,
      creditNoteNumber: this.creditNoteNumber  || undefined,
      creditAmount:     this.creditAmount      ?? undefined,
      debitNoteNumber:  this.debitNoteNumber   || undefined,
      debitAmount:      this.debitAmount       ?? undefined,
      replacementPoUuid:this.replacementPoUuid || undefined,
      notes:            this.resolveNotes      || undefined
    };
    this.warehouseService.resolveSro(this.sro.uuid, req).subscribe({
      next: (res) => {
        this.isResolving = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Resolved', detail: 'Return order resolved.' });
          this.showResolveDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isResolving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Resolution failed.' });
      }
    });
  }

  // ── Expect Replacement (SUPPLIER_RECEIVED → AWAITING_REPLACEMENT) ─────────

  expectReplacement() {
    if (!this.sro) return;
    this.isExpectingReplacement = true;
    this.warehouseService.expectReplacementSro(this.sro.uuid).subscribe({
      next: (res) => {
        this.isExpectingReplacement = false;
        if (res.success) {
          this.messageService.add({ severity: 'info', summary: 'Awaiting Replacement', detail: 'SRO is now tracking expected replacement delivery.' });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isExpectingReplacement = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Action failed.' });
      }
    });
  }

  // ── Create Debit Note ─────────────────────────────────────────────────────

  openDebitNoteDialog() {
    this.debitNoteReason = ''; this.debitNoteReasonDetail = '';
    this.debitNoteAmount = null; this.debitNoteNotes = '';
    this.showDebitNoteDialog = true;
  }

  confirmCreateDebitNote() {
    if (!this.sro || !this.debitNoteReason || !this.debitNoteAmount) return;
    this.isCreatingDebitNote = true;
    const req: CreateDebitNoteRequest = {
      sroId:             this.sro.uuid,
      debitReason:       this.debitNoteReason,
      debitReasonDetail: this.debitNoteReasonDetail || undefined,
      debitAmount:       this.debitNoteAmount,
      notes:             this.debitNoteNotes || undefined
    };
    this.financeService.createDebitNote(req).subscribe({
      next: (res) => {
        this.isCreatingDebitNote = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Debit Note Created', detail: 'Debit note raised and SRO resolved.' });
          this.showDebitNoteDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isCreatingDebitNote = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to create debit note.' });
      }
    });
  }

  // ── Status / label helpers ────────────────────────────────────────────────

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'APPROVED':             return 'success';
      case 'DISPATCHED':           return 'info';
      case 'SUPPLIER_RECEIVED':    return 'info';
      case 'AWAITING_REPLACEMENT': return 'warn';
      case 'RESOLVED_CREDIT':
      case 'RESOLVED_REPLACEMENT':
      case 'RESOLVED_DEBIT':       return 'success';
      case 'REJECTED':
      case 'ESCALATED':            return 'danger';
      case 'DRAFT':                return 'secondary';
      default:                     return 'secondary';
    }
  }

  getStatusLabel(s: string): string {
    const map: Record<string, string> = {
      DRAFT:                'Draft',
      APPROVED:             'Approved',
      REJECTED:             'Rejected',
      DISPATCHED:           'Dispatched',
      SUPPLIER_RECEIVED:    'Supplier Received',
      AWAITING_REPLACEMENT: 'Awaiting Replacement',
      RESOLVED_CREDIT:      'Resolved (Credit)',
      RESOLVED_REPLACEMENT: 'Resolved (Replacement)',
      RESOLVED_DEBIT:       'Resolved (Debit)',
      ESCALATED:            'Escalated'
    };
    return map[s] ?? s;
  }

  getSroTypeLabel(t: string): string {
    const map: Record<string, string> = {
      GRN_REJECTION:       'GRN Rejection',
      POST_RECEIPT_DEFECT: 'Post-Receipt Defect',
      WRONG_ITEM:          'Wrong Item',
      OVERDELIVERY:        'Overdelivery'
    };
    return map[t] ?? t;
  }

  getReasonLabel(r: string): string {
    const map: Record<string, string> = {
      DAMAGED:      'Damaged',
      DEFECTIVE:    'Defective',
      WRONG_ITEM:   'Wrong Item',
      WRONG_QTY:    'Wrong Quantity',
      SHORT_EXPIRY: 'Short Expiry',
      SPEC_MISMATCH:'Spec Mismatch',
      DUPLICATE:    'Duplicate',
      QUALITY_FAIL: 'Quality Failure',
      OTHER:        'Other'
    };
    return map[r] ?? r;
  }

  get canApprove():        boolean { return this.sro?.status === 'DRAFT'; }
  get canReject():         boolean { return this.sro?.status === 'DRAFT'; }
  get canDispatch():       boolean { return this.sro?.status === 'APPROVED'; }
  get canConfirmReceipt(): boolean { return this.sro?.status === 'DISPATCHED'; }
  get canResolve():            boolean {
    return this.sro?.status === 'SUPPLIER_RECEIVED' || this.sro?.status === 'AWAITING_REPLACEMENT';
  }
  get canExpectReplacement():  boolean { return this.sro?.status === 'SUPPLIER_RECEIVED'; }
  get canCreateDebitNote():    boolean {
    return this.sro?.status === 'SUPPLIER_RECEIVED' || this.sro?.status === 'ESCALATED';
  }
  get isSlaOverdue(): boolean {
    if (!this.sro?.slaDeadline) return false;
    return new Date(this.sro.slaDeadline) < new Date();
  }

  // ── Audit Trail ───────────────────────────────────────────────────────────

  loadAuditTrail() {
    if (!this.sro) return;
    this.isLoadingAudit = true;
    this.reportsService.getEntityAuditTrail(this.sro.uuid, 'WAREHOUSE', 'SRO').subscribe({
      next: (res) => {
        this.isLoadingAudit = false;
        this.auditTrail = res.success && res.result?.data ? res.result.data : [];
        this.showAuditTrail = true;
      },
      error: () => { this.isLoadingAudit = false; }
    });
  }

  getAuditActionSeverity(action: string): 'success' | 'danger' | 'warn' | 'info' | 'secondary' {
    const m: Record<string, any> = {
      CREATE: 'info', APPROVE: 'success', REJECT: 'danger',
      DISPATCH: 'info', CONFIRM_RECEIPT: 'success',
      RESOLVE: 'success', ESCALATE: 'danger', EXPECT_REPLACEMENT: 'warn'
    };
    return m[action] ?? 'secondary';
  }
}
