import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule, ActivatedRoute } from '@angular/router';
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
import { DialogModule } from 'primeng/dialog';
import { MessageService } from 'primeng/api';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { DemandService, CreatePoRequest, VendorResponseModel } from '../../../../services/demand.service';
import { SupplierService, SupplierScoreSummaryModel } from '../../../../services/supplier.service';
import { InventoryService, ProductListItemModel, WarehouseModel } from '../../../../services/inventory.service';
import { AuthService } from '../../../service/auth.service';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';

@Component({
  selector: 'app-po-create',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule, FormsModule,
    ButtonModule, InputTextModule, TextareaModule, InputNumberModule,
    DropdownModule, CalendarModule, DividerModule, ToastModule, TooltipModule,
    DialogModule, AttachmentListComponent
  ],
  templateUrl: './po-create.component.html',
  styleUrls: ['./po-create.component.scss'],
  providers: [MessageService]
})
export class PoCreateComponent implements OnInit {
  form!: FormGroup;
  isSubmitting = false;

  // Generated once per page load so attachments uploaded before save can be linked to the
  // eventual PO (which is created with this same UUID on submit).
  readonly poUuid = crypto.randomUUID();

  supplierOptions: { label: string; value: string }[] = [];
  loadingSuppliers = false;
  private suppliersMap = new Map<string, string>();

  // SC-007 — advisory-only grade warning, never blocks save. Dismissible; re-shown if the supplier changes again.
  supplierGradeWarning: { severity: 'danger' | 'warn'; text: string } | null = null;
  private supplierGradeWarningDismissed = false;

  productOptions: { label: string; value: string }[] = [];
  private productsMap = new Map<string, ProductListItemModel>();
  loadingProducts = false;

  prOptions: { label: string; value: string }[] = [];
  loadingPrs = false;
  selectedPrUuid: string | null = null;

  quotationOptions: { label: string; value: string }[] = [];
  loadingQuotations = false;
  selectedQuotationUuid: string | null = null;

  loadingImport = false;
  importSource: 'pr' | 'quotation' | null = null;

  warehouseOptions: { label: string; value: string }[] = [];
  private warehousesMap = new Map<string, string>();
  loadingWarehouses = false;

  private prefillProductId:    string | null = null;
  private prefillQty:          number | null = null;
  private prefillProductName:  string | null = null;
  private prefillWarehouseId:  string | null = null;
  private prefillWarehouseName:string | null = null;

  // ── Supplier lock (awarded quotation) ────────────────────────────────────
  supplierLocked        = false;
  lockQuotationNumber   = '';
  lockOriginalSupplierId   = '';
  lockOriginalSupplierName = '';
  showUnlockDialog      = false;
  private unlockAuditNote = '';

  uomOptions = [
    { label: 'PC',   value: 'PC' },
    { label: 'BOX',  value: 'BOX' },
    { label: 'KG',   value: 'KG' },
    { label: 'L',    value: 'L' },
    { label: 'M',    value: 'M' },
    { label: 'SET',  value: 'SET' },
    { label: 'UNIT', value: 'UNIT' }
  ];

  constructor(
    private fb: FormBuilder,
    private demandService: DemandService,
    private supplierService: SupplierService,
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private router: Router,
    private authService: AuthService,
    private route: ActivatedRoute
  ) { this.buildForm(); }

  get isProcurementManager(): boolean {
    return this.authService.hasRole('Procurement Manager');
  }

  ngOnInit() {
    const p = this.route.snapshot.queryParams;
    if (p['productId']) {
      this.prefillProductId    = p['productId'];
      this.prefillQty          = p['qty'] ? Number(p['qty']) : null;
      this.prefillProductName  = p['productName'] ?? null;
      this.prefillWarehouseId  = p['warehouseId']  ?? null;
      this.prefillWarehouseName = p['warehouseName'] ?? null;
      if (this.prefillProductName) {
        this.form.patchValue({ title: `Reorder: ${this.prefillProductName}` });
      }
    }
    this.loadSuppliers();
    this.loadProducts();
    this.loadPrs();
    this.loadQuotations();
    this.loadWarehouses();
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
        if (this.prefillWarehouseId && this.warehousesMap.has(this.prefillWarehouseId)) {
          this.form.patchValue({
            deliveryWarehouseId:   this.prefillWarehouseId,
            deliveryWarehouseName: this.warehousesMap.get(this.prefillWarehouseId) ?? this.prefillWarehouseName ?? ''
          });
        }
      },
      error: () => { this.loadingWarehouses = false; }
    });
  }

  private loadSuppliers() {
    this.loadingSuppliers = true;
    this.supplierService.getSuppliers({ status: 'ACTIVE', pageSize: 500 }).subscribe({
      next: res => {
        this.loadingSuppliers = false;
        const data = res?.result?.data ?? [];
        this.suppliersMap.clear();
        this.supplierOptions = data.map(s => {
          this.suppliersMap.set(s.uuid, s.supplierName);
          return { label: s.supplierName, value: s.uuid };
        });
      },
      error: () => { this.loadingSuppliers = false; }
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
        if (this.prefillProductId && this.productsMap.has(this.prefillProductId)) {
          this.lines.at(0).patchValue({ productId: this.prefillProductId });
          this.onProductChange(0);
          if (this.prefillQty) {
            this.lines.at(0).patchValue({ quantity: this.prefillQty });
          }
        }
      },
      error: () => { this.loadingProducts = false; }
    });
  }

  private loadPrs() {
    this.loadingPrs = true;
    this.demandService.getPrs({ status: 'APPROVED', pageSize: 200 }).subscribe({
      next: res => {
        this.loadingPrs = false;
        this.prOptions = (res?.result?.data ?? []).map(pr => ({
          label: `${pr.prNumber} — ${pr.prTitle}`,
          value: pr.uuid
        }));
      },
      error: () => { this.loadingPrs = false; }
    });
  }

  private loadQuotations() {
    this.loadingQuotations = true;
    this.demandService.getQuotations({ status: 'AWARDED', pageSize: 200 }).subscribe({
      next: res => {
        this.loadingQuotations = false;
        this.quotationOptions = (res?.result?.data ?? []).map(q => ({
          label: `${q.quotationNumber} — ${q.title}`,
          value: q.uuid
        }));
      },
      error: () => { this.loadingQuotations = false; }
    });
  }

  onSupplierChange(event: any) {
    const name = this.suppliersMap.get(event.value) ?? '';
    this.form.patchValue({ supplierName: name });
    this.checkSupplierGrade(event.value, name);
  }

  // SC-007 — non-blocking grade check. Grade F -> red warning, Grade D -> softer amber warning,
  // A/B/C or never-scored -> no warning. The supplier dropdown itself is never filtered by grade.
  private checkSupplierGrade(supplierId: string | null, supplierName: string) {
    this.supplierGradeWarning = null;
    this.supplierGradeWarningDismissed = false;
    if (!supplierId) return;

    this.supplierService.getScoreSummary(supplierId).subscribe({
      next: (res) => {
        if (!res.success) return;
        this.supplierGradeWarning = this.buildGradeWarning(res.result, supplierName);
      },
      error: () => { /* advisory only — a failed lookup must never block PO creation */ }
    });
  }

  private buildGradeWarning(summary: SupplierScoreSummaryModel, supplierName: string): { severity: 'danger' | 'warn'; text: string } | null {
    if (summary.grade === 'F') {
      return {
        severity: 'danger',
        text: `Warning: ${supplierName} is rated F. Procurement Manager approval is recommended before proceeding.`
      };
    }
    if (summary.grade === 'D') {
      return {
        severity: 'warn',
        text: `${supplierName} is rated D (Underperforming). Consider alternative suppliers.`
      };
    }
    return null;
  }

  dismissSupplierGradeWarning() {
    this.supplierGradeWarningDismissed = true;
  }

  get showSupplierGradeWarning(): boolean {
    return !!this.supplierGradeWarning && !this.supplierGradeWarningDismissed;
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

  onProductChange(i: number) {
    const uuid = this.lines.at(i).get('productId')?.value;
    if (!uuid) return;
    const p = this.productsMap.get(uuid);
    if (!p) return;
    this.lines.at(i).patchValue({
      itemDescription: p.name,
      specification:   (p as any).description ?? '',
      unitOfMeasure:   p.uomCode ?? 'PC',
      unitPrice:       p.unitCost ?? 0
    });
  }

  onPrSelect(uuid: string | null) {
    if (!uuid) { this.clearImport(); return; }
    this.selectedPrUuid = uuid;
    this.selectedQuotationUuid = null;
    this.importSource = 'pr';
    this.loadingImport = true;
    this.demandService.getPrById(uuid).subscribe({
      next: res => {
        this.loadingImport = false;
        if (!res.success || !res.result) return;
        while (this.lines.length) this.lines.removeAt(0);
        for (const line of res.result.lines) {
          const group = this.newLine();
          group.patchValue({
            sourcePrLineUuid: line.uuid,
            productId:        line.productId       ?? null,
            itemDescription:  line.itemDescription,
            specification:    line.specification   ?? '',
            unitOfMeasure:    line.unitOfMeasure   ?? 'PC',
            quantity:         line.quantity,
            unitPrice:        line.estimatedUnitPrice ?? 0,
            lineNotes:        line.lineNotes        ?? ''
          });
          this.lines.push(group);
        }
        if (this.lines.length === 0) this.lines.push(this.newLine());

        // Auto-lock supplier if PR has an awarded quotation
        const aq = res.result.linkedAwardedQuotation;
        if (aq) {
          this.applySupplierLock(aq.awardedSupplierId, aq.awardedSupplierName, aq.quotationNumber);
        } else {
          this.clearSupplierLock();
        }
      },
      error: () => {
        this.loadingImport = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load requisition lines.' });
      }
    });
  }

  onQuotationSelect(uuid: string | null) {
    if (!uuid) { this.clearImport(); return; }
    this.selectedQuotationUuid = uuid;
    this.selectedPrUuid = null;
    this.importSource = 'quotation';
    this.loadingImport = true;
    forkJoin({
      detail:     this.demandService.getQuotationById(uuid),
      comparison: this.demandService.getQuotationComparison(uuid)
                    .pipe(catchError(() => of({ success: true, result: [] as VendorResponseModel[], message: '' })))
    }).subscribe({
      next: ({ detail, comparison }) => {
        this.loadingImport = false;
        if (!detail.success || !detail.result) return;
        const awarded = (comparison.result ?? []).find((r: VendorResponseModel) => r.status === 'AWARDED');
        const priceMap = new Map<string, number>();
        if (awarded) {
          for (const l of awarded.lines) priceMap.set(l.quotationLineUuid, l.netUnitPrice);
        }
        while (this.lines.length) this.lines.removeAt(0);
        for (const line of detail.result.lines) {
          const group = this.newLine();
          group.patchValue({
            itemDescription: line.itemDescription,
            specification:   line.specification ?? '',
            unitOfMeasure:   line.unitOfMeasure ?? 'PC',
            quantity:        line.quantity,
            unitPrice:       priceMap.get(line.uuid) ?? 0,
            lineNotes:       ''
          });
          this.lines.push(group);
        }
        if (this.lines.length === 0) this.lines.push(this.newLine());

        // Auto-lock supplier from awarded quotation
        if (awarded) {
          this.applySupplierLock(awarded.supplierId, awarded.supplierName, detail.result.quotationNumber);
        } else {
          this.clearSupplierLock();
        }
      },
      error: () => {
        this.loadingImport = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load quotation lines.' });
      }
    });
  }

  clearImport() {
    this.selectedPrUuid = null;
    this.selectedQuotationUuid = null;
    this.importSource = null;
    while (this.lines.length > 1) this.lines.removeAt(this.lines.length - 1);
    this.lines.at(0).reset({
      sourcePrLineUuid: null, productId: null, itemDescription: '',
      specification: '', unitOfMeasure: 'PC', quantity: 1,
      unitPrice: 0, requiredDate: null, lineNotes: ''
    });
    this.clearSupplierLock();
  }

  // ── Supplier lock helpers ─────────────────────────────────────────────────

  private applySupplierLock(supplierId: string, supplierName: string, quotationNumber: string) {
    this.form.patchValue({ supplierId, supplierName });
    this.supplierLocked        = true;
    this.lockQuotationNumber   = quotationNumber;
    this.lockOriginalSupplierId   = supplierId;
    this.lockOriginalSupplierName = supplierName;
    this.unlockAuditNote = '';
    this.checkSupplierGrade(supplierId, supplierName);
  }

  private clearSupplierLock() {
    this.supplierLocked        = false;
    this.lockQuotationNumber   = '';
    this.lockOriginalSupplierId   = '';
    this.lockOriginalSupplierName = '';
    this.unlockAuditNote = '';
    this.supplierGradeWarning = null;
    this.supplierGradeWarningDismissed = false;
  }

  openUnlockDialog() { this.showUnlockDialog = true; }

  confirmUnlock() {
    const user = this.authService.getUserData();
    const userName = user ? `${user.firstName} ${user.lastName ?? ''}`.trim() : 'Unknown User';
    const now = new Date().toISOString().replace('T', ' ').slice(0, 19) + ' UTC';
    this.unlockAuditNote =
      `[${now}] Supplier lock overridden by ${userName}. ` +
      `Original awarded supplier: ${this.lockOriginalSupplierName} ` +
      `(from quotation ${this.lockQuotationNumber}). Reason: Manual procurement override.`;
    this.supplierLocked  = false;
    this.showUnlockDialog = false;
    this.messageService.add({
      severity: 'warn',
      summary: 'Supplier Unlocked',
      detail: 'Supplier selection has been unlocked. An audit note will be saved with this PO.'
    });
  }

  private buildForm() {
    this.form = this.fb.group({
      supplierId:            ['', Validators.required],
      supplierName:          [''],
      title:                 [''],
      deliveryDate:          [null],
      deliveryWarehouseId:   [null],
      deliveryWarehouseName: [''],
      budgetCode:            [''],
      notes:                 [''],
      lines:                 this.fb.array([this.newLine()])
    });
  }

  get lines(): FormArray { return this.form.get('lines') as FormArray; }

  newLine(): FormGroup {
    return this.fb.group({
      sourcePrLineUuid: [null],
      productId:        [null],
      itemDescription:  ['', Validators.required],
      specification:    [''],
      unitOfMeasure:    ['PC'],
      quantity:         [1,  [Validators.required, Validators.min(0.0001), Validators.max(999999)]],
      unitPrice:        [0,  [Validators.required, Validators.min(0)]],
      warehouseId:      [null],
      warehouseName:    [''],
      requiredDate:     [null],
      lineNotes:        ['']
    });
  }

  addLine()             { this.lines.push(this.newLine()); }
  removeLine(i: number) { if (this.lines.length > 1) this.lines.removeAt(i); }

  get estimatedTotal(): number {
    return this.lines.controls.reduce(
      (s, c) => s + (c.get('quantity')?.value ?? 0) * (c.get('unitPrice')?.value ?? 0), 0
    );
  }

  onSubmit() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    this.isSubmitting = true;
    const v = this.form.getRawValue();

    const req: CreatePoRequest = {
      supplierId:            v.supplierId,
      supplierName:          v.supplierName,
      title:                 v.title               || undefined,
      deliveryDate:          v.deliveryDate instanceof Date ? v.deliveryDate.toISOString() : v.deliveryDate || undefined,
      deliveryWarehouseId:   v.deliveryWarehouseId  || undefined,
      deliveryWarehouseName: v.deliveryWarehouseName || undefined,
      budgetCode:            v.budgetCode           || undefined,
      notes:                 v.notes                || undefined,
      internalNotes:         this.unlockAuditNote   || undefined,
      lines: v.lines.map((l: any) => ({
        sourcePrLineUuid: l.sourcePrLineUuid || undefined,
        productUuid:      l.productId        || undefined,
        itemDescription:  l.itemDescription,
        specification:    l.specification    || undefined,
        unitOfMeasure:    l.unitOfMeasure    || undefined,
        quantity:         l.quantity,
        unitPrice:        l.unitPrice,
        warehouseId:      l.warehouseId      || undefined,
        warehouseName:    l.warehouseName    || undefined,
        requiredDate:     l.requiredDate instanceof Date ? l.requiredDate.toISOString() : l.requiredDate || undefined,
        lineNotes:        l.lineNotes        || undefined
      })),
      poUuid: this.poUuid
    };

    this.demandService.createPo(req).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Purchase order created.' });
          setTimeout(() => this.router.navigate(['/portal/pages/demand/purchase-orders', res.result]), 1200);
        }
      },
      error: (err) => {
        this.isSubmitting = false;
        const errors = err?.error?.errors;
        let detail: string;
        if (errors && typeof errors === 'object') {
          detail = (Object.values(errors) as string[][]).flat().join(' ');
        } else {
          detail = err?.error?.message ?? err?.error?.title ?? `Failed to create PO. (HTTP ${err?.status ?? 0})`;
        }
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
