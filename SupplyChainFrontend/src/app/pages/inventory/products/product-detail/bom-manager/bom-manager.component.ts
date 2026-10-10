import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormArray, FormBuilder, FormGroup, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TagModule } from 'primeng/tag';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  BOM_LINE_SOURCE_OPTIONS, BOM_USAGE_OPTIONS, BomComparison, BomDetail, BomLineRequest, BomLineSourceType, BomListItem, BomService,
  BomUsage, BomVersion, bomStatusSeverity, bomUsageTag
} from '../../../../../services/bom.service';
import { InventoryService, ProductListItemModel } from '../../../../../services/inventory.service';
import { BusinessPartnerService } from '../../../../../services/business-partner.service';
import { AuthService } from '../../../../service/auth.service';
import {
  ProductVariantPickerComponent, VariantPickerSelection
} from '../../../../../shared/product-variant-picker/product-variant-picker.component';

/**
 * A31-C4 — BOM management inline on the Product form, replacing the standalone BOM module UI
 * (list/create/edit pages) entirely. Two-panel layout: a list of every BOM for this product on the
 * left, an editor/viewer for whichever one is selected (or a brand-new draft) on the right. The BOM
 * API itself (SMS.Modules.Material `/api/boms/**`) is unchanged — only this entry point is new.
 */
@Component({
  selector: 'app-bom-manager',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ReactiveFormsModule,
    ButtonModule, CalendarModule, CheckboxModule, ConfirmDialogModule, DialogModule, DropdownModule,
    InputNumberModule, InputTextModule, TagModule, TextareaModule, ToastModule, TooltipModule,
    ProductVariantPickerComponent
  ],
  templateUrl: './bom-manager.component.html',
  styleUrls: ['./bom-manager.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class BomManagerComponent implements OnChanges {
  @Input({ required: true }) productUuid!: string;
  @Input() productName = '';
  @Input() productUomCode: string | null = null;
  /** A31-BR-C4-01 — only when this is true is a BOM actually mandatory for this product. */
  @Input() isManufacturable = false;
  /** A36 D-4 — a SERVICE product's BOM: lines get a Source (stock / subcontract / internal labor). */
  @Input() isServiceBom = false;
  /**
   * A37 BOM-SHR-02/03 — FEATURE_BOM_MANAGEMENT is off: the BOMs can be read, nothing can be created, edited or moved
   * through the workflow.
   */
  @Input() readOnly = false;

  readonly usageOptions = BOM_USAGE_OPTIONS;
  readonly usageTag = bomUsageTag;
  /** A37 D-11 — the selected BOM's usage (advisory). Kept outside the form: it stays editable after approval. */
  bomUsage: BomUsage = 'UNIVERSAL';
  savingUsage = false;

  readonly severity = bomStatusSeverity;
  readonly sourceOptions = BOM_LINE_SOURCE_OPTIONS;

  /** A36 — vendors for SUBCONTRACT lines (business partners with isVendor), loaded once for service BOMs. */
  vendorOptions: { label: string; value: string }[] = [];
  private vendorsLoaded = false;

  boms: BomListItem[] = [];
  isLoadingList = true;

  selectedUuid: string | null = null;
  bom: BomDetail | null = null;
  isLoadingDetail = false;
  isNew = false;
  isSaving = false;
  busy = false;

  form: FormGroup;
  materials: ProductListItemModel[] = [];
  materialsLoaded = false;

  versions: BomVersion[] = [];
  showCompare = false;
  compareWith: string | null = null;
  comparison: BomComparison | null = null;
  isComparing = false;

  rejectVisible = false;
  rejectReason = '';
  obsoleteVisible = false;
  obsoleteReason = '';

  constructor(
    private fb: FormBuilder,
    private service: BomService,
    private inventoryService: InventoryService,
    private authService: AuthService,
    private partnerService: BusinessPartnerService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {
    this.form = this.fb.group({
      baseQuantity:  [1, [Validators.required, Validators.min(0.0001)]],
      baseUom:       [''],
      effectiveFrom: [new Date()],
      effectiveTo:   [null],
      notes:         [''],
      lines:         this.fb.array([])
    });
  }

  get lines(): FormArray<FormGroup> { return this.form.get('lines') as FormArray<FormGroup>; }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['productUuid'] && this.productUuid) {
      this.selectedUuid = null;
      this.bom = null;
      this.isNew = false;
      this.loadList();
    }
  }

  private has(code: string): boolean { return !this.readOnly && this.authService.hasPermission(code); }
  private is(...statuses: string[]): boolean { return !!this.bom && statuses.includes(this.bom.status); }

  get canEdit(): boolean     { return this.has('BOM_EDIT')     && (this.isNew || this.is('DRAFT', 'REJECTED')); }
  get canSubmit(): boolean   { return !this.isNew && this.has('BOM_SUBMIT')   && this.is('DRAFT', 'REJECTED') && this.lines.length > 0; }
  get canApprove(): boolean  { return !this.isNew && this.has('BOM_APPROVE')  && this.is('SUBMITTED'); }
  get canActivate(): boolean { return !this.isNew && this.has('BOM_ACTIVATE') && this.is('APPROVED'); }
  get canObsolete(): boolean { return !this.isNew && this.has('BOM_OBSOLETE') && this.is('ACTIVE', 'APPROVED'); }
  get canVersion(): boolean  { return !this.isNew && this.has('BOM_CREATE')   && this.is('APPROVED', 'ACTIVE', 'OBSOLETE', 'REJECTED'); }
  get canDelete(): boolean   { return !this.isNew && this.has('BOM_EDIT')     && this.is('DRAFT', 'REJECTED'); }
  /** A37 — usage is editable in any non-terminal status (OBSOLETE is the only terminal one). */
  get canChangeUsage(): boolean { return this.has('BOM_EDIT') && (this.isNew || (!!this.bom && this.bom.status !== 'OBSOLETE')); }
  /** Past draft, a usage change is saved on its own at once; on a draft it goes with Save. */
  private get usageSavesAlone(): boolean { return !this.isNew && !!this.bom && !['DRAFT', 'REJECTED'].includes(this.bom.status); }

  /** A31-BR-C4-01 — the mandatory-BOM warning: manufacturable, but nothing has ever been drafted. */
  get needsFirstBom(): boolean { return !this.readOnly && this.isManufacturable && !this.isLoadingList && this.boms.length === 0; }

  /** A37 BOM-SHR-04 — the list asks for the BOMs this product's orders would prefer first. */
  private get preferFor(): 'PRODUCTION' | 'SERVICE' { return this.isServiceBom ? 'SERVICE' : 'PRODUCTION'; }

  get compareOptions(): { label: string; value: string }[] {
    return this.versions.filter(v => v.uuid !== this.selectedUuid).map(v => ({ label: `v${v.version} — ${v.bomNumber} (${v.status})`, value: v.uuid }));
  }

  private ensureMaterialsLoaded(): void {
    if (this.materialsLoaded) return;
    this.materialsLoaded = true;
    // A36 — a service BOM mixes stock parts, subcontracted services and labor hours, so it is not limited to the
    // Production channel; the server decides what each source may use (SVC-BOM-04/05).
    this.inventoryService.getProducts(this.isServiceBom
        ? { activeOnly: true, pageSize: 500 }
        : { activeOnly: true, pageSize: 500, availableFor: 'PRODUCTION' })
      .pipe(catchError(() => of(null)))
      .subscribe(res => { this.materials = res?.result?.data ?? []; });
    this.ensureVendorsLoaded();
  }

  private ensureVendorsLoaded(): void {
    if (!this.isServiceBom || this.vendorsLoaded) return;
    this.vendorsLoaded = true;
    this.partnerService.getPartners({ isVendor: true, pageSize: 500 })
      .pipe(catchError(() => of(null)))
      .subscribe(res => {
        this.vendorOptions = (res?.result?.data ?? [])
          .filter(p => !!p.uuid)
          .map(p => ({ label: p.companyName, value: p.uuid! }));
      });
  }

  /** The material channel the picker filters variants by: none for service BOMs. */
  get materialChannel(): 'PRODUCTION' | null { return this.isServiceBom ? null : 'PRODUCTION'; }

  supplierName(index: number): string {
    const line = this.lines.at(index);
    const uuid = line?.get('subcontractSupplierUuid')?.value;
    return line?.get('subcontractSupplierName')?.value || this.vendorOptions.find(o => o.value === uuid)?.label || '—';
  }

  sourceOf(index: number): BomLineSourceType { return this.lines.at(index)?.get('sourceType')?.value ?? 'STOCK'; }

  /** A36 — leaving SUBCONTRACT drops the supplier (SVC-BOM-03); INTERNAL_LABOR is counted in hours (SVC-BOM-05). */
  onSourceChange(index: number): void {
    const line = this.lines.at(index);
    const source = line.get('sourceType')?.value as BomLineSourceType;
    if (source !== 'SUBCONTRACT') line.patchValue({ subcontractSupplierUuid: null, subcontractSupplierName: '' });
    if (source === 'INTERNAL_LABOR') line.patchValue({ uom: 'HR' });
  }

  // ── List ──────────────────────────────────────────────────────────────────

  loadList(): void {
    this.isLoadingList = true;
    this.service.getBoms({ productUuid: this.productUuid, pageSize: 100, preferFor: this.preferFor }).subscribe({
      next: (res) => {
        this.isLoadingList = false;
        this.boms = (res.result?.data ?? []).sort((a, b) => b.version - a.version);
        if (this.selectedUuid && this.boms.some(b => b.uuid === this.selectedUuid)) this.loadDetail(this.selectedUuid);
        else if (this.boms.length > 0) this.selectBom(this.boms.find(b => b.status === 'ACTIVE')?.uuid ?? this.boms[0].uuid);
        else if (!this.readOnly) this.startNewBom();
      },
      error: () => {
        this.isLoadingList = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load bills of materials.' });
      }
    });
  }

  selectBom(uuid: string): void {
    this.isNew = false;
    this.selectedUuid = uuid;
    this.loadDetail(uuid);
  }

  private loadDetail(uuid: string): void {
    this.isLoadingDetail = true;
    this.versions = [];
    this.comparison = null;
    this.compareWith = null;
    this.ensureMaterialsLoaded();
    this.service.getBom(uuid).subscribe({
      next: (res) => {
        this.isLoadingDetail = false;
        this.bom = res.result ?? null;
        if (this.bom) this.applyBom(this.bom);
      },
      error: () => {
        this.isLoadingDetail = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Bill of materials not found.' });
      }
    });
  }

  private applyBom(bom: BomDetail): void {
    this.bomUsage = bom.bomUsage ?? 'UNIVERSAL';
    this.form.reset({
      baseQuantity: bom.baseQuantity,
      baseUom: bom.baseUom,
      effectiveFrom: bom.effectiveFrom ? new Date(bom.effectiveFrom) : null,
      effectiveTo: bom.effectiveTo ? new Date(bom.effectiveTo) : null,
      notes: bom.notes ?? ''
    });
    this.lines.clear();
    for (const line of bom.lines) {
      this.lines.push(this.newLine({
        materialProductUuid: line.materialProductUuid, materialVariantUuid: line.materialVariantUuid,
        materialName: `${line.materialProductName} – ${line.materialVariantName}`,
        quantity: line.quantity, uom: line.uom, scrapPercentage: line.scrapPercentage,
        isCritical: line.isCritical, notes: line.notes ?? '',
        sourceType: line.sourceType ?? 'STOCK',
        subcontractSupplierUuid: line.subcontractSupplierUuid ?? null,
        subcontractSupplierName: line.subcontractSupplierName ?? ''
      }));
    }
    if (bom.status !== 'DRAFT' && bom.status !== 'REJECTED') this.form.disable();
    else this.form.enable();
  }

  // ── New draft ─────────────────────────────────────────────────────────────

  startNewBom(): void {
    if (this.readOnly) return;
    this.bomUsage = 'UNIVERSAL';
    this.isNew = true;
    this.selectedUuid = null;
    this.bom = null;
    this.ensureMaterialsLoaded();
    this.form.reset({
      // A31-C5 — defaults to today; the server does the same if this is ever left blank anyway.
      baseQuantity: 1, baseUom: this.productUomCode ?? '', effectiveFrom: new Date(), effectiveTo: null, notes: ''
    });
    this.form.enable();
    this.lines.clear();
    this.addLine();
  }

  // ── Lines ─────────────────────────────────────────────────────────────────

  newLine(preset?: Partial<{
    materialProductUuid: string; materialVariantUuid: string; materialName: string; quantity: number;
    uom: string; scrapPercentage: number; isCritical: boolean; notes: string;
    sourceType: BomLineSourceType; subcontractSupplierUuid: string | null; subcontractSupplierName: string;
  }>): FormGroup {
    return this.fb.group({
      sourceType:              [preset?.sourceType ?? 'STOCK'],
      subcontractSupplierUuid: [preset?.subcontractSupplierUuid ?? null],
      subcontractSupplierName: [preset?.subcontractSupplierName ?? ''],
      materialProductUuid: [preset?.materialProductUuid ?? null],
      materialVariantUuid: [preset?.materialVariantUuid ?? null, Validators.required],
      materialName:        [preset?.materialName ?? ''],
      quantity:            [preset?.quantity ?? null, [Validators.required, Validators.min(0.000001)]],
      uom:                 [preset?.uom ?? ''],
      scrapPercentage:     [preset?.scrapPercentage ?? 0, [Validators.required, Validators.min(0), Validators.max(99.99)]],
      isCritical:          [preset?.isCritical ?? true],
      notes:               [preset?.notes ?? '']
    });
  }

  addLine(): void { this.lines.push(this.newLine()); }

  removeLine(index: number): void {
    this.confirmationService.confirm({
      header: 'Remove this line?',
      message: 'This line will be removed from the recipe.',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      accept: () => this.lines.removeAt(index)
    });
  }

  onMaterialSelected(index: number, selection: VariantPickerSelection): void {
    const line = this.lines.at(index);
    line.patchValue({
      materialProductUuid: selection.productUuid,
      materialVariantUuid: selection.variantUuid,
      materialName: selection.productName && selection.variantName ? `${selection.productName} – ${selection.variantName}` : (selection.productName ?? ''),
      uom: line.get('uom')?.value || selection.uomCode || ''
    });
  }

  get lineProblem(): string | null {
    const variants = this.lines.controls.map(l => l.get('materialVariantUuid')?.value).filter(Boolean);
    if (new Set(variants).size !== variants.length) return 'The same material appears on more than one line.';
    if (this.lines.controls.some(l => l.get('materialProductUuid')?.value === this.productUuid)) return 'A product cannot be an input of its own recipe.';
    if (this.isServiceBom && this.lines.controls.some(l => l.get('sourceType')?.value === 'SUBCONTRACT' && !l.get('subcontractSupplierUuid')?.value)) {
      return 'Subcontract supplier is required for subcontracted BOM lines.';
    }
    return null;
  }

  get canSave(): boolean {
    return this.form.valid && this.lines.length > 0 && !this.lineProblem && !this.isSaving && this.canEdit;
  }

  // ── Save / workflow ───────────────────────────────────────────────────────

  private linesPayload(): BomLineRequest[] {
    return this.lines.controls.map((l, i) => {
      const v = l.value;
      // A36 — manufacturing BOM lines are always STOCK; only a service BOM offers the other sources.
      const sourceType: BomLineSourceType = this.isServiceBom ? (v.sourceType ?? 'STOCK') : 'STOCK';
      return {
        sourceType,
        subcontractSupplierUuid: sourceType === 'SUBCONTRACT' ? (v.subcontractSupplierUuid ?? null) : null,
        materialVariantUuid: v.materialVariantUuid,
        quantity: v.quantity,
        uom: v.uom || undefined,
        scrapPercentage: v.scrapPercentage ?? 0,
        isCritical: !!v.isCritical,
        notes: v.notes || undefined,
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

    if (this.isNew) {
      this.service.createBom({
        productUuid: this.productUuid,
        baseQuantity: v.baseQuantity,
        baseUom: v.baseUom || undefined,
        effectiveFrom: this.isoDate(v.effectiveFrom),
        effectiveTo: this.isoDate(v.effectiveTo),
        notes: v.notes || undefined,
        lines: this.linesPayload(),
        bomUsage: this.bomUsage
      }).subscribe({
        next: (res) => {
          this.isSaving = false;
          this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Bill of materials drafted.' });
          this.selectedUuid = res.result ?? null;
          this.isNew = false;
          this.loadList();
        },
        error: (err) => {
          this.isSaving = false;
          this.messageService.add({ severity: 'error', summary: 'Not created', detail: err.error?.message || 'The bill of materials could not be created.' });
        }
      });
      return;
    }

    if (!this.selectedUuid) return;
    this.service.updateBom(this.selectedUuid, {
      baseQuantity: v.baseQuantity,
      baseUom: v.baseUom || undefined,
      effectiveFrom: this.isoDate(v.effectiveFrom),
      effectiveTo: this.isoDate(v.effectiveTo),
      clearEffectiveDates: !v.effectiveFrom && !v.effectiveTo,
      notes: v.notes ?? '',
      lines: this.linesPayload(),
      bomUsage: this.bomUsage
    }).subscribe({
      next: () => {
        this.isSaving = false;
        this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Bill of materials updated.' });
        this.loadList();
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Not saved', detail: err.error?.message || 'The bill of materials could not be saved.' });
      }
    });
  }

  /** A37 — a draft keeps the choice for Save; past draft (not OBSOLETE) it is saved at once via PUT /api/boms/{uuid}/usage. */
  onUsageChange(usage: BomUsage): void {
    if (!this.canChangeUsage) return;
    const previous = this.bomUsage;
    this.bomUsage = usage;
    if (!this.usageSavesAlone || !this.selectedUuid || usage === previous) return;
    this.savingUsage = true;
    this.service.setUsage(this.selectedUuid, usage).subscribe({
      next: () => {
        this.savingUsage = false;
        const row = this.boms.find(b => b.uuid === this.selectedUuid);
        if (row) row.bomUsage = usage;
        if (this.bom) this.bom.bomUsage = usage;
        this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'BOM usage updated.' });
      },
      error: (err) => {
        this.savingUsage = false;
        this.bomUsage = previous;
        this.fail(err);
      }
    });
  }

  cancelNew(): void {
    if (this.boms.length > 0) this.selectBom(this.boms[0].uuid);
    else { this.isNew = false; this.bom = null; }
  }

  submit(): void   { this.run(this.service.submit(this.selectedUuid!), 'Submitted for approval.'); }
  approve(): void  { this.run(this.service.approve(this.selectedUuid!), 'Approved. It can now be activated.'); }
  activate(): void { this.run(this.service.activate(this.selectedUuid!), 'Activated. Production orders will use this version.'); }

  openReject(): void { this.rejectReason = ''; this.rejectVisible = true; }
  confirmReject(): void {
    if (!this.rejectReason.trim()) return;
    this.run(this.service.reject(this.selectedUuid!, this.rejectReason.trim()), 'Rejected. Edit and resubmit.', () => this.rejectVisible = false);
  }

  openObsolete(): void { this.obsoleteReason = ''; this.obsoleteVisible = true; }
  confirmObsolete(): void {
    this.run(this.service.obsolete(this.selectedUuid!, this.obsoleteReason.trim() || undefined), 'Made obsolete.', () => this.obsoleteVisible = false);
  }

  newVersion(): void {
    if (this.busy || !this.selectedUuid) return;
    this.busy = true;
    this.service.newVersion(this.selectedUuid).subscribe({
      next: (res) => {
        this.busy = false;
        this.messageService.add({ severity: 'success', summary: 'Drafted', detail: 'A new version was drafted from this one.' });
        this.selectedUuid = res.result ?? null;
        this.isNew = false;
        this.loadList();
      },
      error: (err) => { this.busy = false; this.fail(err); }
    });
  }

  deleteBom(): void {
    if (!this.selectedUuid) return;
    this.confirmationService.confirm({
      header: 'Delete this draft?',
      message: `${this.bom?.bomNumber} will be removed. This cannot be undone.`,
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      accept: () => {
        this.busy = true;
        this.service.deleteBom(this.selectedUuid!).subscribe({
          next: () => {
            this.busy = false;
            this.messageService.add({ severity: 'success', summary: 'Deleted', detail: 'The draft was removed.' });
            this.selectedUuid = null;
            this.loadList();
          },
          error: (err) => { this.busy = false; this.fail(err); }
        });
      }
    });
  }

  // ── Version comparison (A31-C4 §6.5) ─────────────────────────────────────

  openCompare(): void {
    if (!this.bom) return;
    this.showCompare = true;
    this.service.getVersions(this.bom.productUuid, this.bom.productVariantUuid).subscribe({
      next: (res) => { this.versions = res.result ?? []; },
      error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'The versions could not be loaded.' })
    });
  }

  compare(): void {
    if (!this.compareWith || !this.selectedUuid) return;
    this.isComparing = true;
    this.service.compare(this.compareWith, this.selectedUuid).subscribe({
      next: (res) => { this.isComparing = false; this.comparison = res.result ?? null; },
      error: (err) => {
        this.isComparing = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'The versions could not be compared.' });
      }
    });
  }

  private run(call: import('rxjs').Observable<unknown>, detail: string, then?: () => void): void {
    if (this.busy) return;
    this.busy = true;
    call.subscribe({
      next: () => {
        this.busy = false;
        then?.();
        this.messageService.add({ severity: 'success', summary: 'Done', detail });
        this.loadList();
      },
      error: (err) => { this.busy = false; this.fail(err); }
    });
  }

  private fail(err: { error?: { message?: string } }): void {
    this.messageService.add({ severity: 'error', summary: 'Not done', detail: err.error?.message || 'The action failed.' });
  }
}
