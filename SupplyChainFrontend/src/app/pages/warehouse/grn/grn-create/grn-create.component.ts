import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router } from '@angular/router';
import { ReactiveFormsModule, FormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { CalendarModule } from 'primeng/calendar';
import { TextareaModule } from 'primeng/textarea';
import { DropdownModule } from 'primeng/dropdown';
import { AutoCompleteModule } from 'primeng/autocomplete';
import { RadioButtonModule } from 'primeng/radiobutton';
import { ToastModule } from 'primeng/toast';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { WarehouseService, GrnLineReceiveInput } from '../../../../services/warehouse.service';
import { DemandService, PoSearchItemModel, PoLineModel } from '../../../../services/demand.service';
import { InventoryService, WarehouseModel } from '../../../../services/inventory.service';

export interface LineInput {
  poLineUuid: string;
  itemDescription: string;
  specification?: string;
  unitOfMeasure?: string;
  qtyOrdered: number;
  qtyReceived: number;
  qtyAccepted: number;
  qtyRejected: number;
  rejectionReason?: string | null;
  batchNumber?: string;
  expiryDate?: Date | null;
}

const RECEIVABLE_STATUSES = new Set(['SENT', 'PARTIALLY_RECEIVED']);

@Component({
  selector: 'app-grn-create',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule, FormsModule,
    ButtonModule, InputTextModule, InputNumberModule, CalendarModule,
    TextareaModule, DropdownModule, AutoCompleteModule, RadioButtonModule,
    ToastModule, TagModule, TooltipModule
  ],
  templateUrl: './grn-create.component.html',
  styleUrls: ['./grn-create.component.scss'],
  providers: [MessageService]
})
export class GrnCreateComponent implements OnInit {
  form!: FormGroup;
  isSaving = false;

  // Warehouse dropdown
  warehouseOptions: { label: string; value: string }[] = [];

  // PO autocomplete
  poAuto: any = null;
  poSuggestions: PoSearchItemModel[] = [];
  selectedPo: PoSearchItemModel | null = null;
  poTouched = false;
  poWarning: string | null = null;

  // Inspection flag — per-GRN, set at create time, locks once submitted
  requiresInspection = true;

  // Line quantities — loaded after PO is selected
  lineInputs: LineInput[] = [];
  loadingLines = false;

  rejectionReasonOptions = [
    { label: 'Damaged',      value: 'Damaged' },
    { label: 'Wrong item',   value: 'Wrong item' },
    { label: 'Short expiry', value: 'Short expiry' },
    { label: 'Over qty',     value: 'Over qty' }
  ];

  constructor(
    private fb: FormBuilder,
    private warehouseService: WarehouseService,
    private demandService: DemandService,
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private router: Router
  ) {}

  ngOnInit() {
    this.form = this.fb.group({
      warehouseUuid:  ['', Validators.required],
      receivedAt:     [new Date(), Validators.required],
      deliveryNoteNo: [''],
      vehicleNo:      [''],
      driverName:     [''],
      invoiceNo:      [''],
      notes:          ['']
    });
    this.inventoryService.getWarehouses().subscribe({
      next: (res) => {
        const active = (res.result ?? []).filter((w: WarehouseModel) => w.isActive);
        this.warehouseOptions = active.map((w: WarehouseModel) => ({
          label: `${w.name} (${w.code})`,
          value: w.uuid
        }));
      },
      error: () => {}
    });
  }

  // ── PO search ──────────────────────────────────────────────────────────────

  onPoFocus() {
    if (this.selectedPo) return;
    // Pre-populate with receivable POs on focus when nothing is selected yet
    this.demandService.searchPosForGrn(undefined, true).subscribe({
      next: (res) => { this.poSuggestions = res.result ?? []; },
      error:  ()  => { this.poSuggestions = []; }
    });
  }

  searchPos(event: any) {
    const q = (event.query as string ?? '').trim();
    if (q.length === 0) {
      // Empty query — show only receivable POs, most recent first
      this.demandService.searchPosForGrn(undefined, true).subscribe({
        next: (res) => { this.poSuggestions = res.result ?? []; },
        error:  ()  => { this.poSuggestions = []; }
      });
    } else {
      // Has query — unrestricted search across all statuses
      this.demandService.searchPosForGrn(q, false).subscribe({
        next: (res) => { this.poSuggestions = res.result ?? []; },
        error:  ()  => { this.poSuggestions = []; }
      });
    }
  }

  onPoSelect(event: any) {
    const po: PoSearchItemModel = event.value ?? event;
    if (po && typeof po === 'object') {
      this.selectedPo = po;
      this.poWarning = RECEIVABLE_STATUSES.has(po.status)
        ? null
        : `This PO is in ${po.status} status and cannot currently receive a GRN.`;
      this.loadPoLines(po.uuid);
    }
  }

  onPoClear() {
    this.selectedPo = null;
    this.poAuto     = null;
    this.poWarning  = null;
    this.lineInputs = [];
  }

  isReceivable(status: string): boolean {
    return RECEIVABLE_STATUSES.has(status);
  }

  // ── Lines ──────────────────────────────────────────────────────────────────

  removeLine(index: number) {
    this.lineInputs.splice(index, 1);
  }

  private loadPoLines(poUuid: string) {
    this.loadingLines = true;
    this.lineInputs   = [];
    this.demandService.getPoById(poUuid).subscribe({
      next: (res) => {
        this.loadingLines = false;
        const lines: PoLineModel[] = res?.result?.lines ?? [];
        const pending = lines.filter(l => l.qtyPending > 0);
        this.lineInputs = pending.map(l => ({
          poLineUuid:      l.uuid,
          itemDescription: l.itemDescription,
          specification:   l.specification,
          unitOfMeasure:   l.unitOfMeasure,
          qtyOrdered:      l.qtyPending,
          qtyReceived:     l.qtyPending,
          qtyAccepted:     l.qtyPending,
          qtyRejected:     0,
          rejectionReason: null,
          batchNumber:     '',
          expiryDate:      null
        }));
        if (pending.length === 0)
          this.messageService.add({ severity: 'info', summary: 'Fully Received', detail: 'All PO lines are already fully received.' });
      },
      error: () => {
        this.loadingLines = false;
        this.messageService.add({ severity: 'warn', summary: 'Warning', detail: 'Could not load PO lines. You can still create the GRN.' });
      }
    });
  }

  onQtyReceivedChange(line: LineInput) {
    line.qtyAccepted = Math.max(0, line.qtyReceived - (line.qtyRejected || 0));
  }

  onQtyRejectedChange(line: LineInput) {
    line.qtyAccepted = Math.max(0, line.qtyReceived - (line.qtyRejected || 0));
    if (!(line.qtyRejected > 0)) line.rejectionReason = null;
  }

  onRequiresInspectionChange() {
    if (!this.requiresInspection) {
      // No-inspection path: auto-set accepted = received, rejected = 0
      this.lineInputs.forEach(l => {
        l.qtyAccepted     = l.qtyReceived;
        l.qtyRejected     = 0;
        l.rejectionReason = null;
      });
    }
  }

  // ── Computed ───────────────────────────────────────────────────────────────

  get poInvalid(): boolean {
    return this.poTouched && !this.selectedPo;
  }

  get poBlockedByStatus(): boolean {
    return !!this.selectedPo && !RECEIVABLE_STATUSES.has(this.selectedPo.status);
  }

  get totalReceived(): number {
    return this.lineInputs.reduce((s, l) => s + (l.qtyReceived || 0), 0);
  }

  // ── Submit ─────────────────────────────────────────────────────────────────

  submit() {
    this.poTouched = true;
    if (!this.selectedPo) return;
    if (this.poBlockedByStatus) {
      this.messageService.add({ severity: 'warn', summary: 'Cannot Create GRN', detail: this.poWarning! });
      return;
    }
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    this.isSaving = true;
    const v = this.form.value;

    const lines: GrnLineReceiveInput[] = this.lineInputs.map(l => ({
      poLineUuid:      l.poLineUuid,
      qtyReceived:     l.qtyReceived  || 0,
      qtyAccepted:     l.qtyAccepted  || 0,
      qtyRejected:     l.qtyRejected  || 0,
      rejectionReason: l.rejectionReason || undefined,
      batchNumber:     l.batchNumber     || undefined,
      expiryDate:      l.expiryDate instanceof Date ? l.expiryDate.toISOString() : l.expiryDate || undefined
    }));

    this.warehouseService.createGrn({
      poUuid:             this.selectedPo.uuid,
      warehouseUuid:      v.warehouseUuid  || undefined,
      receivedAt:         v.receivedAt instanceof Date ? v.receivedAt.toISOString() : v.receivedAt,
      deliveryNoteNo:     v.deliveryNoteNo || undefined,
      vehicleNo:          v.vehicleNo      || undefined,
      driverName:         v.driverName     || undefined,
      invoiceNo:          v.invoiceNo      || undefined,
      notes:              v.notes          || undefined,
      requiresInspection: this.requiresInspection,
      lines:              lines.length > 0 ? lines : undefined
    }).subscribe({
      next: (res) => {
        this.isSaving = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'GRN Created', detail: 'Goods receipt note created successfully.' });
          setTimeout(() => this.router.navigate(['/portal/pages/warehouse/grn', res.result]), 1200);
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to create GRN.' });
      }
    });
  }

  get f() { return this.form.controls; }

  getStatusSeverity(status: string): 'success' | 'warn' | 'info' | 'secondary' | 'danger' {
    switch (status) {
      case 'SENT':               return 'info';
      case 'PARTIALLY_RECEIVED': return 'warn';
      case 'RECEIVED':           return 'success';
      default:                   return 'secondary';
    }
  }
}
