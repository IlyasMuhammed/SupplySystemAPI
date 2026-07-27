import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ReactiveFormsModule, FormsModule, FormBuilder, FormGroup, FormArray, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { InputNumberModule } from 'primeng/inputnumber';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { DividerModule } from 'primeng/divider';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { AutoCompleteModule } from 'primeng/autocomplete';
import { CheckboxModule } from 'primeng/checkbox';
import { MessageService } from 'primeng/api';
import { DemandService, PatchPoRequest } from '../../../../services/demand.service';
import { SupplierService, SupplierListItemModel } from '../../../../services/supplier.service';
import { InventoryService, ProductListItemModel, WarehouseModel } from '../../../../services/inventory.service';

@Component({
  selector: 'app-po-edit',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule, FormsModule,
    ButtonModule, InputTextModule, TextareaModule, InputNumberModule,
    DropdownModule, CalendarModule, DividerModule, ToastModule,
    TooltipModule, AutoCompleteModule, CheckboxModule
  ],
  templateUrl: './po-edit.component.html',
  styleUrls: ['./po-edit.component.scss'],
  providers: [MessageService]
})
export class PoEditComponent implements OnInit {
  uuid = '';
  form!: FormGroup;
  isLoading = true;
  isSubmitting = false;
  minDate = new Date();

  supplierSuggestions: SupplierListItemModel[] = [];
  supplierAuto: any = null;

  productOptions: { label: string; value: string }[] = [];
  private productsMap = new Map<string, ProductListItemModel>();
  loadingProducts = false;

  warehouseOptions: { label: string; value: string }[] = [];
  private warehousesMap = new Map<string, string>();
  loadingWarehouses = false;

  // UOM options from FSD Section 6.5 — kept identical across every line-item form (PR/Quotation/PO)
  // so a value copied from one document's lines always matches an option in the next.
  uomOptions = [
    { label: 'Each (EA)',    value: 'EA'   },
    { label: 'Kilogram (KG)', value: 'KG'  },
    { label: 'Litre (LTR)',  value: 'LTR'  },
    { label: 'Box (BOX)',    value: 'BOX'  },
    { label: 'Set (SET)',    value: 'SET'  },
    { label: 'Meter (MTR)',  value: 'MTR'  },
    { label: 'Packet (PKT)', value: 'PKT'  },
    { label: 'Pair (PAIR)',  value: 'PAIR' }
  ];

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private fb: FormBuilder,
    private demandService: DemandService,
    private supplierService: SupplierService,
    private inventoryService: InventoryService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.buildForm();
    this.loadProducts();
    this.loadWarehouses();
    this.route.params.subscribe(p => { this.uuid = p['uuid']; this.load(); });
  }

  private loadWarehouses() {
    this.loadingWarehouses = true;
    this.inventoryService.getWarehouses().subscribe({
      next: res => {
        this.loadingWarehouses = false;
        const data = res?.result ?? [];
        this.warehousesMap.clear();
        this.warehouseOptions = data
          .filter((w: WarehouseModel) => w.isActive)
          .map((w: WarehouseModel) => {
            this.warehousesMap.set(w.uuid, w.name);
            return { label: `${w.code} — ${w.name}`, value: w.uuid };
          });
      },
      error: () => { this.loadingWarehouses = false; }
    });
  }

  private loadProducts() {
    this.loadingProducts = true;
    this.inventoryService.getProducts({ activeOnly: true, pageSize: 500 }).subscribe({
      next: res => {
        this.loadingProducts = false;
        const data = res?.result?.data ?? [];
        this.productsMap.clear();
        this.productOptions = data.map(p => {
          this.productsMap.set(p.uuid, p);
          return { label: p.name, value: p.uuid };
        });
      },
      error: () => { this.loadingProducts = false; }
    });
  }

  onProductChange(i: number) {
    const uuid = this.lines.at(i).get('productId')?.value;
    if (!uuid) return;
    const p = this.productsMap.get(uuid);
    if (!p) return;
    this.lines.at(i).patchValue({
      itemDescription: p.name,
      specification:   (p as any).description ?? '',
      unitOfMeasure:   p.uomCode ?? null,
      unitPrice:       p.unitCost ?? 0
    });
  }

  private buildForm() {
    this.form = this.fb.group({
      supplierName:          ['', Validators.required],
      supplierId:            [''],
      title:                 [''],
      deliveryDate:          [null],
      deliveryWarehouseId:   [null],
      deliveryWarehouseName: [''],
      notes:                 [''],
      lines:                 this.fb.array([this.newLine()])
    });
  }

  get lines(): FormArray { return this.form.get('lines') as FormArray; }

  newLine(): FormGroup {
    return this.fb.group({
      productId:       [null],
      itemDescription: ['', Validators.required],
      specification:   [''],
      unitOfMeasure:   [null],
      quantity:        [1,  [Validators.required, Validators.min(0.0001), Validators.max(999999)]],
      unitPrice:       [0,  [Validators.required, Validators.min(0)]],
      warehouseId:     [null],
      warehouseName:   [''],
      requiredDate:    [null],
      lineNotes:       [''],
      budgetCode:      [''],
      requiresInspection: [true]
    });
  }

  onHeaderWarehouseChange(event: any) {
    const name = this.warehousesMap.get(event.value) ?? '';
    this.form.patchValue({ deliveryWarehouseName: name });
  }

  onLineWarehouseChange(i: number, event: any) {
    const name = this.warehousesMap.get(event.value) ?? '';
    this.lines.at(i).patchValue({ warehouseName: name });
  }

  get headerWarehouseName(): string {
    const id = this.form.get('deliveryWarehouseId')?.value;
    return id ? (this.warehousesMap.get(id) ?? '') : '';
  }

  lineEffectiveWarehousePlaceholder(i: number): string {
    return this.headerWarehouseName ? `Default: ${this.headerWarehouseName}` : 'Same as PO default…';
  }

  addLine()             { this.lines.push(this.newLine()); }
  removeLine(i: number) { if (this.lines.length > 1) this.lines.removeAt(i); }

  get estimatedTotal(): number {
    return this.lines.controls.reduce((s, c) =>
      s + (c.get('quantity')?.value ?? 0) * (c.get('unitPrice')?.value ?? 0), 0);
  }

  searchSuppliers(event: any) {
    this.supplierService.getSuppliers({ search: event.query, page: 1, pageSize: 10 }).subscribe({
      next: (res) => { this.supplierSuggestions = res.result?.data ?? []; },
      error: () => { this.supplierSuggestions = []; }
    });
  }

  onSupplierChange(val: any) {
    if (val && typeof val === 'object') {
      this.form.patchValue({ supplierId: val.uuid, supplierName: val.supplierName });
    } else {
      this.form.patchValue({ supplierName: val ?? '', supplierId: '' });
    }
  }

  load() {
    this.isLoading = true;
    this.demandService.getPoById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (!res.success || !res.result) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Purchase order not found.' });
          return;
        }
        const po = res.result;
        if (po.status !== 'DRAFT') {
          this.messageService.add({ severity: 'warn', summary: 'Read-only', detail: `Only DRAFT purchase orders can be edited. Status: ${po.status}` });
          setTimeout(() => this.router.navigate(['/portal/pages/demand/purchase-orders', this.uuid]), 1500);
          return;
        }

        while (this.lines.length) this.lines.removeAt(0);
        (po.lines ?? []).forEach(l => this.lines.push(this.fb.group({
          productId:       [l.productUuid ?? null],
          itemDescription: [l.itemDescription, Validators.required],
          specification:   [l.specification ?? ''],
          unitOfMeasure:   [l.unitOfMeasure ?? null],
          quantity:        [l.quantity, [Validators.required, Validators.min(0.0001), Validators.max(999999)]],
          unitPrice:       [l.unitPrice, [Validators.required, Validators.min(0)]],
          warehouseId:     [l.warehouseId ?? null],
          warehouseName:   [l.warehouseName ?? ''],
          requiredDate:    [l.requiredDate ? new Date(l.requiredDate) : null],
          lineNotes:       [l.lineNotes ?? ''],
          budgetCode:      [l.budgetCode ?? ''],
          requiresInspection: [l.requiresInspection ?? true]
        })));
        if (!this.lines.length) this.lines.push(this.newLine());

        this.supplierAuto = po.supplierName;
        this.form.patchValue({
          supplierName:          po.supplierName,
          supplierId:            po.supplierId,
          title:                 po.title ?? '',
          deliveryDate:          po.deliveryDate ? new Date(po.deliveryDate) : null,
          deliveryWarehouseId:   po.deliveryWarehouseId ?? null,
          deliveryWarehouseName: po.deliveryWarehouseName ?? '',
          notes:                 po.notes ?? ''
        });
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load purchase order.' });
      }
    });
  }

  onSubmit() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.isSubmitting = true;
    const v = this.form.getRawValue();
    const req: PatchPoRequest = {
      supplierName:          v.supplierName,
      supplierId:            v.supplierId            || undefined,
      title:                 v.title                 || undefined,
      deliveryDate:          v.deliveryDate instanceof Date ? v.deliveryDate.toISOString() : v.deliveryDate || undefined,
      deliveryWarehouseId:   v.deliveryWarehouseId   || undefined,
      deliveryWarehouseName: v.deliveryWarehouseName || undefined,
      notes:                 v.notes                 || undefined,
      lines: v.lines.map((l: any) => ({
        productUuid:     l.productId       || undefined,
        itemDescription: l.itemDescription,
        specification:   l.specification   || undefined,
        unitOfMeasure:   l.unitOfMeasure   || undefined,
        quantity:        l.quantity,
        unitPrice:       l.unitPrice,
        warehouseId:     l.warehouseId     || undefined,
        warehouseName:   l.warehouseName   || undefined,
        requiredDate:    l.requiredDate instanceof Date ? l.requiredDate.toISOString() : l.requiredDate || undefined,
        lineNotes:       l.lineNotes       || undefined,
        budgetCode:      l.budgetCode      || undefined,
        requiresInspection: l.requiresInspection
      }))
    };

    this.demandService.patchPo(this.uuid, req).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Purchase order updated.' });
        setTimeout(() => this.router.navigate(['/portal/pages/demand/purchase-orders', this.uuid]), 1200);
      },
      error: (err) => {
        this.isSubmitting = false;
        const detail = err?.error?.message ?? `Failed to update PO. (HTTP ${err?.status ?? 0})`;
        this.messageService.add({ severity: 'error', summary: 'Error', detail });
      }
    });
  }

  isInvalid(name: string): boolean {
    const c = this.form.get(name); return !!(c?.invalid && c.touched);
  }
  lineIsInvalid(i: number, name: string): boolean {
    const c = this.lines.at(i).get(name); return !!(c?.invalid && c.touched);
  }
}
