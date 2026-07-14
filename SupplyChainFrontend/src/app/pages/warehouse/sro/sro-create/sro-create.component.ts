import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router } from '@angular/router';
import { FormsModule, ReactiveFormsModule, FormBuilder, FormGroup, FormArray, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { AutoCompleteModule } from 'primeng/autocomplete';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ProgressSpinnerModule } from 'primeng/progressspinner';
import { MessageModule } from 'primeng/message';
import { MessageService } from 'primeng/api';
import { AutoCompleteCompleteEvent } from 'primeng/autocomplete';
import { WarehouseService, CreateSroRequest, GrnDetailModel } from '../../../../services/warehouse.service';
import { InventoryService, WarehouseModel, ProductListItemModel } from '../../../../services/inventory.service';
import { SupplierService } from '../../../../services/supplier.service';
import { DemandService, PoDetailModel } from '../../../../services/demand.service';

@Component({
  selector: 'app-sro-create',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, ReactiveFormsModule,
    ButtonModule, InputTextModule, TextareaModule, DropdownModule,
    InputNumberModule, AutoCompleteModule, ToastModule, TooltipModule,
    ProgressSpinnerModule, MessageModule
  ],
  templateUrl: './sro-create.component.html',
  styleUrls: ['./sro-create.component.scss'],
  providers: [MessageService]
})
export class SroCreateComponent implements OnInit {
  form!: FormGroup;
  isSubmitting    = false;
  isLoadingLines  = false;
  linesSource: 'grn' | 'po' | 'manual' = 'manual';

  suppliers:  { label: string; value: string }[] = [];
  grns:       { label: string; value: string }[] = [];
  pos:        { label: string; value: string }[] = [];
  warehouses: { label: string; value: string }[] = [];

  // Product autocomplete state — one entry per line
  lineProductSelections: (ProductListItemModel | null)[] = [];
  productSuggestions: ProductListItemModel[] = [];

  sroTypeOptions = [
    { label: 'Post-Receipt Defect', value: 'POST_RECEIPT_DEFECT' },
    { label: 'Wrong Item',          value: 'WRONG_ITEM' },
    { label: 'Overdelivery',        value: 'OVERDELIVERY' }
  ];

  returnReasonOptions = [
    { label: 'Damaged',          value: 'DAMAGED' },
    { label: 'Defective',        value: 'DEFECTIVE' },
    { label: 'Wrong Item',       value: 'WRONG_ITEM' },
    { label: 'Wrong Quantity',   value: 'WRONG_QTY' },
    { label: 'Short Expiry',     value: 'SHORT_EXPIRY' },
    { label: 'Spec Mismatch',    value: 'SPEC_MISMATCH' },
    { label: 'Duplicate',        value: 'DUPLICATE' },
    { label: 'Quality Failure',  value: 'QUALITY_FAIL' },
    { label: 'Other',            value: 'OTHER' }
  ];

  constructor(
    private fb: FormBuilder,
    private router: Router,
    private warehouseService: WarehouseService,
    private inventoryService: InventoryService,
    private demandService: DemandService,
    private supplierService: SupplierService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.form = this.fb.group({
      supplierId:         ['', Validators.required],
      sroType:            ['POST_RECEIPT_DEFECT', Validators.required],
      originalGrnId:      [''],
      originalPoId:       [''],
      warehouseUuid:      ['', Validators.required],
      returnReason:       ['DAMAGED', Validators.required],
      returnReasonDetail: [''],
      notes:              [''],
      lines:              this.fb.array([this.newLine()])
    });
    this.lineProductSelections = [null];
    this.loadSuppliers();
    this.loadGrns();
    this.loadPos();
    this.loadWarehouses();
  }

  // ── Dropdown loaders ──────────────────────────────────────────────────────

  loadSuppliers() {
    this.supplierService.getSuppliers({ pageSize: 200 }).subscribe({
      next: (res) => {
        if (res.success && res.result?.data) {
          this.suppliers = res.result.data.map(s => ({ label: s.supplierName, value: s.uuid }));
        }
      }
    });
  }

  loadGrns() {
    this.warehouseService.getGrns({ status: 'APPROVED', pageSize: 200 }).subscribe({
      next: (res) => {
        const items = res.success && res.result?.data ? res.result.data : [];
        this.grns = [
          { label: '— None —', value: '' },
          ...items.map(g => ({ label: `${g.grnNumber} — ${g.supplierName}`, value: g.uuid }))
        ];
      }
    });
  }

  loadPos() {
    this.demandService.getPos({ status: 'APPROVED', pageSize: 200 }).subscribe({
      next: (res) => {
        const items = res.success && res.result?.data ? res.result.data : [];
        this.pos = [
          { label: '— None —', value: '' },
          ...items.map(p => ({ label: `${p.poNumber} — ${p.supplierName}`, value: p.uuid }))
        ];
      }
    });
  }

  loadWarehouses() {
    this.inventoryService.getWarehouses().subscribe({
      next: (res) => {
        const active = (res.result ?? []).filter((w: WarehouseModel) => w.isActive);
        this.warehouses = active.map(w => ({ label: `${w.name} (${w.code})`, value: w.uuid }));
      }
    });
  }

  // ── Product autocomplete ──────────────────────────────────────────────────

  searchProducts(event: AutoCompleteCompleteEvent) {
    const q = event.query?.trim();
    if (!q || q.length < 2) { this.productSuggestions = []; return; }
    this.inventoryService.getProducts({ search: q, pageSize: 20, activeOnly: true }).subscribe({
      next: (res) => {
        this.productSuggestions = res.success && res.result?.data ? res.result.data : [];
      },
      error: () => { this.productSuggestions = []; }
    });
  }

  onProductSelected(product: ProductListItemModel, lineIndex: number) {
    this.linesArray.at(lineIndex).patchValue({
      productUuid:     product.uuid,
      itemDescription: product.name,
      unitOfMeasure:   product.uomCode || '',
      unitCost:        product.unitCost ?? null
    });
  }

  onProductInputChange(value: any, i: number) {
    if (typeof value === 'string') {
      this.linesArray.at(i).patchValue({ itemDescription: value, productUuid: '' });
    }
  }

  getProductDisplayName(p: ProductListItemModel | null): string {
    return p ? p.name : '';
  }

  // ── GRN selected → fetch lines + auto-fill warehouse ─────────────────────

  onGrnChange(uuid: string) {
    if (!uuid) {
      if (this.linesSource === 'grn') { this.resetToManualLine(); }
      this.form.get('warehouseUuid')?.enable();
      return;
    }
    this.isLoadingLines = true;
    this.warehouseService.getGrnById(uuid).subscribe({
      next: (res) => {
        this.isLoadingLines = false;
        if (res.success && res.result) {
          this.populateLinesFromGrn(res.result);
          if (!this.form.value.supplierId) {
            this.form.patchValue({ supplierId: res.result.supplierId });
          }
          if (res.result.poUuid) {
            this.form.patchValue({ originalPoId: res.result.poUuid });
          }
          if (res.result.warehouseUuid) {
            this.form.patchValue({ warehouseUuid: res.result.warehouseUuid });
            this.form.get('warehouseUuid')?.disable();
          }
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load GRN details.' });
        }
      },
      error: () => {
        this.isLoadingLines = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load GRN details.' });
      }
    });
  }

  populateLinesFromGrn(grn: GrnDetailModel) {
    const receivedLines = grn.lines.filter(l => l.qtyReceived > 0);
    if (!receivedLines.length) {
      this.messageService.add({ severity: 'warn', summary: 'No Lines', detail: 'This GRN has no received lines.' });
      return;
    }
    while (this.linesArray.length) this.linesArray.removeAt(0);
    this.lineProductSelections = [];
    receivedLines.forEach(l => {
      this.linesArray.push(this.fb.group({
        grnLineUuid:     [l.uuid],
        productUuid:     [l.productUuid || ''],
        itemDescription: [l.itemDescription, Validators.required],
        unitOfMeasure:   [l.unitOfMeasure || ''],
        qtyToReturn:     [l.qtyAccepted > 0 ? l.qtyAccepted : l.qtyReceived,
                          [Validators.required, Validators.min(1)]],
        returnReason:    [this.mapGrnReason(l.rejectionReason), Validators.required],
        condition:       [''],
        unitCost:        [l.unitCost ?? null]
      }));
      this.lineProductSelections.push({ name: l.itemDescription } as any);
    });
    this.linesSource = 'grn';
  }

  // ── PO selected → fetch lines ─────────────────────────────────────────────

  onPoChange(uuid: string) {
    if (!uuid) {
      if (this.linesSource === 'po') { this.resetToManualLine(); }
      return;
    }
    if (this.form.value.originalGrnId) return;

    this.isLoadingLines = true;
    this.demandService.getPoById(uuid).subscribe({
      next: (res) => {
        this.isLoadingLines = false;
        if (res.success && res.result) {
          this.populateLinesFromPo(res.result);
          if (!this.form.value.supplierId) {
            this.form.patchValue({ supplierId: res.result.supplierId });
          }
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load PO details.' });
        }
      },
      error: () => {
        this.isLoadingLines = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load PO details.' });
      }
    });
  }

  populateLinesFromPo(po: PoDetailModel) {
    if (!po.lines.length) {
      this.messageService.add({ severity: 'warn', summary: 'No Lines', detail: 'This PO has no lines.' });
      return;
    }
    while (this.linesArray.length) this.linesArray.removeAt(0);
    this.lineProductSelections = [];
    po.lines.forEach(l => {
      this.linesArray.push(this.fb.group({
        grnLineUuid:     [''],
        productUuid:     [''],
        itemDescription: [l.itemDescription, Validators.required],
        unitOfMeasure:   [l.unitOfMeasure || ''],
        qtyToReturn:     [l.quantity, [Validators.required, Validators.min(1)]],
        returnReason:    ['DAMAGED', Validators.required],
        condition:       [''],
        unitCost:        [l.unitPrice ?? null]
      }));
      this.lineProductSelections.push({ name: l.itemDescription } as any);
    });
    this.linesSource = 'po';
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private mapGrnReason(reason?: string): string {
    if (!reason) return 'DAMAGED';
    const r = reason.toLowerCase();
    if (r.includes('wrong item'))  return 'WRONG_ITEM';
    if (r.includes('expiry'))      return 'SHORT_EXPIRY';
    if (r.includes('over'))        return 'WRONG_QTY';
    if (r.includes('defect'))      return 'DEFECTIVE';
    if (r.includes('damage'))      return 'DAMAGED';
    return 'DAMAGED';
  }

  resetToManualLine() {
    while (this.linesArray.length) this.linesArray.removeAt(0);
    this.linesArray.push(this.newLine());
    this.lineProductSelections = [null];
    this.linesSource = 'manual';
  }

  get linesArray(): FormArray { return this.form.get('lines') as FormArray; }

  newLine(): FormGroup {
    return this.fb.group({
      grnLineUuid:     [''],
      productUuid:     [''],
      itemDescription: ['', Validators.required],
      unitOfMeasure:   [''],
      qtyToReturn:     [1, [Validators.required, Validators.min(1)]],
      returnReason:    ['DAMAGED', Validators.required],
      condition:       [''],
      unitCost:        [null]
    });
  }

  addLine() {
    this.linesArray.push(this.newLine());
    this.lineProductSelections.push(null);
  }

  removeLine(i: number) {
    if (this.linesArray.length > 1) {
      this.linesArray.removeAt(i);
      this.lineProductSelections.splice(i, 1);
    }
  }

  submit() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.isSubmitting = true;
    const v = this.form.getRawValue();
    const supplierLabel = this.suppliers.find(s => s.value === v.supplierId)?.label ?? '';
    const req: CreateSroRequest = {
      supplierId:         v.supplierId,
      supplierName:       supplierLabel,
      sroType:            v.sroType,
      originalGrnId:      v.originalGrnId      || undefined,
      originalPoId:       v.originalPoId       || undefined,
      warehouseUuid:      v.warehouseUuid       || undefined,
      returnReason:       v.returnReason,
      returnReasonDetail: v.returnReasonDetail  || undefined,
      notes:              v.notes               || undefined,
      lines: (v.lines as any[]).map(l => ({
        grnLineUuid:     l.grnLineUuid     || undefined,
        productUuid:     l.productUuid     || undefined,
        itemDescription: l.itemDescription,
        unitOfMeasure:   l.unitOfMeasure   || undefined,
        qtyToReturn:     l.qtyToReturn,
        returnReason:    l.returnReason,
        condition:       l.condition       || undefined,
        unitCost:        l.unitCost        ?? undefined
      }))
    };
    this.warehouseService.createSro(req).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Supplier return order created successfully.' });
          setTimeout(() => {
            this.router.navigate(res.result
              ? ['/portal/pages/warehouse/sro', res.result]
              : ['/portal/pages/warehouse/sro']);
          }, 1200);
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isSubmitting = false;
        const detail = err?.error?.result?.exceptionMessage
          || err?.error?.message
          || 'Failed to create supplier return order.';
        this.messageService.add({ severity: 'error', summary: 'Error', detail, life: 8000 });
      }
    });
  }

  get f() { return this.form.controls; }
}
