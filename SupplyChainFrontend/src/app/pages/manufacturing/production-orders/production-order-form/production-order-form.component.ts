import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { CheckboxModule } from 'primeng/checkbox';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import { PRIORITY_OPTIONS, ProductionOrderService } from '../../../../services/production-order.service';
import { InventoryService, ProductListItemModel, ProductVariantModel, WarehouseModel } from '../../../../services/inventory.service';
import { FLOW } from '../../../../shared/flow';

/**
 * A30 §29.3 — raise a production order for a manufactured product. The recipe is snapshotted from
 * the active BOM on save; there is nothing to choose here.
 */
@Component({
  selector: 'app-production-order-form',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule,
    ButtonModule, CalendarModule, CheckboxModule, DropdownModule, InputNumberModule, TextareaModule, ToastModule,
    ...FLOW
  ],
  templateUrl: './production-order-form.component.html',
  styleUrls: ['./production-order-form.component.scss'],
  providers: [MessageService]
})
export class ProductionOrderFormComponent implements OnInit {
  form: FormGroup;
  isLoading = true;
  isSaving = false;

  readonly priorityOptions = PRIORITY_OPTIONS;

  manufacturable: ProductListItemModel[] = [];
  productOptions: { label: string; value: string }[] = [];
  variantOptions: { label: string; value: string | null }[] = [{ label: 'Default variant', value: null }];
  warehouses: WarehouseModel[] = [];
  warehouseOptions: { label: string; value: string | null }[] = [{ label: 'Product default', value: null }];

  constructor(
    private fb: FormBuilder,
    private router: Router,
    private service: ProductionOrderService,
    private inventoryService: InventoryService,
    private messageService: MessageService
  ) {
    this.form = this.fb.group({
      productUuid:         [null, Validators.required],
      productVariantUuid:  [null],
      plannedQuantity:     [null, [Validators.required, Validators.min(0.0001)]],
      warehouseUuid:       [null],
      outputWarehouseUuid: [null],
      requiredDate:        [null, Validators.required],
      plannedStartDate:    [null],
      priority:            [1, Validators.required],
      notes:               [''],
      plan:                [false]
    });
  }

  get outputProductUuid(): string | null { return this.form.get('productUuid')?.value ?? null; }

  ngOnInit(): void {
    forkJoin({
      manufacturable: this.inventoryService.getManufacturableProducts(undefined, 1, 500).pipe(catchError(() => of(null))),
      warehouses:     this.inventoryService.getWarehouses().pipe(catchError(() => of(null)))
    }).subscribe({
      next: ({ manufacturable, warehouses }) => {
        this.manufacturable = manufacturable?.result?.data ?? [];
        this.productOptions = this.manufacturable.map(p => ({ label: `${p.name} (${p.sku})`, value: p.uuid }));
        this.warehouses = ((warehouses?.result ?? []) as WarehouseModel[]).filter(w => w.isActive !== false);
        this.warehouseOptions = [{ label: 'Product default', value: null }, ...this.warehouses.map(w => ({ label: `${w.code} – ${w.name}`, value: w.uuid }))];
        this.isLoading = false;
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load reference data.' });
      }
    });
  }

  onProductChange(): void {
    this.form.patchValue({ productVariantUuid: null });
    this.variantOptions = [{ label: 'Default variant', value: null }];
    const product = this.manufacturable.find(p => p.uuid === this.outputProductUuid);
    if (!product) return;

    this.inventoryService.getProductById(product.id).pipe(catchError(() => of(null))).subscribe(res => {
      const detail = res?.result;
      if (!detail) return;
      const variants = (detail.variants ?? []) as ProductVariantModel[];
      this.variantOptions = [
        { label: 'Default variant', value: null },
        ...variants.filter(v => v.isActive).map(v => ({ label: `${v.variantName} (${v.sku})`, value: v.uuid }))
      ];
      // The product's own default production warehouse pre-selects here; the person can still choose another.
      if (!this.form.get('warehouseUuid')?.value && detail.defaultProductionWarehouseId) {
        const wh = this.warehouses.find(w => w.id === detail.defaultProductionWarehouseId);
        if (wh) this.form.patchValue({ warehouseUuid: wh.uuid });
      }
    });
  }

  get canSave(): boolean { return this.form.valid && !this.isSaving; }

  private isoDate(value: Date | null): string | undefined {
    if (!value) return undefined;
    return new Date(Date.UTC(value.getFullYear(), value.getMonth(), value.getDate())).toISOString();
  }

  save(): void {
    if (!this.canSave) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.isSaving = true;

    this.service.create({
      productUuid: v.productUuid,
      productVariantUuid: v.productVariantUuid || undefined,
      plannedQuantity: v.plannedQuantity,
      warehouseUuid: v.warehouseUuid || undefined,
      outputWarehouseUuid: v.outputWarehouseUuid || undefined,
      requiredDate: this.isoDate(v.requiredDate)!,
      plannedStartDate: this.isoDate(v.plannedStartDate),
      priority: v.priority,
      notes: v.notes || undefined,
      plan: !!v.plan
    }).subscribe({
      next: (res) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'success', summary: 'Created', detail: v.plan ? 'Production order created and planned.' : 'Production order drafted.' });
        this.router.navigate(['/portal/pages/manufacturing/production-orders', res.result]);
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Not created', detail: err.error?.message || 'The production order could not be created.' });
      }
    });
  }

  cancel(): void { this.router.navigate(['/portal/pages/manufacturing/production-orders']); }
}
