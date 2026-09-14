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
  PatchMirRequest, PrLineSearchResult
} from '../../../../services/material.service';
import { InventoryService, ProductListItemModel, VariantWarehouseStockModel } from '../../../../services/inventory.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { TimelinePanelComponent } from '../../../../shared/timeline-panel/timeline-panel.component';
import { ProductVariantPickerComponent, VariantPickerSelection } from '../../../../shared/product-variant-picker/product-variant-picker.component';

interface EditMirLine {
  // Populated for newly added lines (via the two-level picker); left blank for lines that
  // already existed on the MIR, since MirLineModel only denormalises VariantUuid/ProductName
  // and doesn't carry the parent product's own uuid back to the client (PV-005).
  productUuid:         string;
  productName:         string;
  variantUuid:         string;
  variantName:         string;
  unitCost:            number;
  isExisting:          boolean;
  requestedQty:        number;
  purpose:             string;
  notes:               string;
  stockItems:          VariantWarehouseStockModel[];
  selectedWarehouseId: number | null;
  maxQty:              number | null;
  isLoadingStock:      boolean;
  prLineId:            string | null;
  prLineLabel:         string | null;
  prSearchResults:     PrLineSearchResult[];
  showPrResults:       boolean;
  isFetchingPr:        boolean;
  prFetchAttempted:    boolean;
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
    DropdownModule, CalendarModule, TimelinePanelComponent, ProductVariantPickerComponent
  ],
  templateUrl: './mir-detail.component.html',
  styleUrls: ['./mir-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class MirDetailComponent implements OnInit {
  uuid!: string;
  showTimeline = false;
  mir: MirDetail | null = null;
  isLoading  = true;
  submitting = false;
  approving  = false;
  rejecting  = false;
  cancelling = false;
  isDownloadingPdf = false;
  minDate = new Date();

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
  editProducts: ProductListItemModel[] = [];
  editProjectOptions: { label: string; value: string }[] = [];
  isLoadingEditData = false;

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
    private confirmationService: ConfirmationService,
    private attachmentService: AttachmentService
  ) {}

  resolveImageUrl(url: string): string {
    return this.attachmentService.resolveUrl(url);
  }

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
    this.submitting = true;
    this.materialService.submitMir(this.uuid).subscribe({
      next: () => {
        this.submitting = false;
        this.messageService.add({ severity: 'success', summary: 'Submitted', detail: 'MIR submitted for approval.' });
        this.load();
      },
      error: (err: any) => {
        this.submitting = false;
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
    this.approving = true;
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
        this.approving = false;
        this.messageService.add({ severity: 'success', summary: 'Approved', detail: 'Approval step recorded.' });
        this.load();
      },
      error: (err: any) => {
        this.approving = false;
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
    this.rejecting = true;
    this.materialService.workflowRejectMir(this.uuid, {
      approvalUUID: this.mir.activeApprovalUuid,
      reason:       this.rejectReason
    }).subscribe({
      next: () => {
        this.rejecting = false;
        this.messageService.add({ severity: 'info', summary: 'Rejected', detail: 'MIR rejected.' });
        this.load();
      },
      error: (err: any) => {
        this.rejecting = false;
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
      productUuid:         '',
      productName:         l.productName ?? '',
      variantUuid:         l.variantUuid,
      variantName:         l.variantName ?? '',
      unitCost:            l.unitCost,
      isExisting:          true,
      requestedQty:        l.requestedQty,
      purpose:             l.purpose ?? '',
      notes:               l.notes   ?? '',
      stockItems:          [],
      selectedWarehouseId: l.warehouseId ?? null,
      maxQty:              null,
      isLoadingStock:      false,
      prLineId:            l.prLineId ?? null,
      prLineLabel:         l.prLineId ? 'Linked PR' : null,
      prSearchResults:     [],
      showPrResults:       false,
      isFetchingPr:        false,
      prFetchAttempted:    false
    }));

    if (this.editProducts.length === 0 || this.editProjectOptions.length === 0) {
      this.isLoadingEditData = true;
      forkJoin({
        products: this.inventoryService.getProducts({ activeOnly: true, pageSize: 500 }),
        projects: this.materialService.getProjects({ status: 'ACTIVE', pageSize: 200 })
      }).subscribe({
        next: ({ products, projects }) => {
          this.isLoadingEditData = false;
          if (products.success && products.result) {
            this.editProducts = products.result.data;
          }
          if (projects.success && projects.result) {
            this.editProjectOptions = (projects.result.data ?? []).map(p => ({
              label: `${p.projectCode} — ${p.projectName}`,
              value: p.uuid
            }));
          }
        },
        error: () => { this.isLoadingEditData = false; }
      });
    }

    this.showEditDialog = true;
  }

  addEditLine() {
    this.editLines.push({
      productUuid: '', productName: '', variantUuid: '', variantName: '', unitCost: 0, isExisting: false,
      requestedQty: 1, purpose: '', notes: '',
      stockItems: [], selectedWarehouseId: null, maxQty: null, isLoadingStock: false,
      prLineId: null, prLineLabel: null, prSearchResults: [], showPrResults: false,
      isFetchingPr: false, prFetchAttempted: false
    });
  }

  // New lines only — existing lines' product/variant is fixed (see EditMirLine comment above).
  onEditVariantSelected(i: number, sel: VariantPickerSelection) {
    const line = this.editLines[i];
    line.productUuid = sel.productUuid ?? '';
    line.productName = sel.productName ?? '';
    line.variantUuid = sel.variantUuid ?? '';
    line.variantName = sel.variantName ?? '';
    line.unitCost     = sel.purchasePrice ?? 0;
    line.stockItems = [];
    line.selectedWarehouseId = null;
    line.maxQty = null;
    this.clearEditPrLine(i);

    if (!line.variantUuid) return;

    line.isLoadingStock = true;
    // Per-warehouse availability for THIS variant specifically (bins summed) — getProductStock
    // returns one row per bin across every variant of the product, which showed as multiple,
    // seemingly-duplicate entries for the same warehouse.
    this.inventoryService.getVariantStock(line.variantUuid).subscribe({
      next: (res) => {
        line.isLoadingStock = false;
        if (res.success && res.result) {
          line.stockItems = res.result.filter(s => s.qtyAvailable > 0);
        }
      },
      error: () => { line.isLoadingStock = false; }
    });
  }

  // ── Link to Purchase Requisition (Fetch button) ─────────────────────────────

  fetchEditPrLines(i: number) {
    const line = this.editLines[i];
    if (!line.productUuid) return;

    line.isFetchingPr = true;
    line.prFetchAttempted = false;
    line.showPrResults = false;
    this.materialService.searchPrLines(line.productUuid, 'APPROVED').subscribe({
      next: (res) => {
        line.isFetchingPr = false;
        line.prFetchAttempted = true;
        line.prSearchResults = res.success && res.result ? res.result : [];
        line.showPrResults = true;
      },
      error: () => {
        line.isFetchingPr = false;
        line.prFetchAttempted = true;
        line.prSearchResults = [];
        line.showPrResults = true;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to search purchase requisitions.' });
      }
    });
  }

  getPrLineOptionLabel(r: PrLineSearchResult): string {
    return `${r.prNumber} — ${r.prTitle} — ${r.lineDescription} — Requested: ${r.requestedQty} — Remaining: ${r.remainingUndisbursedQty}`;
  }

  selectEditPrLine(i: number, result: PrLineSearchResult) {
    const line = this.editLines[i];
    line.prLineId    = result.prLineId;
    line.prLineLabel = result.prNumber;
    line.showPrResults = false;
  }

  clearEditPrLine(i: number) {
    const line = this.editLines[i];
    line.prLineId    = null;
    line.prLineLabel = null;
    line.prSearchResults = [];
    line.showPrResults = false;
    line.prFetchAttempted = false;
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
    const invalid = this.editLines.filter(l => !l.variantUuid || l.requestedQty <= 0);
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
        variantUuid:  l.variantUuid,
        requestedQty: l.requestedQty,
        warehouseId:  l.selectedWarehouseId ?? undefined,
        purpose:      l.purpose || undefined,
        notes:        l.notes   || undefined,
        prLineId:     l.prLineId ?? undefined
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

  downloadPdf() {
    if (!this.mir) return;
    this.isDownloadingPdf = true;
    this.materialService.downloadMirPdf(this.uuid).subscribe({
      next: (blob) => {
        this.isDownloadingPdf = false;
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `MIR-${this.mir?.requestNo || this.uuid}.pdf`;
        a.click();
        URL.revokeObjectURL(url);
      },
      error: () => {
        this.isDownloadingPdf = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to generate MIR PDF.' });
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
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.cancelling = true;
        this.materialService.cancelMir(this.uuid).subscribe({
          next: () => { this.cancelling = false; this.messageService.add({ severity: 'info', summary: 'Cancelled', detail: 'MIR cancelled and stock reservations released.' }); this.load(); },
          error: (err: any) => { this.cancelling = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Cancel failed.' }); }
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
