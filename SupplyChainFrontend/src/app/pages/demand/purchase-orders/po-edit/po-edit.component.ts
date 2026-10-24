import { Component, OnInit, OnDestroy, inject } from '@angular/core';
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
import { AutoCompleteOpenOnFocusDirective } from '../../../../shared/directives/autocomplete-open-on-focus.directive';
import { CheckboxModule } from 'primeng/checkbox';
import { MessageService } from 'primeng/api';
import { DemandService, PatchPoRequest } from '../../../../services/demand.service';
import { SupplierService, SupplierListItemModel, SupplierDetailModel } from '../../../../services/supplier.service';
import { MoneyService } from '../../../../services/money.service';
import { DocCurrencyPicker } from '../../../../shared/doc-currency/doc-currency-picker';
import { DocCurrencyPanelComponent } from '../../../../shared/doc-currency/doc-currency-panel.component';
import { MoneyPipe } from '../../../../shared/money/money.pipe';
import { FLOW } from '../../../../shared/flow';
import { InventoryService, ProductListItemModel, WarehouseModel } from '../../../../services/inventory.service';
import { ProductVariantPickerComponent, VariantPickerSelection } from '../../../../shared/product-variant-picker/product-variant-picker.component';
import { PurchasePricingService } from '../../../../services/purchase-pricing.service';
import { PoLinePricing, addPricingControls, PRICE_TOAST_KEY, ymd } from '../po-line-pricing';
import { conversionTooltip, price2, price4 } from '../../../../services/purchase-pricing.service';
import { fromDateOnly } from '../../../../shared/date-only';

@Component({
  selector: 'app-po-edit',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule, FormsModule,
    ButtonModule, InputTextModule, TextareaModule, InputNumberModule,
    DropdownModule, CalendarModule, DividerModule, ToastModule,
    TooltipModule, AutoCompleteModule, AutoCompleteOpenOnFocusDirective, CheckboxModule, ProductVariantPickerComponent,
    DocCurrencyPanelComponent, MoneyPipe,
    ...FLOW
  ],
  templateUrl: './po-edit.component.html',
  styleUrls: ['./po-edit.component.scss'],
  providers: [MessageService]
})
export class PoEditComponent implements OnInit, OnDestroy {
  /** A45 — PO line auto-pricing (resolve-price, source badge, C7 re-lookup). */
  pricing!: PoLinePricing;
  readonly priceToastKey = PRICE_TOAST_KEY;
  readonly conversionTooltip = conversionTooltip;
  readonly price2 = price2;
  readonly price4 = price4;
  /** A46 C1 — the order date is editable only while the PO is a DRAFT. */
  orderDateEditable = true;
  poNumber: string | null = null;
  uuid = '';
  form!: FormGroup;
  isLoading = true;
  isSubmitting = false;
  minDate = new Date();

  supplierSuggestions: SupplierListItemModel[] = [];

  // A35 P3-14 — the draft's currency (changeable until approval locks the rate).
  private readonly money = inject(MoneyService);
  currency!: DocCurrencyPicker;
  readonly moneyCode = { display: 'code' } as const;
  supplierAuto: any = null;

  products: ProductListItemModel[] = [];
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
    private messageService: MessageService,
    private pricingSvc: PurchasePricingService
  ) {}

  ngOnDestroy() { this.pricing?.destroy(); }

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
        this.products = res?.result?.data ?? [];
      },
      error: () => { this.loadingProducts = false; }
    });
  }

  // PV-004 — see po-create.component.ts's identical handler for why description/UoM/price only
  // get overwritten once a specific variant is actually resolved.
  onLineVariantSelected(i: number, sel: VariantPickerSelection) {
    const line = this.lines.at(i);
    line.patchValue({
      productUuid: sel.productUuid,
      variantUuid: sel.variantUuid
    });
    if (sel.variantUuid) {
      const label = sel.variantName && sel.variantName !== 'Default'
        ? `${sel.productName} — ${sel.variantName}`
        : (sel.productName ?? '');
      line.patchValue({
        itemDescription: label,
        unitOfMeasure:   sel.uomCode ?? line.get('unitOfMeasure')?.value ?? null
      });
      // Silent: product default, not a buyer edit.
      line.get('unitPrice')!.setValue(sel.purchasePrice ?? 0, { emitEvent: false });
    }
  }

  private buildForm() {
    this.form = this.fb.group({
      supplierName:          ['', Validators.required],
      supplierId:            [''],
      orderDate:             [new Date()],
      currencyId:            [null as string | null],
      title:                 [''],
      deliveryDate:          [null],
      deliveryWarehouseId:   [null],
      deliveryWarehouseName: [''],
      notes:                 [''],
      lines:                 this.fb.array([this.newLine()])
    });
    this.currency = new DocCurrencyPicker(this.money, this.form.get('currencyId')!, 'PURCHASE');
    this.currency.load(false);
    this.pricing = new PoLinePricing(this.form, this.pricingSvc, this.messageService, this.router, {
      focusPrice: i => setTimeout(() => document.getElementById('unitPrice-' + i)?.focus()),
      currencyCode: () => this.currency.code || null,
      sourcePo: () => this.poNumber,
      fmt: n => n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 4 })
    });
    this.lines.controls.forEach(g => this.pricing.attach(g as FormGroup));
  }

  get lines(): FormArray { return this.form.get('lines') as FormArray; }

  newLine(): FormGroup {
    const g = this.fb.group({
      productUuid:     [null],
      variantUuid:     [null],
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
    addPricingControls(g);
    return this.pricing ? this.pricing.attach(g) : g;
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
  removeLine(i: number) {
    if (this.lines.length > 1) { this.pricing.detach(this.lines.at(i) as FormGroup); this.lines.removeAt(i); }
  }

  get estimatedTotal(): number {
    return this.lines.controls.reduce((s, c) =>
      s + (c.get('quantity')?.value ?? 0) * (c.get('unitPrice')?.value ?? 0), 0);
  }

  searchSuppliers(event: any) {
    this.supplierService.getSuppliers({ search: event.query, status: 'ACTIVE', page: 1, pageSize: 50 }).subscribe({
      next: (res) => { this.supplierSuggestions = res.result?.data ?? []; },
      error: () => { this.supplierSuggestions = []; }
    });
  }

  onSupplierChange(val: any) {
    if (val && typeof val === 'object') {
      this.form.patchValue({ supplierId: val.uuid, supplierName: val.supplierName });
      this.applySupplierCurrency(val.uuid);
    } else {
      this.form.patchValue({ supplierName: val ?? '', supplierId: '' });
    }
  }

  /** A35 D-9 — the supplier's default purchase currency (defaultPurchaseCurrencyId; older servers: preferredCurrency). */
  private applySupplierCurrency(supplierId: string | null) {
    if (!supplierId) return;
    this.supplierService.getSupplierById(supplierId).subscribe({
      next: res => {
        const s = res?.result as (SupplierDetailModel & { defaultPurchaseCurrencyId?: string | null }) | null | undefined;
        this.currency.applyPartnerDefault(s?.defaultPurchaseCurrencyId ?? s?.preferredCurrency ?? null);
      },
      error: () => { /* the currency stays as it is */ }
    });
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
        this.orderDateEditable = po.status === 'DRAFT';
        if (!this.orderDateEditable) this.form.get('orderDate')!.disable({ emitEvent: false });   // A46 C1
        if (po.status !== 'DRAFT') {
          this.messageService.add({ severity: 'warn', summary: 'Read-only', detail: `Only DRAFT purchase orders can be edited. Status: ${po.status}` });
          setTimeout(() => this.router.navigate(['/portal/pages/demand/purchase-orders', this.uuid]), 1500);
          return;
        }

        while (this.lines.length) { this.pricing.detach(this.lines.at(0) as FormGroup); this.lines.removeAt(0); }
        (po.lines ?? []).forEach(l => this.lines.push(this.pricing.attach(addPricingControls(this.fb.group({
          productUuid:     [l.productUuid ?? null],
          variantUuid:     [l.variantUuid ?? null],
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
        }), {
          priceSource: l.priceSource ?? 0, systemResolvedPrice: l.systemResolvedPrice ?? null,
          supplierPricelistUuid: l.supplierPricelistUuid ?? null, priceOverrideReason: l.priceOverrideReason ?? '',
          pricingDateBasis: l.pricingDateBasis ?? null,
          conversion: l.originalCurrencyPrice != null ? {
            code: l.originalCurrencyCode ?? (l.originalCurrencyId ? this.money.find(l.originalCurrencyId)?.code ?? null : null),
            price: l.originalCurrencyPrice, rate: l.exchangeRateUsed ?? null, date: l.exchangeRateDate ?? null } : null
        }))));
        if (!this.lines.length) this.lines.push(this.newLine());

        this.supplierAuto = po.supplierName;
        this.poNumber = po.poNumber ?? null;
        this.pricing.suspended = true;
        this.form.patchValue({
          supplierName:          po.supplierName,
          supplierId:            po.supplierId,
          orderDate:             po.orderDate ? fromDateOnly(po.orderDate) : (po.createdDate ? fromDateOnly(po.createdDate) : new Date()),
          title:                 po.title ?? '',
          deliveryDate:          po.deliveryDate ? new Date(po.deliveryDate) : null,
          deliveryWarehouseId:   po.deliveryWarehouseId ?? null,
          deliveryWarehouseName: po.deliveryWarehouseName ?? '',
          notes:                 po.notes ?? '',
          currencyId:            po.currencyId ?? null
        });
        this.pricing.suspended = false;
        this.currency.rebuild();
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
      orderDate:             this.orderDateEditable ? ymd(v.orderDate) : undefined,   // the server refuses a change once the PO is not DRAFT
      title:                 v.title                 || undefined,
      deliveryDate:          v.deliveryDate instanceof Date ? v.deliveryDate.toISOString() : v.deliveryDate || undefined,
      deliveryWarehouseId:   v.deliveryWarehouseId   || undefined,
      deliveryWarehouseName: v.deliveryWarehouseName || undefined,
      notes:                 v.notes                 || undefined,
      currencyId:            v.currencyId            || undefined,
      lines: v.lines.map((l: any) => ({
        variantUuid:     l.variantUuid     || undefined,
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
        requiresInspection: l.requiresInspection,
        ...PoLinePricing.payload(l)
      }))
    };

    this.demandService.patchPo(this.uuid, req).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Purchase order updated.' });
        // A46-0152: the server's per-line pricing warnings (absent from older servers).
        for (const warning of res?.pricing?.warnings ?? [])
          this.messageService.add({ severity: 'warn', summary: 'Pricing', detail: warning });
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
