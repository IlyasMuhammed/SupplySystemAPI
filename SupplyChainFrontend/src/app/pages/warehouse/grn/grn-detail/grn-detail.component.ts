import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule, ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TableModule } from 'primeng/table';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { ToggleButtonModule } from 'primeng/togglebutton';
import { TextareaModule } from 'primeng/textarea';
import { TooltipModule } from 'primeng/tooltip';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { MessageService, ConfirmationService } from 'primeng/api';
import {
  WarehouseService, GrnDetailModel, GrnLineModel, UpdateGrnLineRequest, InspectGrnLineRequest
} from '../../../../services/warehouse.service';
import { InventoryService, WarehouseModel } from '../../../../services/inventory.service';
import { ReportsService, AuditLogItemModel } from '../../../../services/reports.service';
import { ProgressSpinnerModule } from 'primeng/progressspinner';

export interface InspectionRowState {
  lineUuid: string;
  inspectionResult: string;
  qtyAccepted: number;
  qtyRejected: number;
  rejectionReason: string | null;
  inspectorRemarks: string;
  isSaving: boolean;
  isDirty: boolean;
}

@Component({
  selector: 'app-grn-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, ReactiveFormsModule,
    ButtonModule, TagModule, ToastModule, TableModule, DialogModule,
    InputTextModule, InputNumberModule, ToggleButtonModule,
    TextareaModule, TooltipModule, DropdownModule, CalendarModule, ConfirmDialogModule, ProgressSpinnerModule
  ],
  templateUrl: './grn-detail.component.html',
  styleUrls: ['./grn-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class GrnDetailComponent implements OnInit {
  uuid = '';
  grn: GrnDetailModel | null = null;
  isLoading = true;
  warehouses: WarehouseModel[] = [];

  // ── Edit line dialog ──────────────────────────────────────────────────────
  showLineDialog  = false;
  editingLine: GrnLineModel | null = null;
  lineForm!: FormGroup;
  isSavingLine = false;

  qcResultOptions = [
    { label: 'Pass',    value: 'PASS' },
    { label: 'Fail',    value: 'FAIL' },
    { label: 'Partial', value: 'PARTIAL' }
  ];

  inspectionResultOptions = [
    { label: 'Pass',         value: 'Pass' },
    { label: 'Fail',         value: 'Fail' },
    { label: 'Partial Pass', value: 'PartialPass' }
  ];

  rejectionReasonOptions = [
    { label: 'Damaged',      value: 'Damaged' },
    { label: 'Wrong item',   value: 'Wrong item' },
    { label: 'Short expiry', value: 'Short expiry' },
    { label: 'Over qty',     value: 'Over qty' }
  ];

  // ── Inspection grid state ─────────────────────────────────────────────────
  inspectionRows: InspectionRowState[] = [];

  // ── QC Confirm dialog ─────────────────────────────────────────────────────
  showQcConfirmDialog = false;
  qcConfirmNotes      = '';
  isQcConfirming      = false;

  // ── QC Reject dialog ──────────────────────────────────────────────────────
  showQcRejectDialog = false;
  qcRejectReason     = '';
  isQcRejecting      = false;

  // ── Finance Approve dialog ────────────────────────────────────────────────
  showFinanceApproveDialog = false;
  isFinanceApproving       = false;

  // ── Finance Reject dialog ─────────────────────────────────────────────────
  showFinanceRejectDialog = false;
  financeRejectReason     = '';
  isFinanceRejecting      = false;

  // ── IM Approve dialog ─────────────────────────────────────────────────────
  showApproveDialog = false;
  approveRemarks    = '';
  isApproving       = false;

  // ── IM Reject dialog ──────────────────────────────────────────────────────
  showRejectDialog = false;
  rejectReason     = '';
  isRejecting      = false;

  // ── Submit ────────────────────────────────────────────────────────────────
  isSubmitting = false;

  isDeleting = false;

  // ── Audit Trail ───────────────────────────────────────────────────────────
  auditTrail: AuditLogItemModel[] = [];
  isLoadingAudit = false;
  showAuditTrail = false;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private warehouseService: WarehouseService,
    private inventoryService: InventoryService,
    private reportsService: ReportsService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    private fb: FormBuilder
  ) {}

  ngOnInit() {
    this.inventoryService.getWarehouses().subscribe({
      next: res => { if (res.success) this.warehouses = res.result; }
    });
    this.route.params.subscribe(p => { this.uuid = p['uuid']; this.load(); });
    this.lineForm = this.fb.group({
      qtyReceived:     [0, [Validators.required, Validators.min(0)]],
      qtyAccepted:     [0, [Validators.required, Validators.min(0)]],
      qtyRejected:     [0, [Validators.min(0)]],
      rejectionReason: [null],
      batchNumber:     [''],
      expiryDate:      [null],
      unitCost:        [null],
      qcResult:        ['PASS']
    });
  }

  load() {
    this.isLoading = true;
    this.warehouseService.getGrnById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.grn = res.success ? res.result : null;
        if (this.grn) this.initInspectionRows();
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load GRN.' });
      }
    });
  }

  getWarehouseName(uuid?: string | null): string {
    if (!uuid) return '—';
    const w = this.warehouses.find(wh => wh.uuid === uuid);
    return w ? `${w.name} (${w.code})` : uuid;
  }

  private initInspectionRows() {
    this.inspectionRows = (this.grn?.lines ?? []).map(l => ({
      lineUuid:         l.uuid,
      inspectionResult: l.inspectionResult ?? '',
      qtyAccepted:      l.qtyAccepted,
      qtyRejected:      l.qtyRejected,
      rejectionReason:  l.rejectionReason ?? null,
      inspectorRemarks: l.inspectorRemarks ?? '',
      isSaving:         false,
      isDirty:          false
    }));
  }

  // ── Edit line ─────────────────────────────────────────────────────────────

  openLineDialog(line: GrnLineModel) {
    this.editingLine = line;
    this.lineForm.patchValue({
      qtyReceived:     line.qtyReceived,
      qtyAccepted:     line.qtyAccepted,
      qtyRejected:     line.qtyRejected,
      rejectionReason: line.rejectionReason ?? null,
      batchNumber:     line.batchNumber     || '',
      expiryDate:      line.expiryDate ? new Date(line.expiryDate) : null,
      unitCost:        line.unitCost        ?? null,
      qcResult:        line.qcResult        || 'PASS'
    });
    this.showLineDialog = true;
  }

  saveLineChanges() {
    if (this.lineForm.invalid || !this.editingLine || !this.grn) return;
    this.isSavingLine = true;
    const v = this.lineForm.value;
    const req: UpdateGrnLineRequest = {
      qtyReceived:     v.qtyReceived,
      qtyAccepted:     v.qtyAccepted,
      qtyRejected:     v.qtyRejected,
      rejectionReason: v.rejectionReason || undefined,
      batchNumber:     v.batchNumber     || undefined,
      expiryDate:      v.expiryDate instanceof Date ? v.expiryDate.toISOString() : v.expiryDate || undefined,
      unitCost:        v.unitCost        ?? undefined,
      qcResult:        v.qcResult        || undefined
    };
    this.warehouseService.updateGrnLine(this.grn.uuid, this.editingLine.uuid, req).subscribe({
      next: (res) => {
        this.isSavingLine = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Line updated.' });
          this.showLineDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isSavingLine = false;
        this.messageService.add({ severity: 'error', summary: 'Error',
          detail: err?.error?.message || 'Failed to update line.' });
      }
    });
  }

  // ── Submit (DRAFT → PENDING_QC) ───────────────────────────────────────────

  submitGrn() {
    if (!this.grn) return;
    this.isSubmitting = true;
    this.warehouseService.submitGrn(this.grn.uuid).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Submitted', detail: 'GRN submitted for QC.' });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isSubmitting = false;
        this.messageService.add({ severity: 'error', summary: 'Error',
          detail: err?.error?.message || 'Failed to submit GRN.' });
      }
    });
  }

  // ── QC Confirm (PENDING_QC → PENDING_FINANCE | PENDING_APPROVAL) ──────────

  openQcConfirmDialog() { this.qcConfirmNotes = ''; this.showQcConfirmDialog = true; }

  confirmQc() {
    if (!this.grn) return;
    this.isQcConfirming = true;
    this.warehouseService.qcConfirm(this.grn.uuid, { qcNotes: this.qcConfirmNotes || undefined }).subscribe({
      next: (res) => {
        this.isQcConfirming = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'QC Confirmed', detail: 'GRN moved to approval queue.' });
          this.showQcConfirmDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isQcConfirming = false;
        this.messageService.add({ severity: 'error', summary: 'Error',
          detail: err?.error?.message || 'QC confirmation failed.' });
      }
    });
  }

  // ── QC Reject (PENDING_QC → REJECTED) ────────────────────────────────────

  openQcRejectDialog() { this.qcRejectReason = ''; this.showQcRejectDialog = true; }

  confirmQcReject() {
    if (!this.grn || !this.qcRejectReason.trim()) return;
    this.isQcRejecting = true;
    this.warehouseService.qcReject(this.grn.uuid, { reason: this.qcRejectReason }).subscribe({
      next: (res) => {
        this.isQcRejecting = false;
        if (res.success) {
          this.messageService.add({ severity: 'warn', summary: 'QC Rejected', detail: 'GRN has been rejected.' });
          this.showQcRejectDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isQcRejecting = false;
        this.messageService.add({ severity: 'error', summary: 'Error',
          detail: err?.error?.message || 'QC rejection failed.' });
      }
    });
  }

  // ── Finance Approve (PENDING_FINANCE → PENDING_APPROVAL) ─────────────────

  openFinanceApproveDialog() { this.showFinanceApproveDialog = true; }

  confirmFinanceApprove() {
    if (!this.grn) return;
    this.isFinanceApproving = true;
    this.warehouseService.financeApprove(this.grn.uuid).subscribe({
      next: (res) => {
        this.isFinanceApproving = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Finance Approved', detail: 'GRN sent to Inventory Manager.' });
          this.showFinanceApproveDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isFinanceApproving = false;
        this.messageService.add({ severity: 'error', summary: 'Error',
          detail: err?.error?.message || 'Finance approval failed.' });
      }
    });
  }

  // ── Finance Reject (PENDING_FINANCE → REJECTED) ───────────────────────────

  openFinanceRejectDialog() { this.financeRejectReason = ''; this.showFinanceRejectDialog = true; }

  confirmFinanceReject() {
    if (!this.grn || !this.financeRejectReason.trim()) return;
    this.isFinanceRejecting = true;
    this.warehouseService.financeReject(this.grn.uuid, { reason: this.financeRejectReason }).subscribe({
      next: (res) => {
        this.isFinanceRejecting = false;
        if (res.success) {
          this.messageService.add({ severity: 'warn', summary: 'Finance Rejected', detail: 'GRN has been rejected by Finance.' });
          this.showFinanceRejectDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isFinanceRejecting = false;
        this.messageService.add({ severity: 'error', summary: 'Error',
          detail: err?.error?.message || 'Finance rejection failed.' });
      }
    });
  }

  // ── IM Approve (PENDING_APPROVAL → APPROVED) ──────────────────────────────

  openApproveDialog() { this.approveRemarks = ''; this.showApproveDialog = true; }

  confirmApprove() {
    if (!this.grn) return;
    this.isApproving = true;
    this.warehouseService.approveGrn(this.grn.uuid, this.approveRemarks || undefined).subscribe({
      next: (res) => {
        this.isApproving = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Approved', detail: 'GRN approved and stock updated.' });
          this.showApproveDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isApproving = false;
        this.messageService.add({ severity: 'error', summary: 'Error',
          detail: err?.error?.message || 'Approval failed.' });
      }
    });
  }

  // ── IM Reject (PENDING_APPROVAL → REJECTED + return order) ───────────────

  openRejectDialog() { this.rejectReason = ''; this.showRejectDialog = true; }

  confirmReject() {
    if (!this.grn || !this.rejectReason.trim()) return;
    this.isRejecting = true;
    this.warehouseService.rejectGrn(this.grn.uuid, { reason: this.rejectReason }).subscribe({
      next: (res) => {
        this.isRejecting = false;
        if (res.success) {
          this.messageService.add({ severity: 'warn', summary: 'Rejected', detail: 'GRN rejected. Supplier return order created.' });
          this.showRejectDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isRejecting = false;
        this.messageService.add({ severity: 'error', summary: 'Error',
          detail: err?.error?.message || 'Rejection failed.' });
      }
    });
  }

  // ── Delete (DRAFT only) ───────────────────────────────────────────────────

  confirmDelete() {
    if (!this.grn) return;
    this.confirmationService.confirm({
      message: `Delete GRN <strong>${this.grn.grnNumber}</strong>? This cannot be undone.`,
      header: 'Delete GRN',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      accept: () => this.deleteGrn()
    });
  }

  deleteGrn() {
    if (!this.grn) return;
    this.isDeleting = true;
    this.warehouseService.deleteGrn(this.grn.uuid).subscribe({
      next: () => {
        this.isDeleting = false;
        this.messageService.add({ severity: 'success', summary: 'Deleted', detail: 'GRN deleted.' });
        setTimeout(() => this.router.navigate(['/portal/pages/warehouse/grn']), 1200);
      },
      error: (err) => {
        this.isDeleting = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to delete GRN.' });
      }
    });
  }

  // ── Inspection grid ───────────────────────────────────────────────────────

  onInspectionResultChange(row: InspectionRowState) {
    const line = this.grn?.lines.find(l => l.uuid === row.lineUuid);
    if (!line) return;
    switch (row.inspectionResult) {
      case 'Pass':
        row.qtyAccepted = line.qtyReceived;
        row.qtyRejected = 0;
        break;
      case 'Fail':
        row.qtyAccepted = 0;
        row.qtyRejected = line.qtyReceived;
        break;
      case 'PartialPass':
        // Leave as-is for manual entry
        break;
    }
    row.isDirty = true;
  }

  onInspectionQtyChange(row: InspectionRowState) {
    row.isDirty = true;
    if (!row.qtyRejected) row.rejectionReason = null;
  }

  saveInspection(row: InspectionRowState) {
    if (!this.grn || !row.inspectionResult) return;
    const line = this.grn.lines.find(l => l.uuid === row.lineUuid);
    if (!line) return;

    // Client-side PartialPass validation
    if (row.inspectionResult === 'PartialPass') {
      const sum = (row.qtyAccepted || 0) + (row.qtyRejected || 0);
      if (Math.abs(sum - line.qtyReceived) > 0.0001) {
        this.messageService.add({
          severity: 'warn', summary: 'Validation',
          detail: `Partial Pass: Qty Accepted (${row.qtyAccepted}) + Qty Rejected (${row.qtyRejected}) must equal Qty Received (${line.qtyReceived}).`
        });
        return;
      }
    }
    if ((row.qtyRejected || 0) > 0 && !row.rejectionReason) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Rejection Reason is required when any quantity is rejected.' });
      return;
    }

    row.isSaving = true;
    const req: InspectGrnLineRequest = {
      inspectionResult: row.inspectionResult,
      qtyAccepted:      row.qtyAccepted      || 0,
      qtyRejected:      row.qtyRejected      || 0,
      rejectionReason:  row.rejectionReason  || undefined,
      inspectorRemarks: row.inspectorRemarks || undefined
    };
    this.warehouseService.inspectGrnLine(this.grn.uuid, row.lineUuid, req).subscribe({
      next: () => {
        row.isSaving = false;
        row.isDirty  = false;
        this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Inspection recorded.' });
        this.load();
      },
      error: (err) => {
        row.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to save inspection.' });
      }
    });
  }

  getInspectionResultSeverity(r: string | undefined): 'success' | 'danger' | 'warn' | 'secondary' {
    switch (r) {
      case 'Pass':        return 'success';
      case 'Fail':        return 'danger';
      case 'PartialPass': return 'warn';
      default:            return 'secondary';
    }
  }

  getInspectionResultLabel(r: string | undefined): string {
    switch (r) {
      case 'Pass':        return 'Pass';
      case 'Fail':        return 'Fail';
      case 'PartialPass': return 'Partial Pass';
      default:            return '—';
    }
  }

  get canInspect(): boolean {
    const s = this.grn?.status;
    return (s === 'PENDING_QC' || s === 'PENDING_APPROVAL') && (this.grn?.requiresInspection ?? false);
  }

  // ── Status helpers ────────────────────────────────────────────────────────

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'APPROVED':          return 'success';
      case 'PENDING_QC':        return 'info';
      case 'PENDING_FINANCE':   return 'warn';
      case 'PENDING_APPROVAL':  return 'info';
      case 'REJECTED':          return 'danger';
      case 'DRAFT':             return 'secondary';
      default:                  return 'secondary';
    }
  }

  getStatusLabel(s: string): string {
    const map: Record<string, string> = {
      DRAFT:            'Draft',
      PENDING_QC:       'Pending QC',
      PENDING_FINANCE:  'Pending Finance',
      PENDING_APPROVAL: 'Pending Approval',
      APPROVED:         'Approved',
      REJECTED:         'Rejected'
    };
    return map[s] ?? s;
  }

  get canEdit():          boolean { return this.grn?.status === 'DRAFT'; }
  get canSubmit():        boolean { return this.grn?.status === 'DRAFT'; }
  get canQcConfirm():     boolean { return this.grn?.status === 'PENDING_QC'; }
  get canQcReject():      boolean { return this.grn?.status === 'PENDING_QC'; }
  get canFinanceApprove():boolean { return this.grn?.status === 'PENDING_FINANCE'; }
  get canFinanceReject(): boolean { return this.grn?.status === 'PENDING_FINANCE'; }
  get canApprove():       boolean { return this.grn?.status === 'PENDING_APPROVAL'; }
  get canReject():        boolean { return this.grn?.status === 'PENDING_APPROVAL'; }

  get lf() { return this.lineForm.controls; }

  loadAuditTrail() {
    if (!this.grn) return;
    this.isLoadingAudit = true;
    this.reportsService.getEntityAuditTrail(this.grn.uuid, 'WAREHOUSE', 'GRN').subscribe({
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
      CREATE: 'info', UPDATE: 'secondary', DELETE: 'danger',
      SUBMIT: 'warn', INSPECT: 'info'
    };
    return m[action] ?? 'secondary';
  }
}
