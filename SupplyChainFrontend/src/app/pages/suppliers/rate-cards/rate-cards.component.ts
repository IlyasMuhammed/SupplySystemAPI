import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule, TableLazyLoadEvent, TableEditCompleteEvent, TableEditCancelEvent } from 'primeng/table';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { DialogModule } from 'primeng/dialog';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { FileUploadModule, FileSelectEvent } from 'primeng/fileupload';
import { ContextMenuModule } from 'primeng/contextmenu';
import { CalendarModule } from 'primeng/calendar';
import { MessageService, ConfirmationService, MenuItem } from 'primeng/api';
import { SelectButtonModule } from 'primeng/selectbutton';
import {
  RateCardService, RateCardRow, UpdateRateCardRequest, RateComparisonRow,
  BulkAdjustRequest, BulkAdjustPreviewRow, BulkRateOperation,
  ImportPreviewRow, CopyRatesRequest, CopyPreviewRow, CreateRateCardRequest
} from '../../../services/rate-card.service';
import { SupplierService } from '../../../services/supplier.service';
import { CurrenciesService, CurrencyModel } from '../../../services/currencies.service';
import { InventoryService, ProductListItemModel } from '../../../services/inventory.service';
import { ProductVariantPickerComponent, VariantPickerSelection } from '../../../shared/product-variant-picker/product-variant-picker.component';
import { RateEditPanelComponent } from '../rate-edit-panel/rate-edit-panel.component';

const CHANGE_REASON_THRESHOLD = 0.10;

@Component({
  selector: 'app-rate-cards',
  standalone: true,
  imports: [
    CommonModule, FormsModule,
    TableModule, ButtonModule, InputTextModule, TextareaModule,
    InputIconModule, IconFieldModule, TagModule, TooltipModule,
    ToastModule, DropdownModule, DialogModule, SelectButtonModule,
    CheckboxModule, ConfirmDialogModule, FileUploadModule, ContextMenuModule, CalendarModule,
    ProductVariantPickerComponent, RateEditPanelComponent
  ],
  templateUrl: './rate-cards.component.html',
  styleUrls: ['./rate-cards.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class RateCardsComponent implements OnInit {
  // RC-003 — Supplier View (RC-002) vs Product Comparison View.
  viewModeOptions = [
    { label: 'By Supplier', value: 'supplier' },
    { label: 'By Product', value: 'product' }
  ];
  viewMode: 'supplier' | 'product' = 'supplier';

  supplierOptions: { label: string; value: string }[] = [];
  selectedSupplierId: string | null = null;

  rows: RateCardRow[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = false;

  searchText = '';
  sortField  = 'productName';
  sortOrder: 1 | -1 = 1;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  currencies: CurrencyModel[] = [];
  // Shared by both the Product Comparison View's picker and the Add Rate Card dialog's picker.
  products: ProductListItemModel[] = [];

  // Row-level edit state: snapshot taken when a cell is clicked into, so Escape (onEditCancel)
  // can restore it, and so we know the pre-edit rate for the >10% change-reason check.
  private clonedRows: Record<string, RateCardRow> = {};

  showReasonDialog = false;
  changeReason = '';
  private pendingReasonRow: RateCardRow | null = null;
  private pendingReasonOldRate: number | null = null;

  // RC-003 — Product Comparison View state.
  selectedVariantUuid: string | null = null;
  comparisonRows: RateComparisonRow[] = [];
  isLoadingComparison = false;

  // RC-004 — rate-edit slide-over.
  showEditPanel = false;
  editPanelUuid: string | null = null;

  // RC-005 — bulk rate adjustment.
  selectedRows: RateCardRow[] = [];
  showBulkDialog = false;
  bulkStep: 'setup' | 'preview' = 'setup';
  bulkMethodOptions = [
    { label: 'Percentage', value: 'PERCENTAGE' as const },
    { label: 'Fixed Amount', value: 'FIXED' as const }
  ];
  bulkMethod: 'PERCENTAGE' | 'FIXED' = 'PERCENTAGE';
  bulkValue: number | null = null;
  bulkChangeReason = '';
  bulkPreviewRows: BulkAdjustPreviewRow[] = [];
  isBulkPreviewing = false;
  isBulkConfirming = false;

  recentBulkOps: BulkRateOperation[] = [];
  showBulkHistoryDialog = false;
  isLoadingBulkHistory = false;

  // RC-006 — Excel import.
  showImportDialog = false;
  importStep: 'select' | 'preview' = 'select';
  importFile: File | null = null;
  importCurrencyId: string | null = null;
  importPreviewRows: ImportPreviewRow[] = [];
  isImportPreviewing = false;
  isImportConfirming = false;

  // RC-007 — right-click "Mark as Reviewed" context menu on grid rows.
  contextMenuItems: MenuItem[] = [
    { label: 'Mark as Reviewed', icon: 'pi pi-check-circle', command: () => this.markReviewed(this.contextRow) }
  ];
  contextRow: RateCardRow | null = null;

  // RC-006 — Copy from Supplier.
  showCopyDialog = false;
  copyStep: 'setup' | 'preview' = 'setup';
  copySourceSupplierId: string | null = null;
  copyAdjustmentPct: number | null = null;
  copyPreviewRows: CopyPreviewRow[] = [];
  isCopyPreviewing = false;
  isCopyConfirming = false;

  // Add Rate Card — first-time creation of a supplier+variant link. Nothing in this feature ever
  // called POST /api/rate-cards before; every other screen only ever operated on links that
  // already existed, so a brand-new supplier had no way to get its first rate card in at all.
  showAddDialog = false;
  addPicked: VariantPickerSelection | null = null;
  addForm = {
    vendorUnitCost: null as number | null,
    currencyId: null as string | null,
    leadTimeDays: null as number | null,
    minOrderQty: null as number | null,
    minOrderValue: null as number | null,
    effectiveFrom: new Date() as Date | null,
    effectiveTo: null as Date | null,
    vendorPartNo: '',
    quotationRef: '',
    notes: ''
  };
  isAdding = false;

  constructor(
    private rateCardService: RateCardService,
    private supplierService: SupplierService,
    private currenciesService: CurrenciesService,
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit() {
    this.supplierService.getSuppliers({ status: 'ACTIVE', pageSize: 500 }).subscribe({
      next: (res) => {
        if (res.success && res.result) {
          this.supplierOptions = res.result.data.map(s => ({ label: s.supplierName, value: s.uuid }));
        }
      }
    });
    this.currenciesService.getAll().subscribe({
      next: (res) => { if (res.success && res.result) this.currencies = res.result; }
    });
    this.inventoryService.getProducts({ activeOnly: true, pageSize: 500 }).subscribe({
      next: (res) => { if (res.success && res.result) this.products = res.result.data ?? []; }
    });
    this.loadRecentBulkOps();
  }

  currencyCode(currencyId: string): string {
    const c = this.currencies.find(x => x.id === currencyId);
    return c?.code || c?.name || '—';
  }

  onSupplierChange() {
    this.currentPage = 1;
    this.selectedRows = [];
    this.load();
  }

  load() {
    if (!this.selectedSupplierId) return;
    this.isLoading = true;
    this.selectedRows = [];
    this.rateCardService.getRateCards({
      supplierId: this.selectedSupplierId,
      page: this.currentPage,
      pageSize: this.pageSize,
      search: this.searchText || undefined,
      sortBy: this.sortField,
      sortDir: this.sortOrder === 1 ? 'asc' : 'desc'
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.rows = res.result.data ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else {
          this.rows = []; this.totalRecords = 0;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load rate cards.' });
      }
    });
  }

  // Instant client-side filter on the currently loaded page, for snappy feedback while typing —
  // the debounced server reload below (with `search`) covers cross-page results.
  get visibleRows(): RateCardRow[] {
    const term = this.searchText.trim().toLowerCase();
    if (!term) return this.rows;
    return this.rows.filter(r =>
      r.productName.toLowerCase().includes(term) ||
      r.variantName.toLowerCase().includes(term) ||
      r.sku.toLowerCase().includes(term) ||
      (r.vendorPartNo ?? '').toLowerCase().includes(term));
  }

  onSearchInput() {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => { this.currentPage = 1; this.load(); }, 400);
  }

  onLazyLoad(event: TableLazyLoadEvent) {
    this.currentPage = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize)) + 1;
    this.pageSize    = event.rows ?? this.pageSize;
    if (event.sortField && typeof event.sortField === 'string') {
      this.sortField = event.sortField;
      this.sortOrder = (event.sortOrder ?? 1) as 1 | -1;
    }
    this.load();
  }

  // ── Inline editing ───────────────────────────────────────────────────────

  startEdit(row: RateCardRow) {
    if (!this.clonedRows[row.uuid]) {
      this.clonedRows[row.uuid] = { ...row };
    }
  }

  onEditCancel(event: TableEditCancelEvent) {
    const row = event.data as RateCardRow | undefined;
    if (!row) return;
    const clone = this.clonedRows[row.uuid];
    if (clone) {
      Object.assign(row, clone);
      delete this.clonedRows[row.uuid];
    }
  }

  onEditComplete(event: TableEditCompleteEvent) {
    const row = event.data as RateCardRow | undefined;
    if (!row) return;
    if (event.field === 'vendorUnitCost') {
      this.handleRateEditComplete(row);
    } else {
      delete this.clonedRows[row.uuid];
      this.saveRow(row);
    }
  }

  private handleRateEditComplete(row: RateCardRow) {
    const oldRate = this.clonedRows[row.uuid]?.vendorUnitCost ?? row.vendorUnitCost;
    const newRate = row.vendorUnitCost;
    delete this.clonedRows[row.uuid];

    if (oldRate === newRate) return;

    if (oldRate > 0 && Math.abs(newRate - oldRate) / oldRate > CHANGE_REASON_THRESHOLD) {
      this.pendingReasonRow = row;
      this.pendingReasonOldRate = oldRate;
      this.changeReason = '';
      this.showReasonDialog = true;
      return;
    }

    this.saveRow(row);
  }

  confirmReasonAndSave() {
    if (!this.changeReason.trim() || !this.pendingReasonRow) return;
    this.showReasonDialog = false;
    this.saveRow(this.pendingReasonRow, this.changeReason);
    this.pendingReasonRow = null;
    this.pendingReasonOldRate = null;
  }

  cancelReasonDialog() {
    if (this.pendingReasonRow && this.pendingReasonOldRate != null) {
      this.pendingReasonRow.vendorUnitCost = this.pendingReasonOldRate;
    }
    this.showReasonDialog = false;
    this.pendingReasonRow = null;
    this.pendingReasonOldRate = null;
  }

  private saveRow(row: RateCardRow, changeReason?: string) {
    const req: UpdateRateCardRequest = {
      vendorUnitCost: row.vendorUnitCost,
      leadTimeDays:   row.leadTimeDays,
      effectiveFrom:  row.effectiveFrom,
      effectiveTo:    row.effectiveTo,
      currencyId:     row.currencyId,
      minOrderValue:  row.minOrderValue,
      minOrderQty:    row.minOrderQty,
      discountTiers:  row.discountTiers,
      quotationRef:   row.quotationRef,
      notes:          row.notes,
      vendorPartNo:   row.vendorPartNo,
      isActive:       row.isActive,
      lastReviewedAt: row.lastReviewedAt,
      lastReviewedBy: row.lastReviewedBy,
      changeReason
    };

    this.rateCardService.updateRateCard(row.uuid, req).subscribe({
      next: (res) => {
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Rate card updated.' });
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Save failed.' });
          this.load();
        }
      },
      error: (err) => {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Save failed.' });
        this.load();
      }
    });
  }

  // ── Preferred toggle ─────────────────────────────────────────────────────

  togglePreferred(row: RateCardRow) {
    if (row.isPreferred) return;
    this.rateCardService.setPreferred(row.uuid).subscribe({
      next: (res) => {
        if (res.success) {
          this.rows.forEach(r => r.isPreferred = r.uuid === row.uuid);
          this.messageService.add({ severity: 'success', summary: 'Preferred', detail: `${row.productName} is now preferred for this supplier.` });
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to set preferred supplier.' });
        }
      },
      error: (err) => {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to set preferred supplier.' });
      }
    });
  }

  // ── Display helpers ──────────────────────────────────────────────────────

  lastPoPriceClass(row: RateCardRow): string {
    if (row.lastPoPrice == null) return 'po-price-none';
    return row.lastPoPrice === row.vendorUnitCost ? 'po-price-match' : 'po-price-mismatch';
  }

  statusSeverity(status: string): 'success' | 'danger' | 'info' | 'warn' | 'secondary' {
    switch (status) {
      case 'ACTIVE':  return 'success';
      case 'EXPIRED': return 'danger';
      case 'PENDING': return 'info';
      case 'STALE':   return 'warn';
      default:        return 'secondary';
    }
  }

  // ── RC-003: Product Comparison View ─────────────────────────────────────

  onVariantSelected(sel: VariantPickerSelection) {
    this.selectedVariantUuid = sel.variantUuid;
    this.loadComparison();
  }

  loadComparison() {
    if (!this.selectedVariantUuid) return;
    this.isLoadingComparison = true;
    this.rateCardService.getComparison(this.selectedVariantUuid).subscribe({
      next: (res) => {
        this.isLoadingComparison = false;
        this.comparisonRows = res.success && res.result ? res.result : [];
      },
      error: () => {
        this.isLoadingComparison = false;
        this.comparisonRows = [];
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load rate comparison.' });
      }
    });
  }

  get lowestRate(): number | null {
    if (this.comparisonRows.length === 0) return null;
    return Math.min(...this.comparisonRows.map(r => r.vendorUnitCost));
  }

  isLowest(row: RateComparisonRow): boolean {
    return this.lowestRate != null && row.vendorUnitCost === this.lowestRate;
  }

  ratePremiumLabel(row: RateComparisonRow): string {
    const lowest = this.lowestRate;
    if (lowest == null) return '—';
    if (row.vendorUnitCost === lowest) return 'Lowest';
    const pct = ((row.vendorUnitCost - lowest) / lowest) * 100;
    const amount = row.vendorUnitCost - lowest;
    return `+${pct.toFixed(1)}% / +${amount.toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 2 })}`;
  }

  gradeSeverity(grade: string | null | undefined): 'success' | 'info' | 'warn' | 'danger' | 'secondary' {
    switch (grade) {
      case 'A': return 'success';
      case 'B': return 'info';
      case 'C': return 'warn';
      case 'D': return 'warn';
      case 'F': return 'danger';
      default:  return 'secondary';
    }
  }

  exportComparison() {
    if (!this.selectedVariantUuid) return;
    this.rateCardService.exportComparison(this.selectedVariantUuid).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `RateComparison-${this.selectedVariantUuid}.xlsx`;
        a.click();
        URL.revokeObjectURL(url);
      },
      error: () => {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to export comparison.' });
      }
    });
  }

  // RC-004 — clicking a comparison row opens the full rate-edit panel for that supplier+variant.
  onComparisonRowClick(row: RateComparisonRow) {
    this.openEditPanel(row.uuid);
  }

  // ── RC-004: rate-edit slide-over ─────────────────────────────────────────

  openEditPanel(uuid: string) {
    this.editPanelUuid = uuid;
    this.showEditPanel = true;
  }

  onEditPanelSaved() {
    // Refresh whichever view is currently on screen so the edited row reflects the save.
    if (this.viewMode === 'supplier') this.load();
    else this.loadComparison();
  }

  // ── RC-005: Bulk Rate Adjustment ─────────────────────────────────────────

  openBulkDialog() {
    if (this.selectedRows.length === 0) return;
    this.bulkStep = 'setup';
    this.bulkMethod = 'PERCENTAGE';
    this.bulkValue = null;
    this.bulkChangeReason = '';
    this.bulkPreviewRows = [];
    this.showBulkDialog = true;
  }

  closeBulkDialog() {
    this.showBulkDialog = false;
  }

  get canPreviewBulk(): boolean {
    return this.selectedRows.length > 0 && this.bulkValue != null && this.bulkValue !== 0 && this.bulkChangeReason.trim().length > 0;
  }

  previewBulkAdjust() {
    if (!this.canPreviewBulk) return;
    const req: BulkAdjustRequest = {
      variantSupplierIds: this.selectedRows.map(r => r.uuid),
      method: this.bulkMethod,
      value: this.bulkValue!,
      changeReason: this.bulkChangeReason.trim()
    };
    this.isBulkPreviewing = true;
    this.rateCardService.previewBulkAdjust(req).subscribe({
      next: (res) => {
        this.isBulkPreviewing = false;
        if (res.success && res.result) {
          this.bulkPreviewRows = res.result;
          this.bulkStep = 'preview';
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Preview failed.' });
        }
      },
      error: (err) => {
        this.isBulkPreviewing = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Preview failed.' });
      }
    });
  }

  backToBulkSetup() {
    this.bulkStep = 'setup';
  }

  get bulkTotalImpact(): number {
    return this.bulkPreviewRows.reduce((sum, r) => sum + r.difference, 0);
  }

  confirmBulkAdjust() {
    const req: BulkAdjustRequest = {
      variantSupplierIds: this.selectedRows.map(r => r.uuid),
      method: this.bulkMethod,
      value: this.bulkValue!,
      changeReason: this.bulkChangeReason.trim()
    };
    this.isBulkConfirming = true;
    this.rateCardService.confirmBulkAdjust(req).subscribe({
      next: (res) => {
        this.isBulkConfirming = false;
        if (res.success && res.result) {
          this.messageService.add({
            severity: 'success', summary: 'Bulk Adjustment Applied',
            detail: `${res.result.affectedCount} rates updated. Undo available for 24 hours.`
          });
          this.showBulkDialog = false;
          this.selectedRows = [];
          this.load();
          this.loadRecentBulkOps();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Bulk adjustment failed.' });
        }
      },
      error: (err) => {
        this.isBulkConfirming = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Bulk adjustment failed.' });
      }
    });
  }

  // ── Recent Bulk Adjustments / Undo ───────────────────────────────────────

  loadRecentBulkOps() {
    this.isLoadingBulkHistory = true;
    this.rateCardService.getRecentBulkOperations().subscribe({
      next: (res) => {
        this.isLoadingBulkHistory = false;
        this.recentBulkOps = res.success && res.result ? res.result : [];
      },
      error: () => { this.isLoadingBulkHistory = false; }
    });
  }

  isWithinUndoWindow(op: BulkRateOperation): boolean {
    return Date.now() - new Date(op.performedAt).getTime() < 24 * 60 * 60 * 1000;
  }

  canUndo(op: BulkRateOperation): boolean {
    return !op.isUndone && this.isWithinUndoWindow(op);
  }

  confirmUndo(op: BulkRateOperation) {
    this.confirmationService.confirm({
      message: `Undo this bulk adjustment? ${op.affectedCount} rate(s) will be restored to their previous values.`,
      header: 'Confirm Undo',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.undoBulkOp(op)
    });
  }

  private undoBulkOp(op: BulkRateOperation) {
    this.rateCardService.undoBulkAdjust(op.uuid).subscribe({
      next: (res) => {
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Undone', detail: 'Bulk adjustment reversed.' });
          this.load();
          this.loadRecentBulkOps();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Undo failed.' });
        }
      },
      error: (err) => {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Undo failed.' });
      }
    });
  }

  // ── RC-006: Excel export ─────────────────────────────────────────────────

  exportRateCards() {
    if (!this.selectedSupplierId) return;
    this.rateCardService.exportRateCards(this.selectedSupplierId).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `RateCard-${this.selectedSupplierId}.xlsx`;
        a.click();
        URL.revokeObjectURL(url);
      },
      error: () => {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to export rate card.' });
      }
    });
  }

  // ── RC-006: Excel import ─────────────────────────────────────────────────

  openImportDialog() {
    this.importStep = 'select';
    this.importFile = null;
    this.importPreviewRows = [];
    // Defaulted the same way as Add Rate Card. Only used for products this supplier has no rate
    // card for yet; without it, and with no organization base currency, those rows cannot be created.
    this.importCurrencyId = this.importCurrencyId ?? this.currencies[0]?.id ?? null;
    this.showImportDialog = true;
  }

  closeImportDialog() {
    this.showImportDialog = false;
  }

  onImportFileSelect(event: FileSelectEvent) {
    this.importFile = event.files?.[0] ?? null;
  }

  previewImport() {
    if (!this.importFile || !this.selectedSupplierId) return;
    this.isImportPreviewing = true;
    this.rateCardService.previewImport(this.importFile, this.selectedSupplierId, this.importCurrencyId).subscribe({
      next: (res) => {
        this.isImportPreviewing = false;
        if (res.success && res.result) {
          this.importPreviewRows = res.result;
          this.importStep = 'preview';
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Import preview failed.' });
        }
      },
      error: (err) => {
        this.isImportPreviewing = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Import preview failed.' });
      }
    });
  }

  backToImportSelect() {
    this.importStep = 'select';
  }

  get importValidRowCount(): number {
    return this.importPreviewRows.filter(r => !r.error).length;
  }

  get importErrorRowCount(): number {
    return this.importPreviewRows.filter(r => !!r.error).length;
  }

  importRowStatus(row: ImportPreviewRow): { label: string; severity: 'success' | 'info' | 'danger' | 'secondary' } {
    if (row.error) return { label: 'Error', severity: 'danger' };
    if (row.newRecord) return { label: 'New Link', severity: 'info' };
    if (row.rateChanged) return { label: 'Rate Changed', severity: 'success' };
    return { label: 'No Change', severity: 'secondary' };
  }

  confirmImport() {
    if (!this.importFile || !this.selectedSupplierId) return;
    this.isImportConfirming = true;
    this.rateCardService.confirmImport(this.importFile, this.selectedSupplierId, this.importCurrencyId).subscribe({
      next: (res) => {
        this.isImportConfirming = false;
        if (res.success && res.result) {
          const { updatedCount, createdCount, errors } = res.result;
          this.messageService.add({
            severity: 'success', summary: 'Import Complete',
            detail: `${updatedCount} rate(s) updated, ${createdCount} new link(s) created` +
              (errors.length ? `, ${errors.length} row(s) skipped due to errors.` : '.')
          });
          this.showImportDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Import failed.' });
        }
      },
      error: (err) => {
        this.isImportConfirming = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Import failed.' });
      }
    });
  }

  // ── RC-006: Copy from Supplier ───────────────────────────────────────────

  get copySourceSupplierOptions(): { label: string; value: string }[] {
    return this.supplierOptions.filter(s => s.value !== this.selectedSupplierId);
  }

  openCopyDialog() {
    if (!this.selectedSupplierId) return;
    this.copyStep = 'setup';
    this.copySourceSupplierId = null;
    this.copyAdjustmentPct = null;
    this.copyPreviewRows = [];
    this.showCopyDialog = true;
  }

  closeCopyDialog() {
    this.showCopyDialog = false;
  }

  get canPreviewCopy(): boolean {
    return !!this.copySourceSupplierId && this.copyAdjustmentPct != null;
  }

  previewCopy() {
    if (!this.canPreviewCopy || !this.selectedSupplierId) return;
    const req: CopyRatesRequest = {
      sourceSupplierId: this.copySourceSupplierId!,
      targetSupplierId: this.selectedSupplierId,
      adjustmentPct: this.copyAdjustmentPct!
    };
    this.isCopyPreviewing = true;
    this.rateCardService.previewCopy(req).subscribe({
      next: (res) => {
        this.isCopyPreviewing = false;
        if (res.success && res.result) {
          this.copyPreviewRows = res.result;
          this.copyStep = 'preview';
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Copy preview failed.' });
        }
      },
      error: (err) => {
        this.isCopyPreviewing = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Copy preview failed.' });
      }
    });
  }

  backToCopySetup() {
    this.copyStep = 'setup';
  }

  get copyCreateCount(): number {
    return this.copyPreviewRows.filter(r => !r.willSkip).length;
  }

  get copySkipCount(): number {
    return this.copyPreviewRows.filter(r => r.willSkip).length;
  }

  confirmCopy() {
    if (!this.copySourceSupplierId || !this.selectedSupplierId) return;
    const req: CopyRatesRequest = {
      sourceSupplierId: this.copySourceSupplierId,
      targetSupplierId: this.selectedSupplierId,
      adjustmentPct: this.copyAdjustmentPct!
    };
    this.isCopyConfirming = true;
    this.rateCardService.confirmCopy(req).subscribe({
      next: (res) => {
        this.isCopyConfirming = false;
        if (res.success && res.result) {
          this.messageService.add({
            severity: 'success', summary: 'Rates Copied',
            detail: `${res.result.createdCount} rate(s) created, ${res.result.skippedCount} skipped (already linked).`
          });
          this.showCopyDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Copy failed.' });
        }
      },
      error: (err) => {
        this.isCopyConfirming = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Copy failed.' });
      }
    });
  }

  // ── RC-007: Mark as Reviewed ─────────────────────────────────────────────

  onRowContextMenu(event: MouseEvent, row: RateCardRow, cm: { show: (e: MouseEvent) => void }) {
    this.contextRow = row;
    cm.show(event);
  }

  markReviewed(row: RateCardRow | null) {
    if (!row) return;
    this.rateCardService.markReviewed(row.uuid).subscribe({
      next: (res) => {
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Reviewed', detail: `${row.productName} marked as reviewed.` });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to mark as reviewed.' });
        }
      },
      error: (err) => {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to mark as reviewed.' });
      }
    });
  }

  // ── Add Rate Card (first-time supplier+variant link creation) ───────────

  openAddDialog() {
    if (!this.selectedSupplierId) return;
    this.addPicked = null;
    this.addForm = {
      vendorUnitCost: null,
      currencyId: this.currencies[0]?.id ?? null,
      leadTimeDays: null,
      minOrderQty: null,
      minOrderValue: null,
      effectiveFrom: new Date(),
      effectiveTo: null,
      vendorPartNo: '',
      quotationRef: '',
      notes: ''
    };
    this.showAddDialog = true;
  }

  closeAddDialog() {
    this.showAddDialog = false;
  }

  onAddVariantSelected(sel: VariantPickerSelection) {
    this.addPicked = sel;
  }

  get canSaveAdd(): boolean {
    return !!this.addPicked?.variantUuid && !!this.addForm.currencyId
      && this.addForm.vendorUnitCost != null && this.addForm.vendorUnitCost > 0;
  }

  saveAddRateCard() {
    if (!this.canSaveAdd || !this.selectedSupplierId) return;

    const req: CreateRateCardRequest = {
      variantUuid:    this.addPicked!.variantUuid!,
      supplierUuid:   this.selectedSupplierId,
      vendorUnitCost: this.addForm.vendorUnitCost!,
      leadTimeDays:   this.addForm.leadTimeDays,
      effectiveFrom:  (this.addForm.effectiveFrom ?? new Date()).toISOString(),
      effectiveTo:    this.addForm.effectiveTo ? this.addForm.effectiveTo.toISOString() : null,
      currencyId:     this.addForm.currencyId,
      minOrderValue:  this.addForm.minOrderValue,
      minOrderQty:    this.addForm.minOrderQty,
      vendorPartNo:   this.addForm.vendorPartNo || null,
      quotationRef:   this.addForm.quotationRef || null,
      notes:          this.addForm.notes || null
    };

    this.isAdding = true;
    this.rateCardService.createRateCard(req).subscribe({
      next: (res) => {
        this.isAdding = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Added', detail: 'Rate card created.' });
          this.showAddDialog = false;
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to create rate card.' });
        }
      },
      error: (err) => {
        this.isAdding = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to create rate card.' });
      }
    });
  }
}
