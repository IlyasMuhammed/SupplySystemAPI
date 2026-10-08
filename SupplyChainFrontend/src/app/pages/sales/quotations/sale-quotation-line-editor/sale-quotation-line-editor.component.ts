import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { SelectButtonModule } from 'primeng/selectbutton';
import { TooltipModule } from 'primeng/tooltip';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';

import {
  ProductVariantPickerComponent, VariantPickerSelection
} from '../../../../shared/product-variant-picker/product-variant-picker.component';
import {
  SalesPreorderService, SaleQuotation, SaleQuotationLine, SaleQuotationLineRequest, SaleQuotationLineType, RejectionReason
} from '../../../../services/sales-preorder.service';
import { InventoryService, ProductListItemModel } from '../../../../services/inventory.service';
import { FinanceSetupService, TaxCodeModel } from '../../../../services/finance-setup.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { TenantService } from '../../../service/tenant.service';
import { AuthService } from '../../../service/auth.service';
import { fromDateOnly, toDateOnly } from '../../../../shared/date-only';
import { RETIRED_TAX_CODE_SUFFIX, taxCodeLabel } from '../../../../shared/tax-code-label';
import {
  TaxCodeOption, saleOrderGrandTotal, saleOrderLineTotal
} from '../../sale-orders/sale-order-form/sale-order-form.component';
import { LeadTimeService } from '../../../../services/lead-time.service';
import {
  LeadTimeApplyMode, LeadTimeCalculation, LeadTimePopoverComponent
} from '../../lead-time-popover/lead-time-popover.component';

/**
 * A32-PC-12 — adds or changes one line of a DRAFT sale quotation. Pricing works as on the sale order form's
 * line editor (tax code with its rate, discount, a total in the server's own arithmetic). The line type decides
 * the rest: a REJECTED line has a reason and no price; an ALTERNATIVE line replaces one of the REJECTED lines.
 */
@Component({
  selector: 'app-sale-quotation-line-editor',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule, ButtonModule, DropdownModule, CalendarModule, InputNumberModule,
    InputTextModule, TextareaModule, SelectButtonModule, TooltipModule, ProductVariantPickerComponent, LeadTimePopoverComponent
  ],
  templateUrl: './sale-quotation-line-editor.component.html',
  styleUrls: ['./sale-quotation-line-editor.component.scss']
})
export class SaleQuotationLineEditorComponent implements OnInit {
  @Input({ required: true }) quotation!: SaleQuotation;
  /** The line to change; null for a new one. */
  @Input() line: SaleQuotationLine | null = null;
  /** The saved line's uuid. */
  @Output() saved = new EventEmitter<string>();
  @Output() cancelled = new EventEmitter<void>();

  form: FormGroup;
  products: ProductListItemModel[] = [];

  typeOptions: { label: string; value: SaleQuotationLineType; disabled?: boolean }[] = [];
  /** The quotation's REJECTED lines, other than this one: what an ALTERNATIVE may replace. */
  alternativeForOptions: { label: string; value: string }[] = [];
  reasonOptions: { label: string; value: string }[] = [];

  /** An existing line with an item shows the item; "Change" opens the picker. */
  pickerOpen = true;
  itemLabel = '';
  itemSku = '';

  isSaving = false;
  error: string | null = null;

  /** The organization's active sales tax codes. Empty until they load, and for an organization with none. */
  private taxCodes: TaxCodeModel[] = [];
  private taxCodesLoaded = false;
  /** The code the loaded line carries, kept so it still shows if it has been retired since. */
  private ownTaxCode: TaxCodeOption | null = null;

  private reasons: RejectionReason[] = [];
  private reasonsRequested = false;

  /** The last price the pricing rules suggested, so a later suggestion may replace it but never a typed price. */
  private suggestedPrice: number | null = null;
  private priceSeq = 0;

  constructor(
    fb: FormBuilder,
    private salesPreorderService: SalesPreorderService,
    private inventoryService: InventoryService,
    private financeSetupService: FinanceSetupService,
    private pricingService: PricingRuleService,
    private tenantService: TenantService,
    private authService: AuthService,
    private leadTimeService: LeadTimeService
  ) {
    this.form = fb.group({
      lineType:               ['NORMAL' as SaleQuotationLineType],
      productUuid:            [null as string | null],
      variantUuid:            [null as string | null],
      productDescription:     ['', Validators.maxLength(500)],
      quantity:               [null as number | null, [Validators.min(0), Validators.max(999999999)]],
      uomCode:                ['', Validators.maxLength(20)],
      unitPrice:              [null as number | null, Validators.min(0)],
      discountPercent:        [0, [Validators.min(0), Validators.max(100)]],
      // A typed percentage is held to 0–100 (decimal(5,2)); a code brings its own rate.
      taxPercent:             [0, [Validators.min(0), Validators.max(100)]],
      taxCodeUuid:            [null as string | null],
      promisedDeliveryDate:   [null as Date | null],
      rejectionReasonUuid:    [null as string | null],
      rejectionNotes:         ['', Validators.maxLength(500)],
      alternativeForLineUuid: [null as string | null],
      alternativeNotes:       ['', Validators.maxLength(500)],
      notes:                  ['', Validators.maxLength(1000)]
    });
  }

  ngOnInit() {
    this.alternativeForOptions = this.quotation.lines
      .filter(l => l.lineType === 'REJECTED' && l.uuid !== this.line?.uuid)
      .sort((a, b) => a.lineNumber - b.lineNumber)
      .map(l => ({ label: `Line ${l.lineNumber} — ${l.productDescription}`, value: l.uuid }));

    // A rejected line that alternatives point at must stay rejected (the server refuses otherwise).
    const hasAlternatives = this.line?.lineType === 'REJECTED' && this.quotation.lines.some(l => l.alternativeForLineUuid === this.line!.uuid);
    this.typeOptions = [
      { label: 'Normal',        value: 'NORMAL',      disabled: hasAlternatives },
      { label: '✕ Rejected',    value: 'REJECTED' },
      { label: '◇ Alternative', value: 'ALTERNATIVE', disabled: hasAlternatives || this.alternativeForOptions.length === 0 }
    ];

    if (this.line) this.patchFrom(this.line);
    if (this.readOnly) this.form.disable();

    this.inventoryService.getProducts({ activeOnly: true, pageSize: 500, availableFor: 'RETAIL' }).subscribe({
      next: (res) => { this.products = res.result?.data ?? []; },
      error: () => { this.error = 'The product list could not be loaded.'; }
    });

    // Without tax codes the editor still works: the line takes a tax percentage.
    this.financeSetupService.getTaxCodes('SALES').subscribe({
      next: (res) => {
        this.taxCodes = (res.result ?? []).filter(c => c.isActive);
        this.taxCodesLoaded = true;
        this.applyDefaultTaxCode();
        this.applyCurrentTaxRate();
      },
      error: () => { this.taxCodes = []; }
    });
  }

  /** Lines change only while the quotation is a draft (BR-C2-05). */
  get readOnly(): boolean {
    return !this.quotation.isEditable || this.quotation.status !== 'DRAFT';
  }

  get isEdit(): boolean { return !!this.line; }

  get lineType(): SaleQuotationLineType { return this.form.get('lineType')!.value; }

  get isPriced(): boolean { return this.lineType !== 'REJECTED'; }

  private patchFrom(l: SaleQuotationLine) {
    this.form.patchValue({
      lineType: l.lineType,
      variantUuid: l.variantUuid ?? null,
      productDescription: l.productDescription ?? '',
      quantity: l.quantity,
      uomCode: l.uomCode ?? '',
      unitPrice: l.lineType === 'REJECTED' ? null : l.unitPrice,
      discountPercent: l.discountPercent ?? 0,
      taxPercent: l.taxPercent ?? 0,
      taxCodeUuid: l.taxCodeUuid ?? null,
      promisedDeliveryDate: l.promisedDeliveryDate ? fromDateOnly(l.promisedDeliveryDate) : null,
      rejectionReasonUuid: l.rejectionReasonUuid ?? null,
      rejectionNotes: l.rejectionNotes ?? '',
      alternativeForLineUuid: l.alternativeForLineUuid ?? null,
      alternativeNotes: l.alternativeNotes ?? '',
      notes: l.notes ?? ''
    });
    // Its description is its own, not the item's default name.
    this.form.get('productDescription')!.markAsDirty();
    if (l.variantUuid) {
      this.pickerOpen = false;
      this.itemLabel = l.variantName || l.productDescription;
      this.itemSku = l.variantSku ?? '';
    }
    if (l.taxCodeUuid) {
      const code = l.taxCode ?? 'Code';
      this.ownTaxCode = { value: l.taxCodeUuid, code, ratePercent: l.taxPercent, label: taxCodeLabel(code, l.taxPercent) };
    }
    if (l.lineType === 'REJECTED') this.ensureReasons();
  }

  // ── Type ────────────────────────────────────────────────────────────────────

  setType(type: SaleQuotationLineType) {
    this.form.get('lineType')!.setValue(type);
    this.onTypeChange();
  }

  onTypeChange() {
    this.error = null;
    if (this.lineType === 'REJECTED') this.ensureReasons();
    else this.refreshPrice();
  }

  private ensureReasons() {
    if (this.reasonsRequested) return;
    this.reasonsRequested = true;
    this.salesPreorderService.getRejectionReasons().subscribe({
      next: (res) => { this.reasons = res.result ?? []; this.buildReasonOptions(); },
      error: () => {
        this.reasonsRequested = false;
        this.error = 'The rejection reasons could not be loaded.';
      }
    });
  }

  /** The active reasons, and the line's own reason if it has been deactivated since — shown, but not offered anew. */
  private buildReasonOptions() {
    this.reasonOptions = this.reasons.map(r => ({ label: `${r.code} — ${r.description}`, value: r.uuid }));
    const own = this.line?.rejectionReasonUuid;
    if (own && !this.reasons.some(r => r.uuid === own)) {
      const label = `${this.line!.rejectionReasonCode ?? 'Reason'} — ${this.line!.rejectionReasonDescription ?? ''}`.trim();
      this.reasonOptions.push({ label: `${label} (no longer active)`, value: own });
    }
  }

  // ── Item ────────────────────────────────────────────────────────────────────

  onVariantSelected(sel: VariantPickerSelection) {
    const label = sel.variantName && sel.variantName !== 'Default' ? `${sel.productName} — ${sel.variantName}` : (sel.productName ?? '');
    this.form.patchValue({ productUuid: sel.productUuid, variantUuid: sel.variantUuid, uomCode: sel.uomCode ?? '' });
    // The description follows the item until someone writes their own.
    const description = this.form.get('productDescription')!;
    if (!description.dirty) description.setValue(label);
    this.itemLabel = label;
    this.itemSku = sel.variantSku ?? '';
    this.refreshPrice();
  }

  changeItem() { this.pickerOpen = true; }

  /**
   * Suggests what the pricing rules give this item for this customer at the start of the validity, as the
   * server would price a line sent without one. Only a price already in the quotation's currency is filled in;
   * otherwise the field stays blank and the server prices (and converts) it on save. A typed price is never replaced.
   */
  refreshPrice() {
    const v = this.form.getRawValue();
    const price = this.form.get('unitPrice')!;
    if (this.readOnly || !this.isPriced || !v.variantUuid || !(v.quantity > 0)) return;
    if (price.value != null && price.value !== this.suggestedPrice) return;
    if (!this.authService.hasPermission('INVENTORY_VIEW')) return;

    const seq = ++this.priceSeq;
    this.pricingService.resolvePrice(v.variantUuid, this.quotation.partnerId, v.quantity, this.quotation.validFrom.slice(0, 10)).subscribe({
      next: (res) => {
        if (seq !== this.priceSeq) return;
        if (price.value != null && price.value !== this.suggestedPrice) return;   // typed meanwhile
        const r = res.result;
        const currency = r?.currencyId ?? this.tenantService.tenant()?.baseCurrency ?? null;
        const usable = !!r?.found && r.unitPrice != null && currency === this.quotation.currencyId;
        this.suggestedPrice = usable ? r!.unitPrice! : null;
        price.setValue(this.suggestedPrice);
      },
      error: () => { /* a preview only: the server prices a blank line on save */ }
    });
  }

  // ── Tax codes — as on the sale order form (SAP alignment S-3) ───────────────

  /** The active codes, and the line's own code if it is no longer among them (so it says what it was taxed at). */
  get taxCodeOptions(): TaxCodeOption[] {
    const options: TaxCodeOption[] = this.taxCodes.map(c => ({
      value: c.uuid, ratePercent: c.ratePercent, code: c.code,
      label: taxCodeLabel(c.code, c.ratePercent) + (c.name && c.name !== c.code ? ` — ${c.name}` : '')
    }));
    const own = this.ownTaxCode;
    if (own && !this.taxCodes.some(c => c.uuid === own.value) && this.form.get('taxCodeUuid')!.value === own.value) {
      options.push({ ...own, label: this.taxCodesLoaded ? own.label + RETIRED_TAX_CODE_SUFFIX : own.label });
    }
    return options;
  }

  /** With no code to offer, the line takes a percentage. */
  get showTaxCodes(): boolean { return this.taxCodeOptions.length > 0; }

  get hasTaxCode(): boolean { return !!this.form.get('taxCodeUuid')!.value; }

  /** On a code the organization no longer offers for sales: the server would refuse it, so the editor does too. */
  get hasRetiredTaxCode(): boolean {
    const uuid = this.form.get('taxCodeUuid')!.value as string | null;
    return !!uuid && this.taxCodesLoaded && !this.taxCodes.some(c => c.uuid === uuid);
  }

  /** Picking a code sets the rate to the code's; clearing it leaves the rate to be typed. */
  onTaxCodeChange() {
    const uuid = this.form.get('taxCodeUuid')!.value as string | null;
    if (!uuid) return;
    const option = this.taxCodeOptions.find(o => o.value === uuid);
    if (option) this.form.get('taxPercent')!.setValue(option.ratePercent);
  }

  /** A new line starts on the organization's default sales code; an existing line keeps what it has. */
  private applyDefaultTaxCode() {
    const code = this.taxCodes.find(c => c.isDefault);
    if (!code || this.isEdit || this.readOnly) return;
    const tax = this.form.get('taxPercent')!;
    if (!this.form.get('taxCodeUuid')!.value && tax.pristine && !tax.value) {
      this.form.patchValue({ taxCodeUuid: code.uuid, taxPercent: code.ratePercent });
    }
  }

  /** The server taxes a line on a code at the code's current rate, so a loaded line previews that rate. */
  private applyCurrentTaxRate() {
    const code = this.taxCodes.find(c => c.uuid === this.form.get('taxCodeUuid')!.value);
    const tax = this.form.get('taxPercent')!;
    if (code && tax.value !== code.ratePercent && !this.readOnly) tax.setValue(code.ratePercent);
  }

  // ── Totals — the server's formula and rounding ──────────────────────────────

  /** qty × price less the discount plus the tax, to the cent. Null until there is a price (or on a REJECTED line). */
  get lineTotal(): number | null {
    const v = this.form.getRawValue();
    if (!this.isPriced || v.unitPrice == null || !v.quantity) return null;
    return saleOrderLineTotal(v.quantity, v.unitPrice, v.discountPercent ?? 0, v.taxPercent ?? 0);
  }

  /** The quotation's grand total with this line as it now stands (in place of its saved self), as the server totals it. */
  get projectedGrandTotal(): number {
    const parts = this.quotation.lines
      .filter(l => l.uuid !== this.line?.uuid && l.lineType !== 'REJECTED')
      .map(l => ({ quantity: l.quantity, unitPrice: l.unitPrice, discountPercent: l.discountPercent, taxPercent: l.taxPercent }));
    const v = this.form.getRawValue();
    if (this.isPriced && v.unitPrice != null && v.quantity) {
      parts.push({ quantity: v.quantity, unitPrice: v.unitPrice, discountPercent: v.discountPercent ?? 0, taxPercent: v.taxPercent ?? 0 });
    }
    return saleOrderGrandTotal(parts);
  }

  // ── Saving ──────────────────────────────────────────────────────────────────

  /** Why the line cannot be saved as it stands; null when it can. */
  get problem(): string | null {
    if (this.readOnly) return 'Only a draft quotation can be changed.';
    const v = this.form.getRawValue();
    const type = this.lineType;
    if (type === 'REJECTED') {
      if (!v.variantUuid && !v.productDescription?.trim()) return 'Describe the item, or choose it.';
      if (!v.rejectionReasonUuid) return 'Choose why the line is rejected.';
    } else {
      if (!v.variantUuid) return 'Choose an item.';
      if (!(v.quantity > 0)) return 'The quantity must be above zero.';
      if (type === 'ALTERNATIVE' && !v.alternativeForLineUuid) return 'Choose the rejected line this is an alternative for.';
      if (this.hasRetiredTaxCode) {
        const code = this.ownTaxCode?.code ?? 'on this line';
        return `Tax code ${code} is no longer active. Choose another code, or clear it and type the rate.`;
      }
    }
    if (this.form.invalid) return 'Check the highlighted fields.';
    return null;
  }

  buildRequest(): SaleQuotationLineRequest {
    const v = this.form.getRawValue();
    const type = this.lineType;
    const priced = type !== 'REJECTED';
    const text = (s: string | null | undefined) => s?.trim() || null;
    return {
      lineType: type,
      variantUuid: v.variantUuid ?? null,
      productDescription: text(v.productDescription),
      quantity: v.quantity ?? 0,
      uomCode: text(v.uomCode),
      // Blank: the server prices it from the pricing rules.
      unitPrice: priced ? v.unitPrice ?? null : null,
      discountPercent: priced ? v.discountPercent ?? 0 : 0,
      taxPercent: priced ? v.taxPercent ?? 0 : 0,
      taxCodeUuid: priced ? v.taxCodeUuid ?? null : null,
      promisedDeliveryDate: priced && v.promisedDeliveryDate ? toDateOnly(v.promisedDeliveryDate) : null,
      rejectionReasonUuid: type === 'REJECTED' ? v.rejectionReasonUuid : null,
      rejectionNotes: type === 'REJECTED' ? text(v.rejectionNotes) : null,
      alternativeForLineUuid: type === 'ALTERNATIVE' ? v.alternativeForLineUuid : null,
      alternativeNotes: type === 'ALTERNATIVE' ? text(v.alternativeNotes) : null,
      notes: text(v.notes)
    };
  }

  save() {
    if (this.isSaving) return;
    this.form.markAllAsTouched();
    const problem = this.problem;
    if (problem) { this.error = problem; return; }

    this.error = null;
    this.isSaving = true;
    const req = this.buildRequest();
    const uuid = this.quotation.uuid;
    const call: Observable<{ result?: string | null }> = this.line
      ? this.salesPreorderService.updateQuotationLine(uuid, this.line.uuid, req)
      : this.salesPreorderService.addQuotationLine(uuid, req);

    call.subscribe({
      next: (res) => {
        this.isSaving = false;
        this.saved.emit(this.line?.uuid ?? (res.result as string));
      },
      error: (err) => {
        this.isSaving = false;
        this.error = err?.error?.message ?? 'The line could not be saved.';
      }
    });
  }

  cancel() { this.cancelled.emit(); }

  // ── A34 PC-07/08/09: lead time ──────────────────────────────────────────────

  /** The saved line's calculation, as stored (updated by its own endpoint); null on a new line. */
  storedLeadTime: { date: string | null; days: number | null; at: string | null } | null = null;

  /** ⏱ on a priced line of a draft, for someone who may edit the quotation. */
  get canCalculateLeadTime(): boolean {
    return !this.readOnly && this.isPriced && this.authService.hasPermission('SALE_QUOTATION_EDIT');
  }

  get canSetPromisedDate(): boolean { return !this.readOnly && this.isPriced; }

  /** A saved line whose item and quantity are as saved: its own endpoint calculates for exactly this. */
  get isUnchangedSavedLine(): boolean {
    const v = this.form.getRawValue();
    return !!this.line && v.variantUuid === this.line.variantUuid && v.quantity === this.line.quantity;
  }

  /** Stored with the line → Apply drops the promised date; computed for an unsaved line → Apply writes it. */
  get leadTimeApplyMode(): LeadTimeApplyMode { return this.isUnchangedSavedLine ? 'clear-manual' : 'set-manual'; }

  /** The calculation shown as the line's: only one that is stored for the line as it stands. */
  get shownLeadTime(): { date: string | null; days: number | null; at: string | null } {
    if (!this.isUnchangedSavedLine) return { date: null, days: null, at: null };
    return this.storedLeadTime ?? {
      date: this.line?.calculatedDeliveryDate ?? null, days: this.line?.calculatedLeadTimeDays ?? null, at: this.line?.leadTimeCalculatedAt ?? null
    };
  }

  get promisedDateValue(): string | null {
    const d = this.form.get('promisedDeliveryDate')!.value as Date | null;
    return d ? toDateOnly(d) : null;
  }

  readonly leadTimeCalculator = (): Observable<LeadTimeCalculation> => {
    if (this.isUnchangedSavedLine) {
      return this.salesPreorderService.calculateQuotationLineLeadTime(this.quotation.uuid, this.line!.uuid).pipe(
        map(res => ({ leadTime: res.result!.leadTime, line: res.result!.line })));
    }
    const v = this.form.getRawValue();
    return this.leadTimeService.calculate({ variantUuid: v.variantUuid, quantity: v.quantity, routeUuid: null, requestedDate: null })
      .pipe(map(res => ({ leadTime: res.result! })));
  };

  onLeadTime(calc: LeadTimeCalculation) {
    const stored = calc.line as SaleQuotationLine | undefined;
    if (stored) {
      this.storedLeadTime = {
        date: stored.calculatedDeliveryDate ?? null, days: stored.calculatedLeadTimeDays ?? null, at: stored.leadTimeCalculatedAt ?? null
      };
    }
  }

  /** The promised date is the line's manual date; it is saved with the line. */
  onPromisedDate(date: string | null) {
    const control = this.form.get('promisedDeliveryDate')!;
    control.setValue(date ? fromDateOnly(date) : null);
    control.markAsDirty();
  }

  fieldInvalid(name: string): boolean {
    const c = this.form.get(name)!;
    return c.invalid && (c.dirty || c.touched);
  }
}
