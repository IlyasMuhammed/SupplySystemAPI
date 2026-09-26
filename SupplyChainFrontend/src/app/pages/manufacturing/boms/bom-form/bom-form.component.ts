import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormArray, FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { CheckboxModule } from 'primeng/checkbox';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import { BomDetail, BomLineRequest, BomService } from '../../../../services/bom.service';
import { InventoryService, ProductListItemModel, ProductVariantModel, WarehouseModel } from '../../../../services/inventory.service';
import {
  ProductVariantPickerComponent, VariantPickerSelection
} from '../../../../shared/product-variant-picker/product-variant-picker.component';

/**
 * A30 §29.2 — create a recipe, or edit one that is still a draft (or was rejected). The output
 * product comes from the manufacturable list; every input comes through the variant picker on its
 * PRODUCTION channel, so only variants marked available for production are offered (decision D2).
 */
@Component({
  selector: 'app-bom-form',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule,
    ButtonModule, CalendarModule, CheckboxModule, DropdownModule, InputNumberModule, InputTextModule, TextareaModule,
    ToastModule, TooltipModule, ProductVariantPickerComponent
  ],
  templateUrl: './bom-form.component.html',
  styleUrls: ['./bom-form.component.scss'],
  providers: [MessageService]
})
export class BomFormComponent implements OnInit {
  form: FormGroup;
  isEditMode = false;
  uuid: string | null = null;
  bom: BomDetail | null = null;

  isLoading = true;
  isSaving = false;

  manufacturable: ProductListItemModel[] = [];
  productOptions: { label: string; value: string }[] = [];
  variantOptions: { label: string; value: string | null }[] = [{ label: 'Whole product', value: null }];
  warehouseOptions: { label: string; value: string | null }[] = [{ label: 'Any warehouse', value: null }];
  materials: ProductListItemModel[] = [];

  constructor(
    private fb: FormBuilder,
    private route: ActivatedRoute,
    private router: Router,
    private service: BomService,
    private inventoryService: InventoryService,
    private messageService: MessageService
  ) {
    this.form = this.fb.group({
      productUuid:        [null, Validators.required],
      productVariantUuid: [null],
      baseQuantity:       [1, [Validators.required, Validators.min(0.0001)]],
      baseUom:            [''],
      effectiveFrom:      [null],
      effectiveTo:        [null],
      warehouseUuid:      [null],
      notes:              [''],
      lines:              this.fb.array([])
    });
  }

  get lines(): FormArray<FormGroup> { return this.form.get('lines') as FormArray<FormGroup>; }
  get outputProductUuid(): string | null { return this.form.get('productUuid')?.value ?? null; }

  ngOnInit(): void {
    this.uuid = this.route.snapshot.paramMap.get('uuid');
    this.isEditMode = !!this.uuid;

    forkJoin({
      manufacturable: this.inventoryService.getManufacturableProducts(undefined, 1, 500).pipe(catchError(() => of(null))),
      materials:      this.inventoryService.getProducts({ activeOnly: true, pageSize: 500, availableFor: 'PRODUCTION' }).pipe(catchError(() => of(null))),
      warehouses:     this.inventoryService.getWarehouses().pipe(catchError(() => of(null))),
      bom:            this.uuid ? this.service.getBom(this.uuid) : of(null)
    }).subscribe({
      next: ({ manufacturable, materials, warehouses, bom }) => {
        this.manufacturable = manufacturable?.result?.data ?? [];
        this.productOptions = this.manufacturable.map(p => ({ label: `${p.name} (${p.sku})`, value: p.uuid }));
        this.materials = materials?.result?.data ?? [];
        this.warehouseOptions = [
          { label: 'Any warehouse', value: null },
          ...((warehouses?.result ?? []) as WarehouseModel[]).filter(w => w.isActive !== false).map(w => ({ label: w.name, value: w.uuid }))
        ];
        if (bom?.result) this.applyBom(bom.result);
        else if (this.isEditMode) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Bill of materials not found.' });
        }
        if (!this.isEditMode && this.lines.length === 0) this.addLine();
        this.isLoading = false;
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load reference data.' });
      }
    });
  }

  private applyBom(bom: BomDetail): void {
    this.bom = bom;
    if (bom.status !== 'DRAFT' && bom.status !== 'REJECTED') {
      this.messageService.add({ severity: 'warn', summary: 'Read only', detail: `A ${bom.status.toLowerCase()} recipe cannot be edited. Create a new version instead.` });
    }
    // The output product is fixed once the recipe exists; only its details and lines change.
    if (!this.productOptions.some(o => o.value === bom.productUuid)) {
      this.productOptions = [{ label: `${bom.productName} (${bom.productSku})`, value: bom.productUuid }, ...this.productOptions];
    }
    this.form.patchValue({
      productUuid: bom.productUuid,
      productVariantUuid: bom.productVariantUuid ?? null,
      baseQuantity: bom.baseQuantity,
      baseUom: bom.baseUom,
      effectiveFrom: bom.effectiveFrom ? new Date(bom.effectiveFrom) : null,
      effectiveTo: bom.effectiveTo ? new Date(bom.effectiveTo) : null,
      warehouseUuid: bom.warehouseUuid ?? null,
      notes: bom.notes ?? ''
    });
    this.form.get('productUuid')?.disable();
    this.loadVariants(bom.productUuid, bom.productVariantUuid ?? null);
    for (const line of bom.lines) {
      this.lines.push(this.newLine({
        materialProductUuid: line.materialProductUuid, materialVariantUuid: line.materialVariantUuid,
        materialName: `${line.materialProductName} – ${line.materialVariantName}`,
        quantity: line.quantity, uom: line.uom, scrapPercentage: line.scrapPercentage, isCritical: line.isCritical,
        notes: line.notes ?? '', warehouseUuid: line.warehouseUuid ?? null
      }));
    }
  }

  onProductChange(): void {
    this.form.patchValue({ productVariantUuid: null });
    const uuid = this.outputProductUuid;
    const product = this.manufacturable.find(p => p.uuid === uuid);
    if (product?.uomCode && !this.form.get('baseUom')?.value) this.form.patchValue({ baseUom: product.uomCode });
    this.loadVariants(uuid, null);
  }

  private loadVariants(productUuid: string | null, selected: string | null): void {
    this.variantOptions = [{ label: 'Whole product', value: null }];
    const product = this.manufacturable.find(p => p.uuid === productUuid);
    if (!product) return;
    this.inventoryService.getProductById(product.id).pipe(catchError(() => of(null))).subscribe(res => {
      const variants = (res?.result?.variants ?? []) as ProductVariantModel[];
      this.variantOptions = [
        { label: 'Whole product', value: null },
        ...variants.filter(v => v.isActive).map(v => ({ label: `${v.variantName} (${v.sku})`, value: v.uuid }))
      ];
      if (selected && this.variantOptions.some(o => o.value === selected)) this.form.patchValue({ productVariantUuid: selected });
    });
  }

  // ── Lines ─────────────────────────────────────────────────────────────────

  newLine(preset?: Partial<{
    materialProductUuid: string; materialVariantUuid: string; materialName: string; quantity: number; uom: string;
    scrapPercentage: number; isCritical: boolean; notes: string; warehouseUuid: string | null;
  }>): FormGroup {
    return this.fb.group({
      materialProductUuid: [preset?.materialProductUuid ?? null],
      materialVariantUuid: [preset?.materialVariantUuid ?? null, Validators.required],
      materialName:        [preset?.materialName ?? ''],
      quantity:            [preset?.quantity ?? null, [Validators.required, Validators.min(0.000001)]],
      uom:                 [preset?.uom ?? ''],
      scrapPercentage:     [preset?.scrapPercentage ?? 0, [Validators.required, Validators.min(0), Validators.max(99.99)]],
      isCritical:          [preset?.isCritical ?? true],
      notes:               [preset?.notes ?? ''],
      warehouseUuid:       [preset?.warehouseUuid ?? null]
    });
  }

  addLine(): void { this.lines.push(this.newLine()); }

  removeLine(index: number): void { this.lines.removeAt(index); }

  onMaterialSelected(index: number, selection: VariantPickerSelection): void {
    const line = this.lines.at(index);
    line.patchValue({
      materialProductUuid: selection.productUuid,
      materialVariantUuid: selection.variantUuid,
      materialName: selection.productName && selection.variantName ? `${selection.productName} – ${selection.variantName}` : (selection.productName ?? ''),
      uom: line.get('uom')?.value || selection.uomCode || ''
    });
  }

  /** The same input twice, or the output product as its own input, is refused server-side; say so first. */
  get lineProblem(): string | null {
    const variants = this.lines.controls.map(l => l.get('materialVariantUuid')?.value).filter(Boolean);
    if (new Set(variants).size !== variants.length) return 'The same material appears on more than one line.';
    const output = this.outputProductUuid;
    if (output && this.lines.controls.some(l => l.get('materialProductUuid')?.value === output)) return 'A product cannot be an input of its own recipe.';
    return null;
  }

  get canSave(): boolean {
    return this.form.valid && this.lines.length > 0 && !this.lineProblem && !this.isSaving &&
      (!this.bom || this.bom.status === 'DRAFT' || this.bom.status === 'REJECTED');
  }

  // ── Save ──────────────────────────────────────────────────────────────────

  private linesPayload(): BomLineRequest[] {
    return this.lines.controls.map((l, i) => {
      const v = l.value;
      return {
        materialVariantUuid: v.materialVariantUuid,
        quantity: v.quantity,
        uom: v.uom || undefined,
        scrapPercentage: v.scrapPercentage ?? 0,
        isCritical: !!v.isCritical,
        notes: v.notes || undefined,
        warehouseUuid: v.warehouseUuid || undefined,
        sequence: (i + 1) * 10
      };
    });
  }

  private isoDate(value: Date | null): string | undefined {
    if (!value) return undefined;
    return new Date(Date.UTC(value.getFullYear(), value.getMonth(), value.getDate())).toISOString();
  }

  save(): void {
    if (!this.canSave) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.isSaving = true;

    if (this.isEditMode && this.uuid) {
      this.service.updateBom(this.uuid, {
        baseQuantity: v.baseQuantity,
        baseUom: v.baseUom || undefined,
        effectiveFrom: this.isoDate(v.effectiveFrom),
        effectiveTo: this.isoDate(v.effectiveTo),
        clearEffectiveDates: !v.effectiveFrom && !v.effectiveTo,
        warehouseUuid: v.warehouseUuid || undefined,
        clearWarehouse: !v.warehouseUuid,
        notes: v.notes ?? '',
        lines: this.linesPayload()
      }).subscribe({
        next: () => {
          this.isSaving = false;
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Bill of materials updated.' });
          this.router.navigate(['/portal/pages/manufacturing/boms', this.uuid]);
        },
        error: (err) => {
          this.isSaving = false;
          this.messageService.add({ severity: 'error', summary: 'Not saved', detail: err.error?.message || 'The bill of materials could not be saved.' });
        }
      });
      return;
    }

    this.service.createBom({
      productUuid: v.productUuid,
      productVariantUuid: v.productVariantUuid || undefined,
      baseQuantity: v.baseQuantity,
      baseUom: v.baseUom || undefined,
      effectiveFrom: this.isoDate(v.effectiveFrom),
      effectiveTo: this.isoDate(v.effectiveTo),
      warehouseUuid: v.warehouseUuid || undefined,
      notes: v.notes || undefined,
      lines: this.linesPayload()
    }).subscribe({
      next: (res) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Bill of materials drafted.' });
        this.router.navigate(['/portal/pages/manufacturing/boms', res.result]);
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Not created', detail: err.error?.message || 'The bill of materials could not be created.' });
      }
    });
  }

  cancel(): void {
    if (this.isEditMode && this.uuid) this.router.navigate(['/portal/pages/manufacturing/boms', this.uuid]);
    else this.router.navigate(['/portal/pages/manufacturing/boms']);
  }
}
