import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TableModule } from 'primeng/table';
import { InputTextModule } from 'primeng/inputtext';
import { TooltipModule } from 'primeng/tooltip';
import { DividerModule } from 'primeng/divider';
import { DropdownModule } from 'primeng/dropdown';
import { CheckboxModule } from 'primeng/checkbox';
import { DialogModule } from 'primeng/dialog';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { MessageService, ConfirmationService } from 'primeng/api';
import {
  InventoryService,
  WarehouseModel,
  StockLevelModel,
  CategoryModel,
  StockLevelFilter,
  WarehouseStructureModel,
  ZoneNodeModel,
  RackNodeModel,
  ShelfNodeModel,
  BinNodeModel,
  StructureConflictResult,
  CreateZoneRequest,
  UpdateZoneRequest,
  CreateRackRequest,
  UpdateRackRequest,
  CreateShelfRequest,
  UpdateShelfRequest,
  CreateBinRequest,
  UpdateBinRequest
} from '../../../../services/inventory.service';

type NodeLevel = 'zone' | 'rack' | 'shelf' | 'bin';
type DialogMode = 'add' | 'edit';

interface NodeForm {
  // zone
  zoneName?: string;
  zoneCode?: string;
  zoneDescription?: string;
  // rack
  rackCode?: string;
  rackName?: string;
  // shelf
  shelfCode?: string;
  shelfLevel?: string;
  // bin
  binCode?: string;
  binDescription?: string;
}

@Component({
  selector: 'app-warehouse-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, CardModule, TagModule, ToastModule,
    TableModule, InputTextModule, TooltipModule, DividerModule,
    DropdownModule, CheckboxModule, DialogModule, ConfirmDialogModule
  ],
  templateUrl: './warehouse-detail.component.html',
  styleUrls: ['./warehouse-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class WarehouseDetailComponent implements OnInit {
  warehouseId = 0;
  warehouse: WarehouseModel | null = null;
  // Computed once when the warehouse loads, not a getter — bypassSecurityTrustResourceUrl()
  // returns a new object identity on every call, and a getter re-invokes it on every Angular
  // change-detection cycle (i.e. any click anywhere on the page), which made the map iframe's
  // [src] binding look "changed" each time and reload.
  mapEmbedUrl: SafeResourceUrl | null = null;
  stockLevels: StockLevelModel[] = [];

  // Tabs
  activeTab: 'stock' | 'structure' = 'stock';

  // Filters
  selectedCategory: number | null = null;
  belowReorderOnly = false;
  includeZeroStock = false;

  // Pagination
  totalRecords = 0;
  currentPage = 1;
  pageSize = 20;

  // State
  isLoading = true;
  isLoadingStock = false;

  categoryOptions: { label: string; value: number | null }[] = [
    { label: 'All Categories', value: null }
  ];

  // ── Structure state ──────────────────────────────────────────────────────────
  structure: WarehouseStructureModel | null = null;
  isLoadingStructure = false;
  expandedZones  = new Set<number>();
  expandedRacks  = new Set<number>();
  expandedShelves = new Set<number>();

  // Node dialog
  showNodeDialog = false;
  nodeDialogMode: DialogMode = 'add';
  nodeDialogLevel: NodeLevel = 'zone';
  nodeDialogParentId = 0;
  nodeDialogEntityId = 0;
  nodeDialogSaving = false;
  nodeForm: NodeForm = {};

  get nodeDialogTitle(): string {
    const action = this.nodeDialogMode === 'add' ? 'Add' : 'Edit';
    const lvl = this.nodeDialogLevel.charAt(0).toUpperCase() + this.nodeDialogLevel.slice(1);
    return `${action} ${lvl}`;
  }

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    private sanitizer: DomSanitizer
  ) {}

  ngOnInit() {
    this.warehouseId = +this.route.snapshot.paramMap.get('id')!;
    this.loadWarehouse();
    this.loadCategories();
  }

  switchTab(tab: 'stock' | 'structure') {
    this.activeTab = tab;
    if (tab === 'structure' && !this.structure && !this.isLoadingStructure) {
      this.loadStructure();
    }
  }

  // ── Load warehouse info ───────────────────────────────────────────────────

  private loadWarehouse() {
    this.isLoading = true;
    this.inventoryService.getWarehouses().subscribe({
      next: (res) => {
        if (res.success) {
          this.warehouse = (res.result ?? []).find((w: WarehouseModel) => w.id === this.warehouseId) ?? null;
          if (!this.warehouse) {
            this.messageService.add({ severity: 'warn', summary: 'Not Found', detail: 'Warehouse not found.' });
          }
          this.mapEmbedUrl = this.buildMapEmbedUrl();
        }
        this.loadStock();
      },
      error: (err) => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to load warehouse.' });
      }
    });
  }

  private loadCategories() {
    this.inventoryService.getCategories().subscribe({
      next: (res) => {
        if (res.success) {
          const cats: CategoryModel[] = res.result ?? [];
          this.categoryOptions = [
            { label: 'All Categories', value: null },
            ...cats.filter(c => c.isActive).map(c => ({ label: c.name, value: c.id }))
          ];
        }
      },
      error: () => { /* non-critical */ }
    });
  }

  // ── Load stock ─────────────────────────────────────────────────────────────

  loadStock() {
    this.isLoadingStock = true;
    const filter: StockLevelFilter = {
      categoryId:      this.selectedCategory ?? undefined,
      belowReorderOnly: this.belowReorderOnly || undefined,
      includeZeroStock: this.includeZeroStock || undefined,
      page:     this.currentPage,
      pageSize: this.pageSize
    };
    this.inventoryService.getWarehouseStock(this.warehouseId, filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.isLoadingStock = false;
        if (res.success) {
          this.stockLevels  = res.result?.data ?? [];
          this.totalRecords = res.result?.totalRecords ?? 0;
        } else {
          this.stockLevels  = [];
          this.totalRecords = 0;
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.isLoadingStock = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to load stock.' });
      }
    });
  }

  onFilterChange() {
    this.currentPage = 1;
    this.loadStock();
  }

  onPageChange(event: { first: number; rows: number }) {
    this.currentPage = Math.floor(event.first / event.rows) + 1;
    this.pageSize = event.rows;
    this.loadStock();
  }

  // ── Structure ──────────────────────────────────────────────────────────────

  loadStructure() {
    this.isLoadingStructure = true;
    this.inventoryService.getWarehouseStructure(this.warehouseId).subscribe({
      next: (res) => {
        this.isLoadingStructure = false;
        this.structure = res.success ? res.result : null;
      },
      error: () => {
        this.isLoadingStructure = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load warehouse structure.' });
      }
    });
  }

  toggleZone(zoneId: number) {
    if (this.expandedZones.has(zoneId)) this.expandedZones.delete(zoneId);
    else this.expandedZones.add(zoneId);
  }

  toggleRack(rackId: number) {
    if (this.expandedRacks.has(rackId)) this.expandedRacks.delete(rackId);
    else this.expandedRacks.add(rackId);
  }

  toggleShelf(shelfId: number) {
    if (this.expandedShelves.has(shelfId)) this.expandedShelves.delete(shelfId);
    else this.expandedShelves.add(shelfId);
  }

  // ── Dialog open helpers ────────────────────────────────────────────────────

  openAddZone() {
    this.nodeDialogMode  = 'add';
    this.nodeDialogLevel = 'zone';
    this.nodeDialogParentId = this.warehouseId;
    this.nodeForm = {};
    this.showNodeDialog = true;
  }

  openAddRack(zone: ZoneNodeModel) {
    this.nodeDialogMode  = 'add';
    this.nodeDialogLevel = 'rack';
    this.nodeDialogParentId = zone.id;
    this.nodeForm = {};
    this.showNodeDialog = true;
  }

  openAddShelf(rack: RackNodeModel) {
    this.nodeDialogMode  = 'add';
    this.nodeDialogLevel = 'shelf';
    this.nodeDialogParentId = rack.id;
    this.nodeForm = {};
    this.showNodeDialog = true;
  }

  openAddBin(shelf: ShelfNodeModel) {
    this.nodeDialogMode  = 'add';
    this.nodeDialogLevel = 'bin';
    this.nodeDialogParentId = shelf.id;
    this.nodeDialogEntityId = 0;
    this.nodeForm = {};
    this.showNodeDialog = true;
  }

  openAddLegacyBin(zone: ZoneNodeModel) {
    this.nodeDialogMode  = 'add';
    this.nodeDialogLevel = 'bin';
    this.nodeDialogParentId = zone.id;
    this.nodeDialogEntityId = -1; // sentinel = legacy (zone-direct)
    this.nodeForm = {};
    this.showNodeDialog = true;
  }

  openEditZone(zone: ZoneNodeModel) {
    this.nodeDialogMode  = 'edit';
    this.nodeDialogLevel = 'zone';
    this.nodeDialogEntityId = zone.id;
    this.nodeForm = { zoneName: zone.name, zoneCode: zone.code, zoneDescription: zone.description };
    this.showNodeDialog = true;
  }

  openEditRack(rack: RackNodeModel) {
    this.nodeDialogMode  = 'edit';
    this.nodeDialogLevel = 'rack';
    this.nodeDialogEntityId = rack.id;
    this.nodeForm = { rackCode: rack.rackCode, rackName: rack.rackName };
    this.showNodeDialog = true;
  }

  openEditShelf(shelf: ShelfNodeModel) {
    this.nodeDialogMode  = 'edit';
    this.nodeDialogLevel = 'shelf';
    this.nodeDialogEntityId = shelf.id;
    this.nodeForm = { shelfCode: shelf.shelfCode, shelfLevel: shelf.shelfLevel };
    this.showNodeDialog = true;
  }

  openEditBin(bin: BinNodeModel) {
    this.nodeDialogMode  = 'edit';
    this.nodeDialogLevel = 'bin';
    this.nodeDialogEntityId = bin.id;
    this.nodeForm = { binCode: bin.code, binDescription: bin.description };
    this.showNodeDialog = true;
  }

  // ── Save dialog ────────────────────────────────────────────────────────────

  saveNode() {
    this.nodeDialogSaving = true;
    const done = () => { this.nodeDialogSaving = false; this.showNodeDialog = false; this.loadStructure(); };
    const fail = (msg: string) => { this.nodeDialogSaving = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: msg }); };

    if (this.nodeDialogMode === 'add') {
      switch (this.nodeDialogLevel) {
        case 'zone': {
          const req: CreateZoneRequest = { name: this.nodeForm.zoneName!, code: this.nodeForm.zoneCode, description: this.nodeForm.zoneDescription };
          this.inventoryService.createZone(this.nodeDialogParentId, req).subscribe({ next: () => done(), error: (e) => fail(e.error?.message || 'Failed to create zone.') });
          break;
        }
        case 'rack': {
          const req: CreateRackRequest = { rackCode: this.nodeForm.rackCode!, rackName: this.nodeForm.rackName };
          this.inventoryService.createRack(this.nodeDialogParentId, req).subscribe({ next: () => done(), error: (e) => fail(e.error?.message || 'Failed to create rack.') });
          break;
        }
        case 'shelf': {
          const req: CreateShelfRequest = { shelfCode: this.nodeForm.shelfCode!, shelfLevel: this.nodeForm.shelfLevel };
          this.inventoryService.createShelf(this.nodeDialogParentId, req).subscribe({ next: () => done(), error: (e) => fail(e.error?.message || 'Failed to create shelf.') });
          break;
        }
        case 'bin': {
          const req: CreateBinRequest = { code: this.nodeForm.binCode!, description: this.nodeForm.binDescription };
          if (this.nodeDialogEntityId === -1) {
            this.inventoryService.createLegacyBin(this.nodeDialogParentId, req).subscribe({ next: () => done(), error: (e) => fail(e.error?.message || 'Failed to create bin.') });
          } else {
            this.inventoryService.createStructuredBin(this.nodeDialogParentId, req).subscribe({ next: () => done(), error: (e) => fail(e.error?.message || 'Failed to create bin.') });
          }
          break;
        }
      }
    } else {
      switch (this.nodeDialogLevel) {
        case 'zone': {
          const req: UpdateZoneRequest = { name: this.nodeForm.zoneName, code: this.nodeForm.zoneCode, description: this.nodeForm.zoneDescription };
          this.inventoryService.updateZone(this.nodeDialogEntityId, req).subscribe({ next: () => done(), error: (e) => fail(e.error?.message || 'Failed to update zone.') });
          break;
        }
        case 'rack': {
          const req: UpdateRackRequest = { rackCode: this.nodeForm.rackCode, rackName: this.nodeForm.rackName };
          this.inventoryService.updateRack(this.nodeDialogEntityId, req).subscribe({ next: () => done(), error: (e) => fail(e.error?.message || 'Failed to update rack.') });
          break;
        }
        case 'shelf': {
          const req: UpdateShelfRequest = { shelfCode: this.nodeForm.shelfCode, shelfLevel: this.nodeForm.shelfLevel };
          this.inventoryService.updateShelf(this.nodeDialogEntityId, req).subscribe({ next: () => done(), error: (e) => fail(e.error?.message || 'Failed to update shelf.') });
          break;
        }
        case 'bin': {
          const req: UpdateBinRequest = { code: this.nodeForm.binCode, description: this.nodeForm.binDescription };
          this.inventoryService.updateBin(this.nodeDialogEntityId, req).subscribe({ next: () => done(), error: (e) => fail(e.error?.message || 'Failed to update bin.') });
          break;
        }
      }
    }
  }

  // ── Deactivate ─────────────────────────────────────────────────────────────

  deactivateNode(level: NodeLevel, id: number, label: string) {
    this.confirmationService.confirm({
      message: `Deactivate ${label}? This cannot be undone unless re-activated via admin.`,
      header: 'Confirm Deactivation',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.doDeactivate(level, id)
    });
  }

  private doDeactivate(level: NodeLevel, id: number) {
    const handleConflict = (e: any) => {
      const conflict: StructureConflictResult | null = e.error?.result ?? null;
      if (e.status === 409 && conflict) {
        this.messageService.add({
          severity: 'warn',
          summary: 'Cannot Deactivate',
          detail: `${conflict.childCount} active ${conflict.childType} must be deactivated first.`,
          life: 6000
        });
      } else {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: e.error?.message || 'Deactivation failed.' });
      }
    };
    const done = () => { this.messageService.add({ severity: 'success', summary: 'Deactivated' }); this.loadStructure(); };

    switch (level) {
      case 'zone':  this.inventoryService.deactivateZone(id).subscribe({ next: () => done(), error: handleConflict }); break;
      case 'rack':  this.inventoryService.deactivateRack(id).subscribe({ next: () => done(), error: handleConflict }); break;
      case 'shelf': this.inventoryService.deactivateShelf(id).subscribe({ next: () => done(), error: handleConflict }); break;
      case 'bin':   this.inventoryService.deactivateBin(id).subscribe({ next: () => done(), error: handleConflict }); break;
    }
  }

  // ── Availability colour helper ─────────────────────────────────────────────

  getAvailabilityClass(item: StockLevelModel): string {
    if (item.qtyAvailable <= 0) return 'qty-zero';
    if (item.isBelowReorder)    return 'qty-low';
    return 'qty-ok';
  }

  // ── Location helper ────────────────────────────────────────────────────────

  getLocation(): string {
    if (!this.warehouse) return '—';
    const parts = [this.warehouse.city, this.warehouse.country].filter(Boolean);
    return parts.length ? parts.join(', ') : '—';
  }

  get hasLocation(): boolean {
    return !!(this.warehouse?.googleMapsUrl ||
              (this.warehouse?.latitude != null && this.warehouse?.longitude != null));
  }

  private buildMapEmbedUrl(): SafeResourceUrl | null {
    if (!this.warehouse) return null;
    const lat = this.warehouse.latitude;
    const lng = this.warehouse.longitude;
    if (lat == null || lng == null) return null;
    return this.sanitizer.bypassSecurityTrustResourceUrl(
      `https://maps.google.com/maps?q=${lat},${lng}&z=15&output=embed`
    );
  }

  get mapsOpenUrl(): string {
    if (!this.warehouse) return '';
    if (this.warehouse.googleMapsUrl) return this.warehouse.googleMapsUrl;
    const lat = this.warehouse.latitude;
    const lng = this.warehouse.longitude;
    if (lat != null && lng != null) return `https://maps.google.com/?q=${lat},${lng}`;
    return '';
  }

  goBack() {
    this.router.navigate(['/portal/pages/inventory/warehouses']);
  }
}
