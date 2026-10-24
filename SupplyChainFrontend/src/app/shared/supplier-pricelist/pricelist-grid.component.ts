import { Component, Input, OnChanges, OnDestroy, OnInit, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DropdownModule } from 'primeng/dropdown';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { InputTextModule } from 'primeng/inputtext';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';

import { InventoryService } from '../../services/inventory.service';
import { SupplierService } from '../../services/supplier.service';
import { PricelistItem, PricelistSaveResult, SupplierPricelistService, sortPricelist } from '../../services/supplier-pricelist.service';
import { PricelistDialogComponent, PricelistVariantOption } from './pricelist-dialog.component';

/**
 * A45 §4 C4 — the vendor-price grid, reused by the Product "Purchase" tab (product fixed), the Supplier "Products" tab
 * (vendor fixed) and the standalone Supplier Pricelists page (`showFilters`, nothing fixed). The column of the fixed side is
 * hidden. Add / edit open the dialog; deactivate asks first (soft delete). An overlap warning (C6) from a save is shown as a
 * warning toast while the save stands.
 */
@Component({
  selector: 'app-pricelist-grid',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, ConfirmDialogModule, DropdownModule, IconFieldModule, InputIconModule, InputTextModule,
    TableModule, ToastModule, TooltipModule, PricelistDialogComponent],
  providers: [MessageService, ConfirmationService],
  template: `
<p-toast key="pricelist"></p-toast>
<p-confirmDialog key="pricelist" [style]="{ width: '26rem' }"></p-confirmDialog>

<div class="pg-head">
  <div *ngIf="title"><h3 class="pg-title">{{ title }}</h3></div>
  <div class="sf-filters pg-filters">
    <p-iconfield class="sf-search" *ngIf="showFilters">
      <p-inputicon styleClass="pi pi-search"></p-inputicon>
      <input pInputText type="text" [(ngModel)]="search" (ngModelChange)="onSearchChange()" placeholder="Search product, vendor, vendor code" data-testid="pg-search" />
    </p-iconfield>
    <p-dropdown *ngIf="showFilters && !productUuid" [options]="productOptions" [(ngModel)]="filterProduct" optionLabel="label" optionValue="value"
                [filter]="true" filterBy="label" [showClear]="true" placeholder="All products" (onChange)="reload()" appendTo="body" data-testid="pg-filter-product"></p-dropdown>
    <p-dropdown *ngIf="showFilters && !supplierUuid" [options]="supplierOptions" [(ngModel)]="filterSupplier" optionLabel="label" optionValue="value"
                [filter]="true" filterBy="label" [showClear]="true" placeholder="All vendors" (onChange)="reload()" appendTo="body" data-testid="pg-filter-supplier"></p-dropdown>
    <button type="button" class="sf-chip" [class.on]="activeOnly" (click)="toggleActive()" data-testid="pg-active-only">Active only</button>
  </div>
  <p-button *ngIf="canEdit" label="Add price" icon="pi pi-plus" size="small" (onClick)="openAdd()" data-testid="pg-add"></p-button>
</div>

<div class="sf-card p0">
  <p-table [value]="rows" [lazy]="true" (onLazyLoad)="onPage($event)" [paginator]="true" [rows]="pageSize" [first]="(page - 1) * pageSize"
           [totalRecords]="total" [loading]="loading" [rowsPerPageOptions]="[20, 50, 100]" styleClass="p-datatable-sm" dataKey="uuid"
           [tableStyle]="{ 'min-width': '62rem' }" data-testid="pricelist-table">
    <ng-template pTemplate="header">
      <tr>
        <th *ngIf="!productUuid">Product</th>
        <th *ngIf="!supplierUuid">Vendor</th>
        <th class="text-right">Min qty</th>
        <th class="text-right">Price</th>
        <th>Currency</th>
        <th>Valid period</th>
        <th>Date basis</th>
        <th class="text-right">Priority</th>
        <th>Active</th>
        <th style="width:6rem" *ngIf="canEdit"></th>
      </tr>
    </ng-template>
    <ng-template pTemplate="body" let-r>
      <tr [class.pg-off]="!r.isActive" [attr.data-testid]="'pl-row-' + r.uuid">
        <td *ngIf="!productUuid"><strong>{{ r.productName }}</strong>
          <div class="sf-mu sf-sm"><span class="sf-mono" *ngIf="r.productSku">{{ r.productSku }}</span><span *ngIf="r.variantName"> · {{ r.variantName }}</span></div></td>
        <td *ngIf="!supplierUuid"><strong>{{ r.supplierName }}</strong>
          <div class="sf-mu sf-sm" *ngIf="r.vendorProductCode || r.vendorProductName"><span class="sf-mono">{{ r.vendorProductCode }}</span> {{ r.vendorProductName }}</div></td>
        <td class="text-right sf-num">{{ r.minQuantity | number:'1.0-4' }}</td>
        <td class="text-right sf-num"><strong>{{ r.unitPrice | number:'1.2-4' }}</strong></td>
        <td>{{ r.currencyCode }}</td>
        <td>{{ r.validFrom | date:'dd MMM yyyy' }} – {{ r.validTo | date:'dd MMM yyyy' }}</td>
        <td>{{ r.pricingDateBasisLabel || (r.pricingDateBasis === 1 ? 'Delivery Date' : 'Order Date') }}</td>
        <td class="text-right sf-num">{{ r.priority }}</td>
        <td><span class="sf-pill" [ngClass]="r.isActive ? 'ok' : 'in'">{{ r.isActive ? 'Active' : 'Inactive' }}</span></td>
        <td class="text-right" *ngIf="canEdit">
          <p-button icon="pi pi-pencil" size="small" [text]="true" severity="secondary" pTooltip="Edit" (onClick)="openEdit(r)" [attr.data-testid]="'pl-edit-' + r.uuid"></p-button>
          <p-button *ngIf="r.isActive" icon="pi pi-ban" size="small" [text]="true" severity="danger" pTooltip="Deactivate" (onClick)="confirmDeactivate(r)" [attr.data-testid]="'pl-deactivate-' + r.uuid"></p-button>
        </td>
      </tr>
    </ng-template>
    <ng-template pTemplate="emptymessage">
      <tr><td [attr.colspan]="colspan" class="sf-empty">No vendor prices yet.<ng-container *ngIf="canEdit"> Use "Add price" to create one.</ng-container></td></tr>
    </ng-template>
  </p-table>
</div>
<div class="sf-mu sf-sm pg-info" data-testid="pg-info"><i class="pi pi-info-circle"></i> Prices are resolved by priority. When several records match, the lowest priority number wins.</div>

<app-pricelist-dialog [(visible)]="dialogVisible" [item]="editing" [fixedProductUuid]="productUuid" [fixedProductName]="productName"
                      [fixedSupplierUuid]="supplierUuid" [fixedSupplierName]="supplierName" [variants]="variants"
                      (saved)="onSaved($event)" (conflict)="reload()"></app-pricelist-dialog>`,
  styles: [`
    .pg-head { display: flex; align-items: center; gap: .75rem; flex-wrap: wrap; margin-bottom: .6rem; }
    .pg-title { margin: 0; font-size: 1rem; }
    .pg-filters { flex: 1; }
    .pg-off { opacity: .6; }
    .pg-info { margin-top: .6rem; }
  `]
})
export class PricelistGridComponent implements OnInit, OnChanges, OnDestroy {
  /** Fix the product (Product tab). */
  @Input() productUuid: string | null = null;
  @Input() productName: string | null = null;
  /** Fix the vendor (Supplier tab). */
  @Input() supplierUuid: string | null = null;
  @Input() supplierName: string | null = null;
  @Input() variants: PricelistVariantOption[] = [];
  /** Add / edit / deactivate (the write permission). */
  @Input() canEdit = false;
  /** Search + product / vendor filters (the standalone page). */
  @Input() showFilters = false;
  @Input() title: string | null = null;
  @Input() pageSize = 20;

  rows: PricelistItem[] = [];
  total = 0;
  page = 1;
  loading = false;
  activeOnly = true;
  search = '';
  filterProduct: string | null = null;
  filterSupplier: string | null = null;
  productOptions: { label: string; value: string }[] = [];
  supplierOptions: { label: string; value: string }[] = [];

  dialogVisible = false;
  editing: PricelistItem | null = null;

  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private started = false;

  constructor(
    private api: SupplierPricelistService,
    private messages: MessageService,
    private confirm: ConfirmationService,
    private inventory: InventoryService,
    private suppliers: SupplierService
  ) {}

  get colspan(): number { return 7 + (this.productUuid ? 0 : 1) + (this.supplierUuid ? 0 : 1) + (this.canEdit ? 1 : 0); }

  ngOnInit(): void {
    this.started = true;
    if (this.showFilters) this.loadFilterOptions();
    this.reload();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (this.started && (changes['productUuid'] || changes['supplierUuid']) && !changes['productUuid']?.firstChange && !changes['supplierUuid']?.firstChange) this.reload();
  }

  ngOnDestroy(): void { if (this.searchTimer) clearTimeout(this.searchTimer); }

  private loadFilterOptions(): void {
    if (!this.productUuid) {
      this.inventory.getProducts({ activeOnly: true, pageSize: 500 }).subscribe({
        next: r => this.productOptions = (r?.result?.data ?? []).map(p => ({ label: p.sku ? `${p.name} (${p.sku})` : p.name, value: p.uuid })),
        error: () => { /* filters are optional */ }
      });
    }
    if (!this.supplierUuid) {
      this.suppliers.getSuppliers({ status: 'ACTIVE', pageSize: 500 }).subscribe({
        next: r => this.supplierOptions = (r?.result?.data ?? []).map(s => ({ label: s.supplierName, value: s.uuid })),
        error: () => { /* filters are optional */ }
      });
    }
  }

  reload(resetPage = true): void {
    if (resetPage) this.page = 1;
    this.loading = true;
    this.api.list({
      productUuid: this.productUuid ?? this.filterProduct,
      supplierUuid: this.supplierUuid ?? this.filterSupplier,
      activeOnly: this.activeOnly,
      search: this.search,
      page: this.page,
      pageSize: this.pageSize
    }).subscribe({
      next: r => { this.loading = false; this.rows = sortPricelist(r.items); this.total = r.total; },
      error: err => {
        this.loading = false; this.rows = []; this.total = 0;
        this.messages.add({ key: 'pricelist', severity: 'error', summary: 'Error', detail: err?.error?.message || 'The vendor prices could not be loaded.' });
      }
    });
  }

  onPage(e: TableLazyLoadEvent): void {
    // the first lazy event fires on init, together with ngOnInit's load — skip the duplicate
    const rows = e.rows ?? this.pageSize;
    const page = Math.floor((e.first ?? 0) / rows) + 1;
    if (page === this.page && rows === this.pageSize) return;
    this.page = page; this.pageSize = rows;
    this.reload(false);
  }

  onSearchChange(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.reload(), 400);
  }

  toggleActive(): void { this.activeOnly = !this.activeOnly; this.reload(); }

  openAdd(): void { this.editing = null; this.dialogVisible = true; }
  openEdit(r: PricelistItem): void { this.editing = r; this.dialogVisible = true; }

  onSaved(result: PricelistSaveResult): void {
    this.messages.add({ key: 'pricelist', severity: 'success', summary: 'Saved', detail: 'The vendor price was saved.' });
    // C6 / BR-11: an overlap warns, the save stands
    if (result?.overlapWarning) {
      this.messages.add({ key: 'pricelist', severity: 'warn', summary: 'Overlapping price', detail: result.overlapWarning, life: 10000 });
    }
    this.reload(false);
  }

  confirmDeactivate(r: PricelistItem): void {
    this.confirm.confirm({
      key: 'pricelist',
      header: 'Deactivate price',
      message: 'Deactivate this price record?',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Deactivate',
      rejectLabel: 'Cancel',
      acceptButtonProps: { severity: 'danger' },
      accept: () => this.deactivate(r)
    });
  }

  deactivate(r: PricelistItem): void {
    this.api.deactivate(r.uuid).subscribe({
      next: () => {
        this.messages.add({ key: 'pricelist', severity: 'success', summary: 'Deactivated', detail: 'The price record is now inactive.' });
        this.reload(false);
      },
      error: err => this.messages.add({ key: 'pricelist', severity: 'error', summary: 'Error', detail: err?.error?.message || 'The price could not be deactivated.' })
    });
  }
}
