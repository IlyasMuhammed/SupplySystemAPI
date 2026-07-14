import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, ActivatedRoute, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { TableModule } from 'primeng/table';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ProgressSpinnerModule } from 'primeng/progressspinner';
import { TooltipModule } from 'primeng/tooltip';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { MessageService, ConfirmationService } from 'primeng/api';
import { forkJoin } from 'rxjs';
import {
  MaterialService, MirDetail, MirLine, MirLineApprovalInput,
  MirLineAvailability, MirStockAvailabilityResponse, MivListItem,
  PatchMirRequest
} from '../../../../services/material.service';
import { InventoryService, ProductStockModel } from '../../../../services/inventory.service';

interface EditMirLine {
  productUuid:         string;
  requestedQty:        number;
  purpose:             string;
  notes:               string;
  stockItems:          ProductStockModel[];
  selectedWarehouseId: number | null;
  maxQty:              number | null;
  isLoadingStock:      boolean;
}

interface LineApprovalRow {
  line: MirLine;
  approvedQty: number;
  ceiling: number;
  availability?: MirLineAvailability;
  stockError?: string;
}

@Component({
  selector: 'app-mir-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, ToastModule, DialogModule,
    InputTextModule, InputNumberModule, TextareaModule, TableModule,
    ConfirmDialogModule, ProgressSpinnerModule, TooltipModule,
    DropdownModule, CalendarModule
  ],
  templateUrl: './mir-detail.component.html',
  styleUrls: ['./mir-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class MirDetailComponent implements OnInit {
  uuid!: string;
  mir: MirDetail | null = null;
  isLoading  = true;
  actionBusy = false;

  showRejectDialog  = false;
  showApproveDialog = false;
  rejectReason      = '';
  approveRemarks    = '';
  lineApprovalRows: LineApprovalRow[] = [];
  availabilityLoading = false;
  availabilityData: MirStockAvailabilityResponse | null = null;

  mivs: MivListItem[] = [];
  mivsLoading = false;

  // ── Edit Dialog ────────────────────────────────────────────────────────────
  showEditDialog = false;
  isEditing      = false;
  editProductOptions: { label: string; value: string }[] = [];
  editProjectOptions: { label: string; value: string }[] = [];
  isLoadingEditData = false;
  private _editProductIdByUuid = new Map<string, number>();

  editData = {
    requestType:    '',
    projectUuid:    '',
    department:     '',
    maintenanceRef: '',
    requiredDate:   null as Date | null,
    priority:       '',
    purpose:        '',
    notes:          ''
  };

  editLines: EditMirLine[] = [];

  priorityOptions = [
    { label: 'Low',    value: 'LOW' },
    { label: 'Medium', value: 'MEDIUM' },
    { label: 'High',   value: 'HIGH' },
    { label: 'Urgent', value: 'URGENT' }
  ];

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private materialService: MaterialService,
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid')!;
    this.load();
  }

  load() {
    this.isLoading = true;
    this.materialService.getMir(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.mir       = res.success ? res.result ?? null : null;
        this.loadMivs();
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load MIR.' });
      }
    });
  }

  loadMivs() {
    this.mivsLoading = true;
    this.materialService.getMivs({ mirUuid: this.uuid, pageSize: 50 }).subscribe({
      next: (res) => {
        this.mivsLoading = false;
        this.mivs = res.success && res.result ? res.result.data : [];
      },
      error: () => { this.mivsLoading = false; }
    });
  }

  submit() {
    this.actionBusy = true;
    this.materialService.submitMir(this.uuid).subscribe({
      next: () => {
        this.actionBusy = false;
        this.messageService.add({ severity: 'success', summary: 'Submitted', detail: 'MIR submitted for approval.' });
        this.load();
      },
      error: (err: any) => {
        this.actionBusy = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Submit failed.' });
      }
    });
  }

  openApprove() {
    this.approveRemarks    = '';
    this.availabilityData  = null;
    this.lineApprovalRows  = (this.mir?.lines ?? []).map(l => ({
      line:        l,
      approvedQty: l.latestApprovedQty !== undefined && l.latestApprovedQty !== null
        ? l.latestApprovedQty : l.requestedQty,
      ceiling:     l.latestApprovedQty !== undefined && l.latestApprovedQty !== null
        ? l.latestApprovedQty : l.requestedQty
    }));
    this.showApproveDialog = true;
    this.loadAvailability();
  }

  private loadAvailability() {
    this.availabilityLoading = true;
    this.materialService.getMirStockAvailability(this.uuid).subscribe({
      next: (res) => {
        this.availabilityLoading = false;
        this.availabilityData    = res.success ? res.result ?? null : null;
        if (this.availabilityData) {
          const availMap = new Map(this.availabilityData.lines.map(a => [a.lineUuid, a]));
          this.lineApprovalRows = this.lineApprovalRows.map(r => ({
            ...r,
            availability: availMap.get(r.line.uuid)
          }));
        }
      },
      error: () => {
        this.availabilityLoading = false;
      }
    });
  }

  onApprovedQtyChange(row: LineApprovalRow) {
    row.stockError = undefined;
    if (row.availability && row.approvedQty > row.availability.qtyAvailable) {
      row.stockError =
        `Exceeds available stock ${row.availability.qtyAvailable.toFixed(4)} ` +
        `(on-hand: ${row.availability.qtyOnHand.toFixed(4)}, ` +
        `reserved: ${row.availability.qtyReserved.toFixed(4)})`;
    }
  }

  get hasStockErrors(): boolean {
    return this.lineApprovalRows.some(r => !!r.stockError);
  }

  confirmApprove() {
    if (!this.mir?.activeApprovalUuid) {
      this.messageService.add({ severity: 'error', summary: 'Error', detail: 'No active approval found.' });
      return;
    }
    if (this.hasStockErrors) {
      this.messageService.add({ severity: 'error', summary: 'Stock Error', detail: 'One or more lines exceed available stock. Approval blocked.' });
      return;
    }
    this.showApproveDialog = false;
    this.actionBusy = true;
    const lineApprovals: MirLineApprovalInput[] = this.lineApprovalRows.map(r => ({
      lineUuid:    r.line.uuid,
      approvedQty: r.approvedQty
    }));
    this.materialService.workflowApproveMir(this.uuid, {
      approvalUUID:  this.mir.activeApprovalUuid,
      remarks:       this.approveRemarks || undefined,
      lineApprovals
    }).subscribe({
      next: () => {
        this.actionBusy = false;
        this.messageService.add({ severity: 'success', summary: 'Approved', detail: 'Approval step recorded.' });
        this.load();
      },
      error: (err: any) => {
        this.actionBusy = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Approve failed.' });
      }
    });
  }

  openReject() { this.rejectReason = ''; this.showRejectDialog = true; }

  confirmReject() {
    if (!this.rejectReason.trim()) {
      this.messageService.add({ severity: 'warn', summary: 'Required', detail: 'Rejection reason is required.' });
      return;
    }
    if (!this.mir?.activeApprovalUuid) {
      this.messageService.add({ severity: 'error', summary: 'Error', detail: 'No active approval found.' });
      return;
    }
    this.showRejectDialog = false;
    this.actionBusy = true;
    this.materialService.workflowRejectMir(this.uuid, {
      approvalUUID: this.mir.activeApprovalUuid,
      reason:       this.rejectReason
    }).subscribe({
      next: () => {
        this.actionBusy = false;
        this.messageService.add({ severity: 'info', summary: 'Rejected', detail: 'MIR rejected.' });
        this.load();
      },
      error: (err: any) => {
        this.actionBusy = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Reject failed.' });
      }
    });
  }

  openEdit() {
    if (!this.mir) return;
    this.editData = {
      requestType:    this.mir.requestType,
      projectUuid:    this.mir.projectUuid    ?? '',
      department:     this.mir.department     ?? '',
      maintenanceRef: this.mir.maintenanceRef ?? '',
      requiredDate:   this.mir.requiredDate   ? new Date(this.mir.requiredDate) : null,
      priority:       this.mir.priority,
      purpose:        this.mir.purpose        ?? '',
      notes:          this.mir.notes          ?? ''
    };
    this.editLines = this.mir.lines.map(l => ({
      productUuid:         l.productUuid,
      requestedQty:        l.requestedQty,
      purpose:             l.purpose ?? '',
      notes:               l.notes   ?? '',
      stockItems:          [],
      selectedWarehouseId: l.warehouseId ?? null,
      maxQty:              null,
      isLoadingStock:      false
    }));

    if (this.editProductOptions.length === 0 || this.editProjectOptions.length === 0) {
      this.isLoadingEditData = true;
      forkJoin({
        products: this.inventoryService.getProducts({ activeOnly: true, pageSize: 500 }),
        projects: this.materialService.getProjects({ status: 'ACTIVE', pageSize: 200 })
      }).subscribe({
        next: ({ products, projects }) => {
          this.isLoadingEditData = false;
          if (products.success && products.result) {
            this.editProductOptions = products.result.data.map(p => ({
              label: `${p.sku} — ${p.name}`,
              value: p.uuid
            }));
            products.result.data.forEach(p => this._editProductIdByUuid.set(p.uuid, p.id));
          }
          if (projects.success && projects.result) {
            this.editProjectOptions = (projects.result.data ?? []).map(p => ({
              label: `${p.projectCode} — ${p.projectName}`,
              value: p.uuid
            }));
          }
          this.editLines.forEach((_, i) => {
            if (this.editLines[i].productUuid) this.onEditProductChange(i, this.editLines[i].productUuid, false);
          });
        },
        error: () => { this.isLoadingEditData = false; }
      });
    } else {
      this.editLines.forEach((_, i) => {
        if (this.editLines[i].productUuid) this.onEditProductChange(i, this.editLines[i].productUuid, false);
      });
    }

    this.showEditDialog = true;
  }

  addEditLine() {
    this.editLines.push({
      productUuid: '', requestedQty: 1, purpose: '', notes: '',
      stockItems: [], selectedWarehouseId: null, maxQty: null, isLoadingStock: false
    });
  }

  onEditProductChange(i: number, uuid: string, resetWarehouse = true) {
    const line = this.editLines[i];
    line.stockItems = [];
    line.maxQty     = null;
    if (resetWarehouse) line.selectedWarehouseId = null;

    const productId = this._editProductIdByUuid.get(uuid);
    if (!productId) return;

    line.isLoadingStock = true;
    this.inventoryService.getProductStock(productId).subscribe({
      next: (res) => {
        line.isLoadingStock = false;
        if (res.success && res.result) {
          line.stockItems = res.result.filter(s => s.qtyAvailable > 0);
          if (line.selectedWarehouseId) {
            this.onEditWarehouseChange(i, line.selectedWarehouseId);
          }
        }
      },
      error: () => { line.isLoadingStock = false; }
    });
  }

  onEditWarehouseChange(i: number, warehouseId: number) {
    const line  = this.editLines[i];
    const stock = line.stockItems.find(s => s.warehouseId === warehouseId);
    line.maxQty = stock ? stock.qtyAvailable : null;
    if (line.maxQty != null && line.requestedQty > line.maxQty) {
      line.requestedQty = line.maxQty;
    }
  }

  getEditWarehouseOptions(i: number): { label: string; value: number }[] {
    return this.editLines[i].stockItems.map(s => ({
      label: `${s.warehouseName}  (Available: ${s.qtyAvailable})`,
      value: s.warehouseId
    }));
  }

  removeEditLine(i: number) {
    if (this.editLines.length > 1) this.editLines.splice(i, 1);
  }

  saveEdit() {
    const invalid = this.editLines.filter(l => !l.productUuid || l.requestedQty <= 0);
    if (invalid.length > 0) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'All lines must have a product and quantity > 0.' });
      return;
    }

    const req: PatchMirRequest = {
      projectUuid:    this.editData.requestType === 'PROJECT'     ? this.editData.projectUuid    || undefined : undefined,
      department:     this.editData.requestType === 'DEPARTMENT'  ? this.editData.department     || undefined : undefined,
      maintenanceRef: this.editData.requestType === 'MAINTENANCE' ? this.editData.maintenanceRef || undefined : undefined,
      requiredDate:   this.editData.requiredDate ? this.editData.requiredDate.toISOString() : undefined,
      priority:       this.editData.priority   || undefined,
      purpose:        this.editData.purpose    || undefined,
      notes:          this.editData.notes      || undefined,
      lines:          this.editLines.map(l => ({
        productUuid:  l.productUuid,
        requestedQty: l.requestedQty,
        warehouseId:  l.selectedWarehouseId ?? undefined,
        purpose:      l.purpose || undefined,
        notes:        l.notes   || undefined
      }))
    };

    this.isEditing = true;
    this.materialService.patchMir(this.uuid, req).subscribe({
      next: (res) => {
        this.isEditing = false;
        if (res.success) {
          this.showEditDialog = false;
          this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'MIR updated successfully.' });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message ?? 'Update failed.' });
        }
      },
      error: (err: any) => {
        this.isEditing = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Update failed.' });
      }
    });
  }

  createIssueVoucher() {
    this.router.navigate(['/portal/pages/material/miv/create'], {
      queryParams: { mirUuid: this.uuid }
    });
  }

  getMivStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'DRAFT':     return 'secondary';
      case 'POSTED':    return 'success';
      case 'CANCELLED': return 'danger';
      default:          return 'secondary';
    }
  }

  cancel() {
    this.confirmationService.confirm({
      message: 'Cancel this Material Issue Request? Any active stock reservations will be released.',
      header: 'Confirm Cancel',
      icon: 'pi pi-exclamation-triangle',
      accept: () => {
        this.actionBusy = true;
        this.materialService.cancelMir(this.uuid).subscribe({
          next: () => { this.actionBusy = false; this.messageService.add({ severity: 'info', summary: 'Cancelled', detail: 'MIR cancelled and stock reservations released.' }); this.load(); },
          error: (err: any) => { this.actionBusy = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Cancel failed.' }); }
        });
      }
    });
  }

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'DRAFT':              return 'secondary';
      case 'PENDING_APPROVAL':   return 'warn';
      case 'APPROVED':           return 'success';
      case 'PARTIALLY_APPROVED': return 'info';
      case 'REJECTED':           return 'danger';
      case 'PARTIALLY_ISSUED':   return 'warn';
      case 'FULLY_ISSUED':       return 'success';
      case 'CANCELLED':          return 'danger';
      default:                   return 'secondary';
    }
  }

  getStatusLabel(s: string): string {
    const map: Record<string, string> = {
      DRAFT: 'Draft', PENDING_APPROVAL: 'Pending Approval',
      APPROVED: 'Approved', PARTIALLY_APPROVED: 'Partially Approved',
      REJECTED: 'Rejected', PARTIALLY_ISSUED: 'Partially Issued',
      FULLY_ISSUED: 'Fully Issued', CANCELLED: 'Cancelled'
    };
    return map[s] ?? s;
  }

  getTypeLabel(t: string): string {
    const map: Record<string, string> = { PROJECT: 'Project', DEPARTMENT: 'Department', MAINTENANCE: 'Maintenance' };
    return map[t] ?? t;
  }

  getPriorityLabel(p: string): string {
    const map: Record<string, string> = { LOW: 'Low', MEDIUM: 'Medium', HIGH: 'High', URGENT: 'Urgent' };
    return map[p] ?? p;
  }
}
