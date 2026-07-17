import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { DividerModule } from 'primeng/divider';
import { MessageService } from 'primeng/api';
import { forkJoin } from 'rxjs';
import { MaterialService, CreateMirRequest, PrLineSearchResult } from '../../../../services/material.service';
import { InventoryService, ProductStockModel } from '../../../../services/inventory.service';

interface MirLine {
  productUuid: string;
  requestedQty: number;
  purpose: string;
  notes: string;
  stockItems: ProductStockModel[];
  selectedWarehouseId: number | null;
  maxQty: number | null;
  isLoadingStock: boolean;
  prLineId: string | null;
  prLineLabel: string | null;
  prSearchResults: PrLineSearchResult[];
  showPrResults: boolean;
  isFetchingPr: boolean;
  prFetchAttempted: boolean;
}

@Component({
  selector: 'app-mir-create',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, InputTextModule, InputNumberModule,
    DropdownModule, CalendarModule, TextareaModule,
    ToastModule, DividerModule
  ],
  templateUrl: './mir-create.component.html',
  styleUrls: ['./mir-create.component.scss'],
  providers: [MessageService]
})
export class MirCreateComponent implements OnInit {
  isSaving  = false;
  isLoading = true;

  projectOptions: { label: string; value: string }[] = [];
  productOptions: { label: string; value: string; uom?: string; cost?: number }[] = [];

  private _productIdByUuid = new Map<string, number>();

  requestType    = 'PROJECT';
  projectUuid    = '';
  department     = '';
  maintenanceRef = '';
  requiredDate: Date | null = null;
  priority       = 'MEDIUM';
  purpose        = '';
  notes          = '';

  lines: MirLine[] = [ this.newLine() ];

  typeOptions = [
    { label: 'Project',     value: 'PROJECT' },
    { label: 'Department',  value: 'DEPARTMENT' },
    { label: 'Maintenance', value: 'MAINTENANCE' }
  ];

  priorityOptions = [
    { label: 'Low',    value: 'LOW' },
    { label: 'Medium', value: 'MEDIUM' },
    { label: 'High',   value: 'HIGH' },
    { label: 'Urgent', value: 'URGENT' }
  ];

  constructor(
    private materialService: MaterialService,
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private router: Router
  ) {}

  ngOnInit() {
    forkJoin({
      projects: this.materialService.getProjects({ status: 'ACTIVE', pageSize: 200 }),
      products: this.inventoryService.getProducts({ activeOnly: true, pageSize: 500 })
    }).subscribe({
      next: ({ projects, products }) => {
        this.isLoading = false;
        if (projects.success && projects.result) {
          this.projectOptions = (projects.result.data ?? []).map(p => ({
            label: `${p.projectCode} — ${p.projectName}`,
            value: p.uuid
          }));
        }
        if (products.success && products.result) {
          this.productOptions = (products.result.data ?? []).map(p => ({
            label: `${p.sku} — ${p.name}`,
            value: p.uuid,
            uom:  p.uomCode ?? undefined,
            cost: p.unitCost ?? undefined
          }));
          products.result.data.forEach(p => this._productIdByUuid.set(p.uuid, p.id));
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load reference data.' });
      }
    });
  }

  newLine(): MirLine {
    return {
      productUuid: '', requestedQty: 1, purpose: '', notes: '',
      stockItems: [], selectedWarehouseId: null, maxQty: null, isLoadingStock: false,
      prLineId: null, prLineLabel: null, prSearchResults: [], showPrResults: false,
      isFetchingPr: false, prFetchAttempted: false
    };
  }

  addLine() {
    this.lines.push(this.newLine());
  }

  removeLine(i: number) {
    if (this.lines.length > 1) this.lines.splice(i, 1);
  }

  onProductChange(i: number, uuid: string) {
    const line = this.lines[i];
    line.stockItems = [];
    line.selectedWarehouseId = null;
    line.maxQty = null;
    this.clearPrLine(i);

    const productId = this._productIdByUuid.get(uuid);
    if (!productId) return;

    line.isLoadingStock = true;
    this.inventoryService.getProductStock(productId).subscribe({
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

  fetchPrLines(i: number) {
    const line = this.lines[i];
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

  selectPrLine(i: number, result: PrLineSearchResult) {
    const line = this.lines[i];
    line.prLineId    = result.prLineId;
    line.prLineLabel = result.prNumber;
    line.showPrResults = false;
  }

  clearPrLine(i: number) {
    const line = this.lines[i];
    line.prLineId    = null;
    line.prLineLabel = null;
    line.prSearchResults = [];
    line.showPrResults = false;
    line.prFetchAttempted = false;
  }

  onWarehouseChange(i: number, warehouseId: number) {
    const line = this.lines[i];
    const stock = line.stockItems.find(s => s.warehouseId === warehouseId);
    line.maxQty = stock ? stock.qtyAvailable : null;
    if (line.maxQty != null && line.requestedQty > line.maxQty) {
      line.requestedQty = line.maxQty;
    }
  }

  getWarehouseOptions(i: number): { label: string; value: number }[] {
    return this.lines[i].stockItems.map(s => ({
      label: `${s.warehouseName}  (Available: ${s.qtyAvailable})`,
      value: s.warehouseId
    }));
  }

  getEstimatedLineValue(productUuid: string, qty: number): number {
    const opt = this.productOptions.find(p => p.value === productUuid);
    return opt?.cost ? opt.cost * qty : 0;
  }

  getTotalEstimated(): number {
    return this.lines.reduce((sum, l) => sum + this.getEstimatedLineValue(l.productUuid, l.requestedQty), 0);
  }

  onSubmit() {
    if (this.requestType === 'PROJECT' && !this.projectUuid) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Project is required for type PROJECT.' }); return;
    }
    if (this.requestType === 'DEPARTMENT' && !this.department.trim()) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Department is required.' }); return;
    }
    if (this.requestType === 'MAINTENANCE' && !this.maintenanceRef.trim()) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Maintenance reference is required.' }); return;
    }
    const invalidLines = this.lines.filter(l => !l.productUuid || l.requestedQty <= 0);
    if (invalidLines.length > 0) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'All lines must have a product and quantity > 0.' }); return;
    }
    const unselectedWarehouse = this.lines.filter(l => l.stockItems.length > 0 && !l.selectedWarehouseId);
    if (unselectedWarehouse.length > 0) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Please select a warehouse for all lines with available stock.' }); return;
    }

    this.isSaving = true;
    const req: CreateMirRequest = {
      requestType:    this.requestType,
      projectUuid:    this.requestType === 'PROJECT'     ? this.projectUuid    : undefined,
      department:     this.requestType === 'DEPARTMENT'  ? this.department     : undefined,
      maintenanceRef: this.requestType === 'MAINTENANCE' ? this.maintenanceRef : undefined,
      requiredDate:   this.requiredDate ? this.requiredDate.toISOString() : undefined,
      priority:       this.priority,
      purpose:        this.purpose || undefined,
      notes:          this.notes   || undefined,
      lines:          this.lines.map(l => ({
        productUuid:  l.productUuid,
        requestedQty: l.requestedQty,
        warehouseId:  l.selectedWarehouseId ?? undefined,
        purpose:      l.purpose  || undefined,
        notes:        l.notes    || undefined,
        prLineId:     l.prLineId ?? undefined
      }))
    };

    this.materialService.createMir(req).subscribe({
      next: (res) => {
        this.isSaving = false;
        const uuid = res.result;
        this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Material Issue Request created.' });
        setTimeout(() => this.router.navigate(['/portal/pages/material/mir', uuid]), 1200);
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to create MIR.' });
      }
    });
  }
}
