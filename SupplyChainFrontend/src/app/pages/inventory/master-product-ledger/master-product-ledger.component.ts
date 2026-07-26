import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { AutoCompleteModule } from 'primeng/autocomplete';
import { DropdownModule } from 'primeng/dropdown';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { DialogModule } from 'primeng/dialog';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import {
  MasterProductLedgerService, MasterProductLedgerEntryModel, MasterProductLedgerFilter, MasterProductLedgerSummaryModel
} from '../../../services/master-product-ledger.service';
import { InventoryService, ProductListItemModel, CategoryModel, WarehouseModel } from '../../../services/inventory.service';

const TRANSACTION_TYPE_OPTIONS = [
  { label: 'GRN Receipt',       value: 'GRN_RECEIPT' },
  { label: 'Return Dispatch',   value: 'RETURN_DISPATCH' },
  { label: 'Stock Adjustment',  value: 'STOCK_ADJUSTMENT' },
  { label: 'Material Issue',    value: 'MATERIAL_ISSUE' },
  { label: 'Material Return',   value: 'MATERIAL_RETURN' },
  { label: 'Transfer Out',      value: 'TRANSFER_OUT' },
  { label: 'Transfer In',       value: 'TRANSFER_IN' },
];

const PARTY_TYPE_OPTIONS = [
  { label: 'Supplier',    value: 'SUPPLIER' },
  { label: 'Warehouse',   value: 'WAREHOUSE' },
  { label: 'Project',     value: 'PROJECT' },
  { label: 'Department',  value: 'DEPARTMENT' },
  { label: 'Adjustment',  value: 'ADJUSTMENT' },
];

// Reference types with a real detail page today — everything else renders as plain text.
const REFERENCE_ROUTES: Record<string, string> = {
  GRN:            '/portal/pages/warehouse/grn',
  SRO:            '/portal/pages/warehouse/sro',
  MIV:            '/portal/pages/material/miv',
  RETURN_VOUCHER: '/portal/pages/material/returns',
};

@Component({
  selector: 'app-master-product-ledger',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, CalendarModule, AutoCompleteModule, DropdownModule,
    TagModule, TooltipModule, DialogModule, ToastModule
  ],
  templateUrl: './master-product-ledger.component.html',
  styleUrls: ['./master-product-ledger.component.scss'],
  providers: [MessageService]
})
export class MasterProductLedgerComponent implements OnInit {
  entries: MasterProductLedgerEntryModel[] = [];
  summary: MasterProductLedgerSummaryModel | null = null;
  isLoading = true;
  isExportingPdf = false;
  isExportingExcel = false;

  totalRecords = 0;
  page = 1;
  pageSize = 20;

  // ── Filters ──────────────────────────────────────────────────────────────
  dateFrom: Date | null = null;
  dateTo: Date | null = null;
  selectedTransactionType: string | null = null;
  transactionTypeOptions = TRANSACTION_TYPE_OPTIONS;
  partyTypeOptions = PARTY_TYPE_OPTIONS;
  selectedSourceType: string | null = null;
  selectedDestinationType: string | null = null;

  selectedProduct: ProductListItemModel | null = null;
  productSuggestions: ProductListItemModel[] = [];
  categories: CategoryModel[] = [];
  selectedCategoryId: number | null = null;
  warehouses: WarehouseModel[] = [];
  selectedWarehouseId: number | null = null;

  // ── Product Journey ────────────────────────────────────────────────────────
  showJourneyDialog = false;
  isLoadingJourney = false;
  journeyEntries: MasterProductLedgerEntryModel[] = [];
  journeyProductName = '';

  constructor(
    private ledgerService: MasterProductLedgerService,
    private inventoryService: InventoryService,
    private router: Router,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.inventoryService.getCategories().subscribe({
      next: (res) => { this.categories = res.success ? res.result : []; }
    });
    this.inventoryService.getWarehouses().subscribe({
      next: (res) => { this.warehouses = res.success ? res.result : []; }
    });
    this.load();
    this.loadSummary();
  }

  private buildFilter(): MasterProductLedgerFilter {
    return {
      dateFrom: this.dateFrom ? this.toIsoDate(this.dateFrom) : undefined,
      dateTo:   this.dateTo   ? this.toIsoDate(this.dateTo)   : undefined,
      productId: this.selectedProduct?.id,
      categoryId: this.selectedCategoryId ?? undefined,
      warehouseId: this.selectedWarehouseId ?? undefined,
      transactionType: this.selectedTransactionType ?? undefined,
      sourceType: this.selectedSourceType ?? undefined,
      destinationType: this.selectedDestinationType ?? undefined,
      page: this.page,
      pageSize: this.pageSize
    };
  }

  private toIsoDate(d: Date): string {
    return new Date(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate())).toISOString();
  }

  load() {
    this.isLoading = true;
    this.ledgerService.getLedger(this.buildFilter()).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success) {
          this.entries = res.result.data;
          this.totalRecords = res.result.totalRecords;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load master product ledger.' });
      }
    });
  }

  loadSummary() {
    this.ledgerService.getSummary(this.buildFilter()).subscribe({
      next: (res) => { this.summary = res.success ? res.result : null; },
      error: () => { this.summary = null; }
    });
  }

  onPageChange(event: TableLazyLoadEvent) {
    const rows = event.rows || this.pageSize;
    this.pageSize = rows;
    this.page = Math.floor((event.first || 0) / rows) + 1;
    this.load();
  }

  applyFilters() {
    this.page = 1;
    this.load();
    this.loadSummary();
  }

  resetFilters() {
    this.dateFrom = null;
    this.dateTo = null;
    this.selectedTransactionType = null;
    this.selectedSourceType = null;
    this.selectedDestinationType = null;
    this.selectedProduct = null;
    this.selectedCategoryId = null;
    this.selectedWarehouseId = null;
    this.page = 1;
    this.load();
    this.loadSummary();
  }

  searchProducts(event: any) {
    const q = (event.query as string ?? '').trim();
    this.inventoryService.getProducts({ search: q || undefined, pageSize: 20 }).subscribe({
      next: (res) => { this.productSuggestions = res.success ? res.result.data : []; },
      error: () => { this.productSuggestions = []; }
    });
  }

  // ── Product Journey ────────────────────────────────────────────────────────

  get canViewJourney(): boolean {
    return !!this.selectedProduct;
  }

  viewJourney() {
    if (!this.selectedProduct) return;
    this.journeyProductName = this.selectedProduct.name;
    this.showJourneyDialog = true;
    this.isLoadingJourney = true;
    this.journeyEntries = [];
    this.ledgerService.getProductJourney(this.selectedProduct.id).subscribe({
      next: (res) => {
        this.isLoadingJourney = false;
        this.journeyEntries = res.success ? res.result : [];
      },
      error: () => {
        this.isLoadingJourney = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load product journey.' });
      }
    });
  }

  // ── Drill-down ─────────────────────────────────────────────────────────────

  canDrillDownReference(e: MasterProductLedgerEntryModel): boolean {
    return !!REFERENCE_ROUTES[e.referenceType];
  }

  drillDownReference(e: MasterProductLedgerEntryModel) {
    const base = REFERENCE_ROUTES[e.referenceType];
    if (base) this.router.navigate([base, e.referenceId]);
  }

  goToProduct(e: MasterProductLedgerEntryModel) {
    this.router.navigate(['/portal/pages/inventory/products', e.productId]);
  }

  // ── Export ───────────────────────────────────────────────────────────────

  exportPdf() {
    this.isExportingPdf = true;
    this.ledgerService.exportPdf(this.buildFilter()).subscribe({
      next: (blob) => { this.isExportingPdf = false; this.downloadBlob(blob, 'master-product-ledger.pdf'); },
      error: () => {
        this.isExportingPdf = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'PDF export failed.' });
      }
    });
  }

  exportExcel() {
    this.isExportingExcel = true;
    this.ledgerService.exportExcel(this.buildFilter()).subscribe({
      next: (blob) => { this.isExportingExcel = false; this.downloadBlob(blob, 'master-product-ledger.xlsx'); },
      error: () => {
        this.isExportingExcel = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Excel export failed.' });
      }
    });
  }

  private downloadBlob(blob: Blob, fileName: string) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
    URL.revokeObjectURL(url);
  }

  getTypeSeverity(t: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' {
    switch (t) {
      case 'GRN_RECEIPT':
      case 'MATERIAL_RETURN':
      case 'TRANSFER_IN':
        return 'success'; // stock coming in
      case 'RETURN_DISPATCH':
      case 'MATERIAL_ISSUE':
      case 'TRANSFER_OUT':
        return 'warn'; // stock going out
      default:
        return 'secondary';
    }
  }

  getTypeLabel(t: string): string {
    return this.transactionTypeOptions.find(o => o.value === t)?.label ?? t;
  }
}
