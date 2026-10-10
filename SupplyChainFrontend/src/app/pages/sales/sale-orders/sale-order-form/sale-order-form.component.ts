import { Component, Inject, LOCALE_ID, OnInit, effect, inject, untracked } from '@angular/core';
import { CommonModule, formatDate, formatNumber } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, Subject, merge } from 'rxjs';
import { debounceTime, distinctUntilChanged, map } from 'rxjs/operators';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import {
  AbstractControl, FormArray, FormBuilder, FormControl, FormGroup, ReactiveFormsModule, ValidationErrors, Validators
} from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { AutoCompleteModule, AutoCompleteCompleteEvent } from 'primeng/autocomplete';
import { SelectButtonModule } from 'primeng/selectbutton';
import { MessageService } from 'primeng/api';

import {
  ProductVariantPickerComponent, VariantPickerSelection
} from '../../../../shared/product-variant-picker/product-variant-picker.component';
import {
  SaleOrderService, SaleOrderModel, SaleOrderLineRequest, CreateSaleOrderRequest, UpdateSaleOrderRequest,
  SaleOrderDefaultsModel, CustomerPoDuplicateModel, SaleOrderDeliveryPreviewModel, SaleOrderDeliveryPreviewRequest,
  DeliveryPreviewLineModel
} from '../../../../services/sale-order.service';
import {
  FulfillmentRoutesService, FulfillmentRouteModel, FulfillmentRouteSource, routesVisibleToOrg, unavailableRouteOptions
} from '../../../../services/fulfillment-routes.service';
import {
  LineRouteDisplay, PreviewLineRef, ROUTE_LEGEND, inheritPlaceholder, lineRouteDisplay
} from '../fulfillment-route-display';
import { DeliveryPreviewPanelComponent } from '../delivery-preview-panel/delivery-preview-panel.component';
import {
  SalesPreorderService, SaleQuotation, SaleQuotationLine, SaleQuotationListItem, ConvertQuotationToOrderRequest
} from '../../../../services/sales-preorder.service';
import { fromDateOnly, toDateOnly } from '../../../../shared/date-only';
import { formatCode } from '../../../../shared/format-code';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { InventoryService, ProductListItemModel } from '../../../../services/inventory.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { AddressService } from '../../../../services/address.service';
import { CurrenciesService, CurrencyModel } from '../../../../services/currencies.service';
import { FinanceSetupService, TaxCodeModel } from '../../../../services/finance-setup.service';
import { AddressModel, AddressRequest } from '../../../../services/logistics.service';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { taxCodeLabel as formatTaxCodeLabel } from '../../../../shared/tax-code-label';
import { LeadTimeService } from '../../../../services/lead-time.service';
import { LeadTimeCalculation, LeadTimePopoverComponent } from '../../lead-time-popover/lead-time-popover.component';
import { MoneyService } from '../../../../services/money.service';
import { DocCurrencyPanelComponent } from '../../../../shared/doc-currency/doc-currency-panel.component';
import { DocCurrencyInfo } from '../../../../shared/doc-currency/doc-currency';
import { FLOW } from '../../../../shared/flow';

/** A35 D-9 — a customer's default sale currency (API-CONTRACT.md §5); absent on servers before A35. */
type PartnerWithSaleCurrency = BusinessPartnerModel & { defaultSaleCurrencyId?: string | null };

/**
 * Where a line's indicative unit price stands. The server prices the order when it is saved; this only previews it.
 * `noRate`: the price is in another currency and the server has no way to convert it (no rate on file for the
 * order date, or a currency with no ISO code), so saving will be refused.
 */
export type PriceState = 'none' | 'loading' | 'found' | 'missing' | 'noRate' | 'unavailable';

/** A tax code as the line's dropdown offers it. */
export interface TaxCodeOption {
  /** "GST17 · 17%" — the code and the rate the line will be taxed at. */
  label: string;
  value: string;
  ratePercent: number;
  /** The code's own text, e.g. "GST17". */
  code: string;
}

// ── Money, the way the server works it out ────────────────────────────────────
// The server computes in .NET decimal and rounds to the cent half away from zero. Binary floating point
// cannot: 3 x 2.50 x 1.17 is 8.774999… as a double, which rounds to 8.77 where the server has 8.78. So the
// preview works in exact decimals too — every input is a decimal number the server sent or the user typed.

/** digits x 10^-scale, exactly. */
interface Decimal { digits: bigint; scale: number; }

function toDecimal(n: number): Decimal {
  // A double's shortest round-trip text is the decimal it was parsed from (JSON, or a typed figure).
  const m = /^(-?)(\d+)(?:\.(\d+))?(?:e([+-]?\d+))?$/i.exec(String(n));
  if (!m) throw new RangeError(`${n} is not a finite number.`);
  const [, sign, whole, fraction = '', exponent = '0'] = m;
  let digits = BigInt(sign + whole + fraction);
  let scale = fraction.length - Number(exponent);
  if (scale < 0) { digits *= 10n ** BigInt(-scale); scale = 0; }
  return { digits, scale };
}

function times(a: Decimal, b: Decimal): Decimal {
  return { digits: a.digits * b.digits, scale: a.scale + b.scale };
}

function plus(a: Decimal, b: Decimal): Decimal {
  const scale = Math.max(a.scale, b.scale);
  return {
    digits: a.digits * 10n ** BigInt(scale - a.scale) + b.digits * 10n ** BigInt(scale - b.scale),
    scale
  };
}

/** n / 100, exactly. */
function percent(n: number): Decimal {
  const d = toDecimal(n);
  return { digits: d.digits, scale: d.scale + 2 };
}

const ZERO: Decimal = { digits: 0n, scale: 0 };
const ONE: Decimal = { digits: 1n, scale: 0 };

/** Whole cents, half away from zero — .NET's Math.Round(x, 2, MidpointRounding.AwayFromZero). */
function cents(d: Decimal): bigint {
  if (d.scale <= 2) return d.digits * 10n ** BigInt(2 - d.scale);
  const divisor = 10n ** BigInt(d.scale - 2);
  let whole = d.digits / divisor;                       // truncated toward zero
  const rest = d.digits % divisor;
  if ((rest < 0n ? -rest : rest) * 2n >= divisor) whole += d.digits < 0n ? -1n : 1n;
  return whole;
}

function fromCents(c: bigint): number {
  return Number(`${c}e-2`);
}

/** price x rate to the cent, half away from zero: the server's ExchangeRateMath.Convert. */
export function convertAtRate(amount: number, rate: number): number {
  return fromCents(cents(times(toDecimal(amount), toDecimal(rate))));
}

/** qty x price x (1 - disc%) x (1 + tax%) to the cent: the server's SaleOrderService.ComputeLineTotal. */
export function saleOrderLineTotal(quantity: number, unitPrice: number, discountPercent: number, taxPercent: number): number {
  const afterDiscount = plus(ONE, times(percent(discountPercent), { digits: -1n, scale: 0 }));
  const withTax = plus(ONE, percent(taxPercent));
  return fromCents(cents(times(times(times(toDecimal(quantity), toDecimal(unitPrice)), afterDiscount), withTax)));
}

/**
 * The order's grand total the way the server's ApplyTotals makes it: the subtotal, the discount and the tax
 * are each summed over every line and rounded once, then subtotal - discount + tax. Not the sum of the
 * rounded line totals, which can be a cent or two away from it.
 */
export function saleOrderGrandTotal(
  lines: { quantity: number; unitPrice: number; discountPercent: number; taxPercent: number }[]
): number {
  let subtotal = ZERO, discount = ZERO, tax = ZERO;
  for (const l of lines) {
    const base = times(toDecimal(l.quantity), toDecimal(l.unitPrice));
    const lineDiscount = times(base, percent(l.discountPercent));
    const afterDiscount = plus(base, times(lineDiscount, { digits: -1n, scale: 0 }));
    subtotal = plus(subtotal, base);
    discount = plus(discount, lineDiscount);
    tax = plus(tax, times(afterDiscount, percent(l.taxPercent)));
  }
  return fromCents(cents(subtotal) - cents(discount) + cents(tax));
}

/** Today as the server sees it for a new order: the UTC calendar day, yyyy-MM-dd. */
function todayUtc(): string {
  return new Date().toISOString().slice(0, 10);
}

/** The customer must be one picked from the list, not text typed into the box. */
function customerPicked(control: AbstractControl): ValidationErrors | null {
  const value = control.value as BusinessPartnerModel | string | null;
  return value && typeof value === 'object' && !!value.uuid ? null : { customerRequired: true };
}

function atLeastOneLine(control: AbstractControl): ValidationErrors | null {
  return (control as FormArray).length > 0 ? null : { noLines: true };
}

/** A32 PD-07 — a new order is typed in (MANUAL on the server), or made from a quotation the customer accepted. */
export type SaleOrderCreateMode = 'DIRECT' | 'QUOTATION';

/** BR-C3-04 — the customer's PO reference is free text, at most 50 characters. */
const CUSTOMER_PO_MAX_LENGTH = 50;

@Component({
  selector: 'app-sale-order-form',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule,
    ButtonModule, ToastModule, TooltipModule, DialogModule, DropdownModule, CalendarModule,
    InputNumberModule, InputTextModule, TextareaModule, AutoCompleteModule, SelectButtonModule,
    ProductVariantPickerComponent, DeliveryPreviewPanelComponent, LeadTimePopoverComponent, DocCurrencyPanelComponent,
    ...FLOW
  ],
  templateUrl: './sale-order-form.component.html',
  styleUrls: ['./sale-order-form.component.scss'],
  providers: [MessageService]
})
export class SaleOrderFormComponent implements OnInit {
  /** The order being edited; null when a new one is being made. */
  uuid: string | null = null;
  order: SaleOrderModel | null = null;

  isLoading = false;
  isSaving = false;
  notFound = false;
  /** An order past DRAFT is read-only. */
  notEditable = false;

  form: FormGroup;

  modeOptions: { label: string; value: string; icon: string; disabled?: boolean }[] = [
    { label: 'Ship to the customer', value: 'SHIP',        icon: 'pi pi-truck' },
    { label: 'Customer collects',    value: 'SELF_PICKUP', icon: 'pi pi-shopping-cart' }
  ];

  /** True when the organization has customer pickup switched off, so every order is shipped. */
  pickupOff = false;

  minDate = new Date(new Date().setHours(0, 0, 0, 0));

  products: ProductListItemModel[] = [];
  customerSuggestions: BusinessPartnerModel[] = [];

  addresses: AddressModel[] = [];
  isLoadingAddresses = false;

  // The new-address dialog.
  addressDialogVisible = false;
  addressForm: FormGroup;
  isSavingAddress = false;

  currencyOptions: { label: string; value: string }[] = [];
  isLoadingCurrencies = false;
  /** The currencies as loaded, with their ISO codes: a price in another currency is converted by code. */
  private currencies: CurrencyModel[] = [];

  // A35 P3-13 — the organization's currencies (api/currencies) narrow the picker to the active ones (D-1).
  private readonly money = inject(MoneyService);
  private formCurrencyCache: DocCurrencyInfo | null = null;

  /**
   * SAP alignment (S-3) — the organization's active sales tax codes, default first. Empty until they load,
   * and for an organization that has none: the lines then take a tax percentage as they always did.
   */
  taxCodes: TaxCodeModel[] = [];
  /** True once the list has come back, so a code missing from it is known to be retired rather than not yet loaded. */
  private taxCodesLoaded = false;
  /**
   * Codes the loaded order's lines carry that are no longer offered (deactivated since), so such a line
   * still shows what it was taxed at. The server refuses them on save, saying which.
   */
  private retiredTaxCodes: TaxCodeOption[] = [];

  /** Kept from the loaded order so an edit does not reset what the form does not show. */
  private intimationDepartmentId?: number;

  /** The latest price request per line, so a slow answer for an old quantity cannot overwrite a newer one. */
  private priceRequests = new Map<AbstractControl, number>();
  private requestSeq = 0;

  // ── A32 PD-07: how a new order is made ──────────────────────────────────────

  createMode: SaleOrderCreateMode = 'DIRECT';
  readonly createModeControl = new FormControl<SaleOrderCreateMode>('DIRECT', { nonNullable: true });
  sourceModeOptions: { label: string; value: SaleOrderCreateMode; icon: string; disabled?: boolean }[] = [];

  readonly quotationControl = new FormControl<SaleQuotationListItem | string | null>(null);
  quotationSuggestions: SaleQuotationListItem[] = [];
  /** The ACCEPTED quotation the order will be converted from, read in full. */
  selectedQuotation: SaleQuotation | null = null;
  isLoadingQuotation = false;

  /** BR-C3-05 — other orders already carrying the typed customer PO reference: a warning, never a block. */
  poDuplicates: CustomerPoDuplicateModel[] = [];
  readonly customerPoMaxLength = CUSTOMER_PO_MAX_LENGTH;

  // ── A33: routes and the delivery preview ────────────────────────────────────

  /** The organization's active routes, for each line's Route dropdown. */
  activeRoutes: FulfillmentRouteModel[] = [];
  /** A37 RTE-01 — routes the server marks unavailable (module off): listed disabled with the reason. */
  unavailableRoutes: FulfillmentRouteModel[] = [];
  private routesRequested = false;
  /** A loaded line's override that is no longer offered (deactivated, deleted), named as the server last knew it. */
  private retiredRouteLabels = new Map<string, string>();
  /** The server said routes do not apply to this organization (D-11). */
  private serverRoutesOff = false;

  preview: SaleOrderDeliveryPreviewModel | null = null;
  /** The line controls the preview was asked for, in request order: the server's line N is previewControls[N-1]. */
  private previewControls: AbstractControl[] = [];
  private previewSeq = 0;
  isLoadingPreview = false;
  previewFailed = false;
  /** Asks for a preview when something outside the form changes (the organization's modules arriving). */
  private readonly previewTrigger$ = new Subject<void>();

  constructor(
    private fb: FormBuilder,
    private route: ActivatedRoute,
    private router: Router,
    private saleOrderService: SaleOrderService,
    private routesService: FulfillmentRoutesService,
    private preorderService: SalesPreorderService,
    private partnerService: BusinessPartnerService,
    private inventoryService: InventoryService,
    private pricingService: PricingRuleService,
    private addressService: AddressService,
    private currenciesService: CurrenciesService,
    private financeSetupService: FinanceSetupService,
    private tenantService: TenantService,
    public authService: AuthService,
    private messageService: MessageService,
    private leadTimeService: LeadTimeService,
    @Inject(LOCALE_ID) private locale: string
  ) {
    this.form = this.fb.group({
      customer:             [null as BusinessPartnerModel | string | null, [customerPicked]],
      deliveryMode:         ['SHIP', Validators.required],
      shippingAddressId:    [null as string | null, Validators.required],
      expectedDeliveryDate: [null as Date | null],
      currencyId:           [null as string | null, Validators.required],
      notes:                ['', Validators.maxLength(2000)],
      // A32 C3 — optional on a direct order; a duplicate in the organization is only a warning (BR-C3-05).
      customerPoReference:  ['', Validators.maxLength(CUSTOMER_PO_MAX_LENGTH)],
      customerPoDate:       [null as Date | null],
      lines:                this.fb.array([], [atLeastOneLine])
    });

    this.sourceModeOptions = [
      { label: 'Create directly', value: 'DIRECT', icon: 'pi pi-pencil' },
      { label: 'From an accepted quotation', value: 'QUOTATION', icon: 'pi pi-file-import', disabled: !this.canUseQuotations }
    ];

    this.addressForm = this.fb.group({
      line1:          ['', [Validators.required, Validators.maxLength(200)]],
      line2:          ['', Validators.maxLength(200)],
      cityName:       ['', [Validators.required, Validators.maxLength(100)]],
      state:          [''],
      postalCode:     [''],
      countryName:    ['', [Validators.required, Validators.maxLength(100)]],
      contactName:    [''],
      contactPhone:   [''],
      contactEmail:   ['', Validators.email]
    });

    // The tenant is loaded once by the shell, and may arrive after the currency list or before it.
    effect(() => {
      this.tenantService.tenant();
      this.applyDefaultCurrency();
      // A33 — whether routes apply depends on the organization's modules (D-11).
      untracked(() => {
        this.ensureRoutesLoaded();
        this.previewTrigger$.next();
      });
    });

    // A line is priced in the order's currency, so another currency is another price for every line.
    this.form.get('currencyId')!.valueChanges.pipe(distinctUntilChanged(), takeUntilDestroyed()).subscribe(() => {
      this.lines.controls.forEach((_, i) => this.refreshPrice(i));
    });

    // A33 PC-08 — the preview follows the lines, their routes, the delivery mode and the address, a moment after
    // the last change; nothing else asks for a new one.
    merge(this.form.valueChanges, this.previewTrigger$).pipe(
      map(() => this.previewKey()),
      debounceTime(PREVIEW_DEBOUNCE_MS),
      distinctUntilChanged(),
      takeUntilDestroyed()
    ).subscribe(() => this.refreshPreview());
  }

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid');

    this.isLoadingCurrencies = true;
    this.currenciesService.getAll().subscribe({
      next: (res) => {
        this.isLoadingCurrencies = false;
        this.currencies = res.result ?? [];
        this.rebuildCurrencyOptions();
        this.applyDefaultCurrency();
      },
      error: () => {
        this.isLoadingCurrencies = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'The currency list could not be loaded.' });
      }
    });
    // A35 — whichever list arrives second narrows the picker; without the org list every currency stays offered.
    this.money.load().subscribe(() => { this.rebuildCurrencyOptions(); this.applyDefaultCurrency(); });

    this.inventoryService.getProducts({ activeOnly: true, pageSize: 500, availableFor: 'RETAIL' }).subscribe({
      next: (res) => { this.products = res.result?.data ?? []; },
      error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'The product list could not be loaded.' })
    });

    // Without the defaults the form still works: orders start as shipped, and the server has the last word.
    this.saleOrderService.getDefaults().subscribe({
      next: (res) => this.applyDefaults(res.result),
      error: () => { /* nothing to tell the user: the answer only sets where the form starts */ }
    });

    // Without tax codes the form still works too: each line takes a tax percentage, as before codes existed.
    this.financeSetupService.getTaxCodes('SALES').subscribe({
      next: (res) => {
        this.taxCodes = (res.result ?? []).filter(c => c.isActive);
        this.taxCodesLoaded = true;
        this.applyDefaultTaxCode();
        this.applyCurrentTaxRates();
      },
      error: () => { this.taxCodes = []; }
    });

    this.ensureRoutesLoaded();

    if (this.uuid) this.loadOrder(this.uuid);
    else {
      this.addLine();
      // A32 PD-07 — "?source=quotation" opens on the quotation route; "?quotation=<uuid>" with that one picked.
      const query = this.route.snapshot.queryParamMap;
      const quotationUuid = query?.get('quotation');
      if (quotationUuid || query?.get('source') === 'quotation') this.setCreateMode('QUOTATION');
      if (quotationUuid && this.isFromQuotation) this.loadQuotation(quotationUuid);
    }
  }

  get isEdit(): boolean { return !!this.uuid; }

  // ── A32 PD-07: direct, or from an accepted quotation ────────────────────────

  /** Picking a quotation reads the quotation list; converting it is SALE_ORDER_CREATE, which this page's route needs. */
  get canUseQuotations(): boolean { return this.authService.hasPermission('SALE_QUOTATION_VIEW'); }

  get isFromQuotation(): boolean { return !this.isEdit && this.createMode === 'QUOTATION'; }

  setCreateMode(mode: SaleOrderCreateMode) {
    if (this.isEdit || mode === this.createMode || (mode === 'QUOTATION' && !this.canUseQuotations)) {
      this.createModeControl.setValue(this.createMode, { emitEvent: false });
      return;
    }
    this.createMode = mode;
    this.createModeControl.setValue(mode, { emitEvent: false });

    // Either way the customer starts over: a quotation brings its own, a direct order is chosen from the list.
    this.selectedQuotation = null;
    this.quotationControl.setValue(null);
    const customer = this.form.get('customer')!;
    customer.setValue(null);
    customer.enable();
    customer.markAsUntouched();
    this.onCustomerCleared();
  }

  /** Accepted quotations only — nothing else can become an order. */
  searchQuotations(event: AutoCompleteCompleteEvent) {
    const search = (event.query ?? '').trim();
    this.preorderService.getQuotations({ status: 'ACCEPTED', pageSize: 20, ...(search ? { search } : {}) }).subscribe({
      next: (res) => { this.quotationSuggestions = res.result?.data ?? []; },
      error: () => { this.quotationSuggestions = []; }
    });
  }

  /** Clicking into the empty box lists what there is, as the customer box does. */
  onQuotationFieldFocus() {
    if (this.quotationSuggestions.length === 0 && !this.selectedQuotation) this.searchQuotations({ query: '' } as AutoCompleteCompleteEvent);
  }

  onQuotationSelected(item: SaleQuotationListItem | null) {
    if (item?.uuid) this.loadQuotation(item.uuid);
  }

  clearQuotation() {
    this.selectedQuotation = null;
    const customer = this.form.get('customer')!;
    customer.setValue(null);
    this.onCustomerCleared();
  }

  /** Reads the quotation in full: its customer, currency and lines become the order's. */
  private loadQuotation(uuid: string) {
    this.isLoadingQuotation = true;
    this.preorderService.getQuotation(uuid).subscribe({
      next: (res) => {
        this.isLoadingQuotation = false;
        const q = res.result;
        if (!q) {
          this.clearQuotation();
          this.messageService.add({ severity: 'error', summary: 'Not found', detail: 'That quotation could not be found.' });
          return;
        }
        if (q.status !== 'ACCEPTED') {
          this.clearQuotation();
          this.quotationControl.setValue(null);
          this.messageService.add({
            severity: 'warn', summary: 'Not an accepted quotation',
            detail: `${q.quotationNumber} is ${formatCode(q.status)}. Only a quotation the customer accepted can become an order.`
          });
          return;
        }

        this.selectedQuotation = q;
        this.quotationControl.setValue({ uuid: q.uuid, quotationNumber: q.quotationNumber } as SaleQuotationListItem, { emitEvent: false });
        const customer = this.form.get('customer')!;
        customer.setValue({ uuid: q.partnerId, companyName: q.partnerName ?? q.partnerId } as BusinessPartnerModel);
        customer.disable();
        this.form.get('shippingAddressId')?.setValue(null);
        this.addresses = [];
        this.loadAddresses(q.partnerId);
      },
      error: () => {
        this.isLoadingQuotation = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'The quotation could not be loaded.' });
      }
    });
  }

  /** "2026-10-31T00:00:00" is the 31st, wherever the reader is. */
  dateOnly(iso: string): Date { return fromDateOnly(iso); }

  /** What the order will carry: the lines the customer accepted, at the quoted prices (a counter offer is not one). */
  get acceptedLines(): SaleQuotationLine[] {
    return (this.selectedQuotation?.lines ?? []).filter(l => l.lineType !== 'REJECTED' && l.customerResponse === 'ACCEPTED');
  }

  get acceptedTotal(): number {
    return this.acceptedLines.reduce((sum, l) => sum + (l.lineTotal ?? 0), 0);
  }

  // ── A32 C3: customer PO ─────────────────────────────────────────────────────

  /** On leaving the reference box: is it on another order already? A warning only (BR-C3-05). */
  onCustomerPoBlur() {
    const reference = ((this.form.get('customerPoReference')?.value as string | null) ?? '').trim();
    if (!reference) { this.poDuplicates = []; return; }
    this.saleOrderService.checkCustomerPo(reference, this.uuid ?? undefined).subscribe({
      next: (res) => { this.poDuplicates = res.result ?? []; },
      error: () => { this.poDuplicates = []; }
    });
  }

  /** The customer PO as the server takes it: trimmed, date-only, and left out when not given. */
  private customerPoFields(): { customerPoReference?: string; customerPoDate?: string } {
    const v = this.form.getRawValue();
    const reference = ((v.customerPoReference as string | null) ?? '').trim();
    return {
      ...(reference ? { customerPoReference: reference } : {}),
      ...(v.customerPoDate ? { customerPoDate: toDateOnly(v.customerPoDate as Date) } : {})
    };
  }

  /** POST …/convert-to-order: the order's own details; partner, currency and lines come from the quotation. */
  buildConvertRequest(): ConvertQuotationToOrderRequest {
    const v = this.form.getRawValue();
    const notes = ((v.notes as string | null) ?? '').trim();
    return {
      ...(v.expectedDeliveryDate ? { expectedDeliveryDate: toDateOnly(v.expectedDeliveryDate as Date) } : {}),
      deliveryMode: v.deliveryMode,
      // A33 D-4 — the same rule as saving: shipped, or a line's route ships.
      ...(this.showAddress && v.shippingAddressId ? { shippingAddressId: v.shippingAddressId as string } : {}),
      ...(notes ? { notes } : {}),
      ...this.customerPoFields()
    };
  }

  /** The first thing stopping a conversion, in words for the toast. */
  private firstQuotationProblem(): string | null {
    const q = this.selectedQuotation;
    if (!q) return 'Choose an accepted quotation.';
    if (this.acceptedLines.length === 0) {
      return `${q.quotationNumber} has no line the customer accepted, so there is nothing to convert.`;
    }
    if (this.isShip && !this.form.get('shippingAddressId')?.value) return 'A shipped order needs a shipping address.';
    if (this.form.get('customerPoReference')?.invalid) return this.customerPoTooLong;
    if (this.form.get('notes')?.invalid) return 'The notes are too long.';
    return null;
  }

  private get customerPoTooLong(): string {
    return `The customer PO reference can be at most ${CUSTOMER_PO_MAX_LENGTH} characters.`;
  }

  private convert() {
    const q = this.selectedQuotation!;
    this.preorderService.convertQuotationToOrder(q.uuid, this.buildConvertRequest()).subscribe({
      next: (res) => {
        this.isSaving = false;
        this.messageService.add({
          severity: 'success', summary: 'Order created',
          detail: `${q.quotationNumber} is now a draft order. Review it, then confirm it.`
        });
        if (res.result) this.router.navigate(['/portal/pages/sales/orders', res.result]);
        else this.router.navigate(['/portal/pages/sales/orders']);
      },
      error: (err) => this.failed(err)
    });
  }

  get lines(): FormArray { return this.form.get('lines') as FormArray; }

  get isShip(): boolean { return this.form.get('deliveryMode')?.value === 'SHIP'; }

  /** True once the organization is known and has no base currency, so each new order needs one chosen. */
  get noBaseCurrency(): boolean {
    const tenant = this.tenantService.tenant();
    return !!tenant && !tenant.baseCurrency;
  }

  /** A new order starts in the organization's base currency, unless one has been chosen already. */
  private applyDefaultCurrency() {
    if (this.isEdit) return;
    const control = this.form.get('currencyId');
    if (!control || control.value) return;

    const base = this.tenantService.tenant()?.baseCurrency;
    if (base && this.currencyOptions.some(o => o.value === base)) control.setValue(base);
  }

  /**
   * A35 D-1 — the picker offers the organization's active currencies (the global catalogue entries it has set up and
   * not deactivated), plus the one the order already has. Until the org list is known, every currency is offered.
   */
  private rebuildCurrencyOptions() {
    const org = this.money.currencies();
    const keep = (this.form.get('currencyId')?.value as string | null) ?? this.order?.currencyId ?? null;
    const offered = org.length
      ? this.currencies.filter(c => c.id === keep || this.money.find(c.id)?.isActive === true)
      : this.currencies;
    this.currencyOptions = offered.map(c => ({ label: c.code ? `${c.name} (${c.code})` : c.name, value: c.id }));
  }

  /**
   * A35 D-9 / D-14 — a new order follows the customer's default sale currency (else the sale base) until the user picks
   * one by hand. An order from a quotation takes the quotation's.
   */
  private applyCustomerCurrency(partner: BusinessPartnerModel | null) {
    if (this.isEdit || this.isFromQuotation) return;
    const control = this.form.get('currencyId');
    if (!control || control.dirty) return;
    const own = (partner as PartnerWithSaleCurrency | null)?.defaultSaleCurrencyId || null;
    // A customer with no default leaves the currency alone, unless it was the previous customer's default.
    const fromPrevious = this.currencyFromCustomer !== null && control.value === this.currencyFromCustomer;
    const wanted = own ?? (fromPrevious ? this.tenantService.tenant()?.baseCurrency ?? null : null);
    this.currencyFromCustomer = null;
    if (!wanted || !this.currencyOptions.some(o => o.value === wanted)) return;
    if (own) this.currencyFromCustomer = own;
    if (wanted !== control.value) control.setValue(wanted);
  }
  private currencyFromCustomer: string | null = null;

  /** The header's currency panel: the chosen currency, not locked (a form only edits drafts). Cached for change detection. */
  get formCurrency(): DocCurrencyInfo | null {
    const id = (this.form.get('currencyId')?.value as string | null) ?? null;
    if (!id) return null;
    const code = this.currencies.find(c => c.id === id)?.code ?? null;
    if (this.formCurrencyCache?.currencyId !== id || this.formCurrencyCache?.currencyCode !== code) {
      this.formCurrencyCache = { currencyId: id, currencyCode: code, exchangeRate: null };
    }
    return this.formCurrencyCache;
  }

  /** The customer, once one has been picked from the list. */
  get customer(): BusinessPartnerModel | null {
    const v = this.form.get('customer')?.value;
    return v && typeof v === 'object' && v.uuid ? v : null;
  }

  // ── Loading an order to edit ────────────────────────────────────────────────

  private loadOrder(uuid: string) {
    this.isLoading = true;

    this.saleOrderService.getSaleOrderById(uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (!res.success || !res.result) { this.notFound = true; return; }

        this.order = res.result;
        if (res.result.status !== 'DRAFT') { this.notEditable = true; return; }
        this.patchFrom(res.result);
      },
      error: (err) => {
        this.isLoading = false;
        this.notFound = err?.status === 404;
        if (!this.notFound) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load the sale order.' });
        }
      }
    });
  }

  private patchFrom(order: SaleOrderModel) {
    this.intimationDepartmentId = order.intimationDepartmentId;

    this.form.patchValue({
      currencyId: order.currencyId,
      deliveryMode: order.deliveryMode,
      shippingAddressId: order.shippingAddressId ?? null,
      expectedDeliveryDate: order.expectedDeliveryDate ? fromDateOnly(order.expectedDeliveryDate) : null,
      notes: order.notes ?? '',
      customerPoReference: order.customerPoReference ?? '',
      customerPoDate: order.customerPoDate ? fromDateOnly(order.customerPoDate) : null
    });
    // A35 — a draft in a currency deactivated since keeps it on offer.
    this.rebuildCurrencyOptions();
    if (order.routesEnabled === false) this.serverRoutesOff = true;
    // A stored address is kept (and sent only while the field is shown): a collected draft may need it (D-4).
    this.onModeChange(false);

    this.lines.clear();
    this.retiredTaxCodes = [];
    this.retiredRouteLabels.clear();
    for (const l of order.lines.filter(l => l.status !== 'CANCELLED')) {
      // A line keeps the code it has (and one with none stays without): the default is only for new lines.
      const group = this.newLine(false);
      group.patchValue({
        variantUuid: l.variantUuid,
        label: l.itemDescription ?? l.variantSku ?? `Variant ${l.variantUuid}`,
        sku: l.variantSku ?? '',
        pickerOpen: false,
        quantity: l.quantity,
        discountPercent: l.discountPercent,
        taxPercent: l.taxPercent,
        taxCodeUuid: l.taxCodeUuid ?? null,
        // A33 — the line's own override (null = inherits); sent back on every save, the lines being rebuilt.
        fulfillmentRouteUuid: l.fulfillmentRouteUuid ?? null,
        unitPrice: l.unitPrice,
        priceState: 'found',
        // A34 — the line's dates, kept as they were saved and sent back as they are unless changed here.
        manualDeliveryDate: l.manualDeliveryDate ? l.manualDeliveryDate.slice(0, 10) : null,
        calculatedLeadTimeDays: l.calculatedLeadTimeDays ?? null,
        calculatedDeliveryDate: l.calculatedDeliveryDate ? l.calculatedDeliveryDate.slice(0, 10) : null,
        leadTimeCalculatedAt: l.leadTimeCalculatedAt ?? null,
        leadTimeBasis: l.calculatedDeliveryDate ? leadTimeBasis(l.variantUuid, l.quantity) : null,
        routeWarning: l.routeWarning ?? null
      });
      this.lines.push(group);

      if (l.fulfillmentRouteUuid) {
        const name = (l.routeSource === 'LINE_OVERRIDE' ? l.effectiveRouteName || l.effectiveRouteCode : null) ?? 'Route';
        const why = l.routeBlocker === 'ROUTE_INACTIVE' ? ' (inactive)' : l.routeBlocker === 'ROUTE_UNKNOWN' ? ' (no longer available)' : '';
        this.retiredRouteLabels.set(l.fulfillmentRouteUuid, name + why);
      }

      if (l.taxCodeUuid && !this.retiredTaxCodes.some(o => o.value === l.taxCodeUuid)) {
        const code = l.taxCode ?? 'Code';
        this.retiredTaxCodes.push({
          value: l.taxCodeUuid, ratePercent: l.taxPercent, code, label: this.taxCodeLabel(code, l.taxPercent)
        });
      }
    }
    // The codes may have come back first.
    this.applyCurrentTaxRates();

    // The customer cannot be changed on an order that exists: that is a different act from amending it.
    this.partnerService.getPartnerById(order.partnerId).subscribe({
      next: (res) => {
        const partner = res.result ?? ({ uuid: order.partnerId, companyName: order.partnerId } as BusinessPartnerModel);
        this.form.get('customer')?.setValue(partner);
        this.form.get('customer')?.disable();
      },
      error: () => {
        this.form.get('customer')?.setValue({ uuid: order.partnerId, companyName: order.partnerId } as BusinessPartnerModel);
        this.form.get('customer')?.disable();
      }
    });

    this.loadAddresses(order.partnerId, order.shippingAddressId);
  }

  // ── Customer ────────────────────────────────────────────────────────────────

  searchCustomers(event: AutoCompleteCompleteEvent) {
    this.runCustomerSearch(event.query);
  }

  // The field only searched on typed input (or the tiny dropdown-arrow icon, easy to miss on a
  // field that otherwise looks like a plain dropdown) — clicking straight into the box did
  // nothing and looked exactly like "no customers exist". Loading the current candidates as soon
  // as the field is focused means any click into it shows something immediately.
  onCustomerFieldFocus() {
    if (this.customerSuggestions.length === 0 && !this.customer) this.runCustomerSearch('');
  }

  private runCustomerSearch(query: string) {
    this.partnerService.getPartners({ isCustomer: true, active: true, search: query, pageSize: 20 }).subscribe({
      next: (res) => { this.customerSuggestions = res.result?.data ?? []; },
      error: () => { this.customerSuggestions = []; }
    });
  }

  onCustomerSelected(partner: BusinessPartnerModel) {
    // A different customer has different addresses and, possibly, different prices.
    this.form.get('shippingAddressId')?.setValue(null);
    this.addresses = [];
    if (partner?.uuid) this.loadAddresses(partner.uuid);
    this.applyCustomerCurrency(partner);
    this.lines.controls.forEach((_, i) => this.refreshPrice(i));
  }

  onCustomerCleared() {
    this.form.get('shippingAddressId')?.setValue(null);
    this.addresses = [];
  }

  // ── Delivery mode and address ───────────────────────────────────────────────

  /** What the organization's settings say: where a new order starts, and whether pickup is offered. */
  private applyDefaults(defaults?: SaleOrderDefaultsModel | null) {
    if (!defaults) return;

    this.pickupOff = !defaults.selfPickupEnabled;
    this.modeOptions = this.modeOptions.map(o => o.value === 'SELF_PICKUP' ? { ...o, disabled: this.pickupOff } : o);

    // A new order starts as the organization says, unless the user has already chosen; an order being
    // edited keeps the mode it has.
    const mode = this.form.get('deliveryMode')!;
    if (!this.isEdit && mode.pristine && defaults.deliveryMode) {
      mode.setValue(defaults.deliveryMode);
      this.onModeChange();
    }
  }

  /**
   * A shipped order cannot do without an address. A collected one has none, unless a line's route ships (A33 D-4,
   * REV-03): then the address is kept and offered, though a draft may still be saved without it (Confirm is the gate).
   * @param clearAddress False when loading a draft: its stored address stays until the preview says whether it is needed.
   */
  onModeChange(clearAddress = true) {
    const address = this.form.get('shippingAddressId')!;
    if (this.isShip) {
      address.addValidators(Validators.required);
    } else {
      address.removeValidators(Validators.required);
      if (clearAddress && !this.routesNeedShipping) address.setValue(null);
    }
    address.updateValueAndValidity();
  }

  /**
   * A33 D-4 — some line's route has a Ship step (or the server asks for an address), so the order needs a shipping
   * address whatever its header mode. From the preview; before the first one, from the loaded draft.
   */
  get routesNeedShipping(): boolean {
    if (!this.routesEnabled) return false;
    if (this.preview) {
      return this.preview.blockers.some(b => b.code === 'SHIPPING_ADDRESS_REQUIRED')
          || this.preview.groups.some(g => g.requiresShipping)
          || this.preview.lines.some(l => (l.effectiveRouteSteps ?? []).includes('SHIP'));
    }
    const o = this.order;
    return !!o && ((o.confirmBlockers ?? []).some(b => b.code === 'SHIPPING_ADDRESS_REQUIRED')
          || o.lines.some(l => (l.effectiveRouteSteps ?? []).includes('SHIP')));
  }

  /** The address field is on screen, and its value is sent, for a shipped order or one with a line that ships. */
  get showAddress(): boolean { return this.isShip || this.routesNeedShipping; }

  private loadAddresses(customerUuid: string, select?: string | null) {
    this.isLoadingAddresses = true;
    this.addressService.getAddresses(customerUuid).subscribe({
      next: (res) => {
        this.isLoadingAddresses = false;
        this.addresses = res.result ?? [];
        if (select) this.ensureAddressOption(select);
      },
      error: () => {
        this.isLoadingAddresses = false;
        this.addresses = [];
        if (select) this.ensureAddressOption(select);
      }
    });
  }

  /** The order's own address may have been folded into a newer copy of the same place, or be one the book no longer lists. */
  private ensureAddressOption(uuid: string) {
    if (this.addresses.some(a => a.uuid === uuid)) return;
    this.addressService.getAddress(uuid).subscribe({
      next: (res) => { if (res.result) this.addresses = [res.result, ...this.addresses]; },
      error: () => { /* the id is still sent as it was; only its label is missing */ }
    });
  }

  get addressOptions(): { label: string; value: string }[] {
    return this.addresses.map(a => ({ label: this.formatAddress(a), value: a.uuid }));
  }

  formatAddress(a: AddressModel): string {
    const cityLine = [a.cityName, a.postalCode].filter(Boolean).join(' ');
    return [a.line1, a.line2, cityLine, a.countryName].filter(Boolean).join(', ');
  }

  get canAddAddress(): boolean {
    return !!this.customer && this.authService.hasPermission('SALE_ORDER_CREATE');
  }

  openAddressDialog() {
    if (!this.canAddAddress) return;
    this.addressForm.reset({ line1: '', line2: '', cityName: '', state: '', postalCode: '', countryName: '', contactName: '', contactPhone: '', contactEmail: '' });
    this.addressDialogVisible = true;
  }

  /** What the new-address dialog will send. */
  buildAddressRequest(): AddressRequest {
    const v = this.addressForm.getRawValue();
    const text = (s: string | null) => (s ?? '').trim() || undefined;

    return {
      line1: (v.line1 ?? '').trim(),
      line2: text(v.line2),
      cityName: (v.cityName ?? '').trim(),
      state: text(v.state),
      postalCode: text(v.postalCode),
      countryName: (v.countryName ?? '').trim(),
      contactName: text(v.contactName),
      contactPhone: text(v.contactPhone),
      contactEmail: text(v.contactEmail),
      addressType: 'CUSTOMER',
      consigneeUuid: this.customer?.uuid
    };
  }

  saveAddress() {
    this.addressForm.markAllAsTouched();
    if (this.addressForm.invalid || !this.customer || this.isSavingAddress) return;
    this.isSavingAddress = true;

    this.addressService.createAddress(this.buildAddressRequest()).subscribe({
      next: (res) => {
        this.isSavingAddress = false;
        const saved = res.result;
        if (!saved) return;

        this.addresses = [saved, ...this.addresses];
        this.form.get('shippingAddressId')?.setValue(saved.uuid);
        this.addressDialogVisible = false;

        if (saved.validationStatus !== 'VALID') {
          this.messageService.add({
            severity: 'info', summary: 'Address saved',
            detail: saved.validationNotes ?? 'It could not be fully checked, so a courier may not accept it yet.'
          });
        }
      },
      error: (err) => {
        this.isSavingAddress = false;
        this.messageService.add({
          severity: 'error', summary: 'Address not saved',
          detail: err?.error?.message ?? 'The address could not be saved.'
        });
      }
    });
  }

  // ── Lines ───────────────────────────────────────────────────────────────────

  /** @param withDefaultTaxCode A new line starts on the organization's default sales tax code, when it has one. */
  private newLine(withDefaultTaxCode = true): FormGroup {
    const code = withDefaultTaxCode ? this.defaultTaxCode : null;
    return this.fb.group({
      productUuid:     [null as string | null],
      variantUuid:     [null as string | null, Validators.required],
      label:           [''],
      sku:             [''],
      // An order line being edited shows what it is; a new one shows the picker. "Change" opens it.
      pickerOpen:      [true],
      quantity:        [null as number | null, [Validators.required, Validators.min(0.0001), Validators.max(999999)]],
      discountPercent: [0, [Validators.min(0), Validators.max(100)]],
      // The server holds a typed percentage to 0–100 (the column is decimal(5,2)); a code brings its own rate.
      taxPercent:      [code?.ratePercent ?? 0, [Validators.min(0), Validators.max(100)]],
      // SAP alignment (S-3) — the tax code; with one, taxPercent is its rate and is not typed.
      taxCodeUuid:     [code?.uuid ?? null as string | null],
      // A33 PC-07 — the line's route override; null inherits (variant, then organization default). Read-only for
      // someone who may not change the order, but still sent back so a save never drops it.
      fulfillmentRouteUuid: [{ value: null as string | null, disabled: !this.canChangeRoute }],
      unitPrice:       [null as number | null],
      priceState:      ['none' as PriceState],
      // UI-only: how a price in another currency was converted, or why it cannot be.
      priceNote:       [null as string | null],
      // A31-C1 — UI-only, carried from the picked variant, never submitted; drives the inline
      // min/max quantity check below (the server enforces the same rule regardless).
      saleOrderMinQty: [null as number | null],
      saleOrderMaxQty: [null as number | null],
      // A34 D-15 — the line's dates, yyyy-MM-dd (and a UTC timestamp), sent back on every save (C-14). The calculation
      // holds for the variant and quantity it was made for (leadTimeBasis); a changed line drops it.
      manualDeliveryDate:     [null as string | null],
      calculatedLeadTimeDays: [null as number | null],
      calculatedDeliveryDate: [null as string | null],
      leadTimeCalculatedAt:   [null as string | null],
      leadTimeBasis:          [null as string | null],
      // A37 — UI-only, never submitted: the loaded line's routeWarning (API-CONTRACT §4).
      routeWarning:           [null as string | null]
    });
  }

  addLine() {
    this.lines.push(this.newLine());
  }

  // ── Tax codes (SAP alignment S-3) ───────────────────────────────────────────

  /** "GST17 · 17%" */
  taxCodeLabel(code: string, ratePercent: number): string {
    return formatTaxCodeLabel(code, ratePercent);
  }

  /** The organization's default sales code, if it has one that is active. */
  get defaultTaxCode(): TaxCodeModel | null {
    return this.taxCodes.find(c => c.isDefault) ?? null;
  }

  /**
   * What a line's dropdown offers: the active codes, and any code a loaded line carries that is no longer
   * among them — so the line still says what it was taxed at.
   */
  get taxCodeOptions(): TaxCodeOption[] {
    return [...this.activeTaxCodeOptions, ...this.retiredTaxCodeOptions];
  }

  private get activeTaxCodeOptions(): TaxCodeOption[] {
    return this.taxCodes.map(c => ({
      value: c.uuid, ratePercent: c.ratePercent, code: c.code,
      label: this.taxCodeLabel(c.code, c.ratePercent) + (c.name && c.name !== c.code ? ` — ${c.name}` : '')
    }));
  }

  private get retiredTaxCodeOptions(): TaxCodeOption[] {
    return this.retiredTaxCodes
      .filter(r => !this.taxCodes.some(c => c.uuid === r.value))
      .map(r => ({ ...r, label: this.taxCodesLoaded ? `${r.label} (no longer active)` : r.label }));
  }

  /**
   * What line `i`'s dropdown offers: the active codes, and a code no longer offered only on the line that
   * already has it — so it still says what it was taxed at, but cannot be newly picked for another line
   * only for the server to refuse it.
   */
  taxCodeOptionsFor(i: number): TaxCodeOption[] {
    const own = this.lineControl(i, 'taxCodeUuid').value as string | null;
    return [...this.activeTaxCodeOptions, ...this.retiredTaxCodeOptions.filter(r => r.value === own)];
  }

  /** With no code to offer, the line takes a percentage, as it always did. */
  get showTaxCodes(): boolean {
    return this.taxCodeOptions.length > 0;
  }

  /** A line on a code shows the code's rate, and the rate cannot be typed over. */
  lineHasTaxCode(i: number): boolean {
    return !!this.lineControl(i, 'taxCodeUuid').value;
  }

  /**
   * The line is on a code the organization no longer offers for sales (deactivated, or now purchase-only).
   * The server refuses it on save, so the form says so and will not send it. Only known once the codes have
   * loaded: if they could not be, the code goes as it is and the server judges it.
   */
  lineHasRetiredTaxCode(i: number): boolean {
    const uuid = this.lineControl(i, 'taxCodeUuid').value as string | null;
    return !!uuid && this.taxCodesLoaded && !this.taxCodes.some(c => c.uuid === uuid);
  }

  /**
   * The server taxes a line on a code at the code's rate as it is when the order is saved, not at the rate the
   * line was saved with; a draft loaded after the code's rate changed shows (and previews) the rate it will get.
   */
  private applyCurrentTaxRates() {
    if (!this.taxCodesLoaded) return;
    for (const line of this.lines.controls) {
      const code = this.taxCodes.find(c => c.uuid === line.get('taxCodeUuid')!.value);
      const tax = line.get('taxPercent')!;
      if (code && tax.value !== code.ratePercent) tax.setValue(code.ratePercent);
    }
  }

  /** Picking a code sets the line's rate to the code's; clearing it leaves the rate there to be typed. */
  onTaxCodeChange(i: number) {
    const uuid = this.lineControl(i, 'taxCodeUuid').value as string | null;
    if (!uuid) return;
    const option = this.taxCodeOptionsFor(i).find(o => o.value === uuid);
    if (option) this.lineControl(i, 'taxPercent').setValue(option.ratePercent);
  }

  /**
   * The codes may arrive after the form's first line was made: a new order's lines that nobody has given a
   * tax yet start on the default. An order being edited keeps what its lines have.
   */
  private applyDefaultTaxCode() {
    const code = this.defaultTaxCode;
    if (!code || this.isEdit) return;
    for (const line of this.lines.controls) {
      const tax = line.get('taxPercent')!;
      if (!line.get('taxCodeUuid')!.value && tax.pristine && !tax.value) {
        line.patchValue({ taxCodeUuid: code.uuid, taxPercent: code.ratePercent });
      }
    }
  }

  removeLine(i: number) {
    this.priceRequests.delete(this.lines.at(i));
    this.lines.removeAt(i);
  }

  lineControl(i: number, name: string): AbstractControl {
    return this.lines.at(i).get(name)!;
  }

  lineIsInvalid(i: number, name: string): boolean {
    const c = this.lineControl(i, name);
    return c.invalid && (c.dirty || c.touched);
  }

  onLineVariantSelected(i: number, sel: VariantPickerSelection) {
    const line = this.lines.at(i);
    line.patchValue({
      productUuid: sel.productUuid,
      variantUuid: sel.variantUuid,
      label: sel.variantName && sel.variantName !== 'Default' ? `${sel.productName} — ${sel.variantName}` : (sel.productName ?? ''),
      sku: sel.variantSku ?? '',
      unitPrice: null,
      priceState: 'none',
      priceNote: null,
      saleOrderMinQty: sel.saleOrderMinQty,
      saleOrderMaxQty: sel.saleOrderMaxQty
    });
    line.get('variantUuid')?.markAsTouched();
    this.refreshPrice(i);
  }

  /** A31-C1/§3.7 — conditional: a side only applies when it is set AND > 0. Null when the line's quantity is within range (or has no limits configured). */
  lineQtyLimitError(i: number): string | null {
    const line = this.lines.at(i);
    const qty = line.get('quantity')?.value as number | null;
    const min = line.get('saleOrderMinQty')?.value as number | null;
    const max = line.get('saleOrderMaxQty')?.value as number | null;
    if (qty == null) return null;
    if (min != null && min > 0 && qty < min) return `Minimum order quantity for this item is ${min}.`;
    if (max != null && max > 0 && qty > max) return `Maximum order quantity for this item is ${max}.`;
    return null;
  }

  /** Gates the Save button — the server enforces this too, but there is no point round-tripping a request we already know it will refuse. */
  hasLineQtyLimitErrors(): boolean {
    return this.lines.controls.some((_, i) => this.lineQtyLimitError(i) !== null);
  }

  changeLineItem(i: number) {
    this.lineControl(i, 'pickerOpen').setValue(true);
  }

  /**
   * The date the server prices the order at: an existing order keeps its own order date; a new one is dated
   * today (UTC) when it is saved. Undefined for a new order, which the price preview then leaves to the server.
   */
  private get orderDate(): string | undefined {
    return this.isEdit && this.order?.orderDate ? this.order.orderDate.slice(0, 10) : undefined;
  }

  /**
   * Previews what the pricing rules would give this line for this customer and quantity, in the order's
   * currency, as the server will work it out on save. Best effort: saving decides.
   */
  refreshPrice(i: number) {
    const line = this.lines.at(i);
    if (!line) return;

    const variantUuid = line.get('variantUuid')?.value as string | null;
    const quantity = line.get('quantity')?.value as number | null;
    const customer = this.customer;

    if (!variantUuid || !quantity || quantity <= 0 || !customer) {
      this.priceRequests.delete(line);
      line.patchValue({ unitPrice: null, priceState: 'none', priceNote: null });
      return;
    }
    if (!this.authService.hasPermission('INVENTORY_VIEW')) {
      this.priceRequests.delete(line);
      line.patchValue({ unitPrice: null, priceState: 'unavailable', priceNote: null });
      return;
    }

    const seq = ++this.requestSeq;
    this.priceRequests.set(line, seq);
    line.patchValue({ priceState: 'loading', priceNote: null });

    // An existing order is priced at its own date; a new one at today's, which is the endpoint's default.
    const date = this.orderDate;
    const resolve = date
      ? this.pricingService.resolvePrice(variantUuid, customer.uuid!, quantity, date)
      : this.pricingService.resolvePrice(variantUuid, customer.uuid!, quantity);

    resolve.subscribe({
      next: (res) => {
        if (this.priceRequests.get(line) !== seq) return;
        const price = res.result;
        if (price?.found && price.unitPrice != null) this.priceInOrderCurrency(line, seq, price.unitPrice, price.currencyId ?? null);
        else line.patchValue({ unitPrice: null, priceState: 'missing', priceNote: null });
      },
      error: () => {
        if (this.priceRequests.get(line) !== seq) return;
        line.patchValue({ unitPrice: null, priceState: 'unavailable', priceNote: null });
      }
    });
  }

  /**
   * The server's SaleOrderService.ToOrderCurrencyAsync, step for step. A price rule is quoted in its own
   * currency; a list price has none and is in the organization's base currency (taken as it is when the
   * organization has no base currency). Same currency: as it is. Otherwise both need an ISO code, and a rate
   * on file on or before the order date (or the reciprocal of the opposite pair); the price is converted at
   * it, to the cent, half away from zero. No code or no rate: the server refuses the save.
   */
  private priceInOrderCurrency(line: AbstractControl, seq: number, price: number, priceCurrencyId: string | null) {
    const found = (unitPrice: number, priceNote: string | null = null) =>
      line.patchValue({ unitPrice, priceState: 'found', priceNote });
    const refused = (priceNote: string) => line.patchValue({ unitPrice: null, priceState: 'noRate', priceNote });
    const unknown = () => line.patchValue({ unitPrice: null, priceState: 'unavailable', priceNote: null });

    const orderCurrencyId = this.form.get('currencyId')?.value as string | null;
    let fromId = priceCurrencyId;
    if (!fromId) {
      const tenant = this.tenantService.tenant();
      if (!tenant) { unknown(); return; }            // the base currency is not known yet
      fromId = tenant.baseCurrency ?? null;
    }
    if (!fromId || !orderCurrencyId || fromId === orderCurrencyId) { found(price); return; }

    const from = this.currencies.find(c => c.id === fromId);
    const to = this.currencies.find(c => c.id === orderCurrencyId);
    if (!from || !to) { unknown(); return; }

    const fromCode = from.code?.trim().toUpperCase() || null;
    const toCode = to.code?.trim().toUpperCase() || null;
    if (!fromCode || !toCode) {
      refused(`The price is in ${fromCode ?? from.name}, but ${fromCode ? `${to.name} (the order's currency)` : 'that currency'} ` +
              'has no ISO code under Settings → Currencies, so it cannot be converted. Saving will be refused.');
      return;
    }
    if (fromCode === toCode) { found(price); return; }

    const date = this.orderDate ?? todayUtc();
    this.financeSetupService.quoteExchangeRate(fromCode, toCode, date).subscribe({
      next: (res) => {
        if (this.priceRequests.get(line) !== seq) return;
        const rate = res.result?.rate;
        if (rate == null || !(rate > 0)) {
          refused(`No ${fromCode} → ${toCode} exchange rate on or before ${formatDate(date, 'dd MMM yyyy', this.locale)}. ` +
                  'Saving will be refused until one is added under Settings → Exchange Rates.');
          return;
        }
        found(convertAtRate(price, rate),
          `From ${fromCode} ${formatNumber(price, this.locale, '1.2-4')} at ${formatNumber(rate, this.locale, '1.0-8')}`);
      },
      error: () => {
        if (this.priceRequests.get(line) !== seq) return;
        unknown();
      }
    });
  }

  /** qty × price less the discount plus the tax, to the cent — the server's own formula and rounding. Null until there is a price. */
  lineTotal(i: number): number | null {
    const v = this.lines.at(i).value;
    if (v.unitPrice == null || !v.quantity) return null;
    return saleOrderLineTotal(v.quantity, v.unitPrice, v.discountPercent ?? 0, v.taxPercent ?? 0);
  }

  /** What the server will total the priced lines to (its ApplyTotals), for lines that have a price. */
  get estimatedTotal(): number {
    const priced = this.lines.controls
      .map(c => c.value)
      .filter(v => v.unitPrice != null && !!v.quantity)
      .map(v => ({ quantity: v.quantity, unitPrice: v.unitPrice, discountPercent: v.discountPercent ?? 0, taxPercent: v.taxPercent ?? 0 }));
    return saleOrderGrandTotal(priced);
  }

  /** Lines that have no price to add up yet. */
  get unpricedLines(): number {
    return this.lines.controls.filter((_, i) => this.lineTotal(i) === null).length;
  }

  // ── Saving ──────────────────────────────────────────────────────────────────

  private buildLines(): SaleOrderLineRequest[] {
    return this.lines.controls.map((c, i) => {
      // Raw: a read-only route field is disabled, and its override must still go back.
      const v = (c as FormGroup).getRawValue();
      const calc = this.lineCalculation(i);
      return {
        variantUuid: v.variantUuid as string,
        quantity: v.quantity as number,
        discountPercent: v.discountPercent ?? 0,
        taxPercent: v.taxPercent ?? 0,
        // Sent on every save, an edit included: a draft's lines are rebuilt from what is sent.
        ...(v.taxCodeUuid ? { taxCodeUuid: v.taxCodeUuid as string } : {}),
        ...(v.fulfillmentRouteUuid ? { fulfillmentRouteUuid: v.fulfillmentRouteUuid as string } : {}),
        // A34 C-14 — the dates go back too, or the rebuilt line loses them.
        ...(v.manualDeliveryDate ? { manualDeliveryDate: v.manualDeliveryDate as string } : {}),
        ...(calc.calculatedDate ? {
          calculatedLeadTimeDays: calc.calculatedDays,
          calculatedDeliveryDate: calc.calculatedDate,
          leadTimeCalculatedAt: calc.calculatedAt
        } : {})
      };
    });
  }

  // ── A34 PC-07/08/09: lead time per line ─────────────────────────────────────

  /** ⏱ and the date are part of the order: creating it (new) or editing it (existing) — both are calculate codes. */
  get canUseLeadTime(): boolean {
    return this.authService.hasPermission(this.isEdit ? 'SALE_ORDER_EDIT' : 'SALE_ORDER_CREATE');
  }

  /** Line `i`'s stored calculation, while it still matches the line's variant and quantity. */
  lineCalculation(i: number): { calculatedDate: string | null; calculatedDays: number | null; calculatedAt: string | null } {
    const v = (this.lines.at(i) as FormGroup).getRawValue();
    const current = !!v.calculatedDeliveryDate && v.leadTimeBasis === leadTimeBasis(v.variantUuid, v.quantity);
    return current
      ? { calculatedDate: v.calculatedDeliveryDate, calculatedDays: v.calculatedLeadTimeDays, calculatedAt: v.leadTimeCalculatedAt }
      : { calculatedDate: null, calculatedDays: null, calculatedAt: null };
  }

  private readonly leadTimeCalculators = new WeakMap<AbstractControl, () => Observable<LeadTimeCalculation>>();

  /**
   * POST api/lead-time/calculate for the line as it stands (unsaved): its own route override, else the route the preview
   * resolved for it, against the header's expected date. One function per line, so the popover's input stays put.
   */
  leadTimeCalculator(i: number): () => Observable<LeadTimeCalculation> {
    const control = this.lines.at(i);
    let calc = this.leadTimeCalculators.get(control);
    if (!calc) {
      calc = () => {
        const index = this.lines.controls.indexOf(control);
        const v = (control as FormGroup).getRawValue();
        const expected = this.form.get('expectedDeliveryDate')?.value as Date | null;
        return this.leadTimeService.calculate({
          variantUuid: v.variantUuid as string,
          quantity: v.quantity as number,
          routeUuid: (v.fulfillmentRouteUuid as string | null) || this.previewLineFor(index)?.effectiveRouteUuid || null,
          requestedDate: expected ? toDateOnly(expected) : null
        }).pipe(map(res => ({ leadTime: res.result! })));
      };
      this.leadTimeCalculators.set(control, calc);
    }
    return calc;
  }

  /** The calculation, onto the line, for the variant and quantity it was made for. */
  onLineLeadTime(i: number, calc: LeadTimeCalculation) {
    const line = this.lines.at(i) as FormGroup;
    const v = line.getRawValue();
    line.patchValue({
      calculatedLeadTimeDays: calc.leadTime.totalLeadTimeDays,
      calculatedDeliveryDate: calc.leadTime.earliestDeliveryDate.slice(0, 10),
      leadTimeCalculatedAt: calc.leadTime.calculatedAt,
      leadTimeBasis: leadTimeBasis(v.variantUuid, v.quantity)
    });
  }

  /** Override date / Apply / × — the line's own date, or null for the calculated one. Saved with the order. */
  setLineDate(i: number, date: string | null) {
    this.lines.at(i).patchValue({ manualDeliveryDate: date });
  }

  lineLeadTimeLabel(i: number): string {
    const label = this.lineControl(i, 'label').value as string;
    return `Line ${i + 1}${label ? ': ' + label : ''}`;
  }

  /** What saving will send: a create for a new order, an update for one being edited. Exported so a spec can pin it. */
  buildRequest(): CreateSaleOrderRequest | UpdateSaleOrderRequest {
    const v = this.form.getRawValue();
    // A33 D-4 — also on a collected order when a line's route ships (REV-03).
    const withAddress = this.showAddress;
    const expected = v.expectedDeliveryDate ? toDateOnly(v.expectedDeliveryDate as Date) : undefined;
    const notes = (v.notes ?? '').trim() || undefined;

    if (this.isEdit) {
      return {
        expectedDeliveryDate: expected,
        currencyId: v.currencyId ?? undefined,
        deliveryMode: v.deliveryMode,
        // An update replaces this outright, so what the form does not show is sent back as it was.
        shippingAddressId: withAddress ? v.shippingAddressId ?? undefined : undefined,
        intimationDepartmentId: this.intimationDepartmentId,
        notes,
        // A32 — the draft's customer PO is replaced too: sent back as loaded unless changed, left out when cleared.
        ...this.customerPoFields(),
        lines: this.buildLines()
      };
    }

    return {
      partnerId: (v.customer as BusinessPartnerModel).uuid!,
      expectedDeliveryDate: expected,
      currencyId: v.currencyId ?? undefined,
      deliveryMode: v.deliveryMode,
      shippingAddressId: withAddress ? v.shippingAddressId ?? undefined : undefined,
      notes,
      // A32 — sourceType is left to the server's default, MANUAL: the only one this endpoint takes.
      ...this.customerPoFields(),
      lines: this.buildLines()
    };
  }

  /** The first thing wrong with the form, in words, for the toast. */
  firstProblem(): string | null {
    if (this.form.get('customer')?.invalid) return 'Choose the customer from the list.';
    if (this.isShip && this.form.get('shippingAddressId')?.invalid) return 'A shipped order needs a shipping address.';
    if (this.form.get('currencyId')?.invalid) return 'Choose the currency.';
    if (this.lines.length === 0) return 'Add at least one line.';
    const badTax = this.lines.controls.findIndex(c => c.get('taxPercent')?.invalid);
    if (badTax >= 0) return `Line ${badTax + 1}: the tax % must be between 0 and 100.`;
    const retired = this.lines.controls.findIndex((_, i) => this.lineHasRetiredTaxCode(i));
    if (retired >= 0) {
      const uuid = this.lineControl(retired, 'taxCodeUuid').value as string;
      const code = this.retiredTaxCodes.find(r => r.value === uuid)?.code ?? 'on this line';
      return `Line ${retired + 1}: tax code ${code} is no longer active. Choose another code, or clear it and type the rate.`;
    }
    const bad = this.lines.controls.findIndex(c => c.invalid);
    if (bad >= 0) return `Line ${bad + 1} is incomplete: choose an item and a quantity above zero.`;
    if (this.form.get('customerPoReference')?.invalid) return this.customerPoTooLong;
    if (this.form.invalid) return 'Some details are not valid.';
    return null;
  }

  save() {
    this.form.markAllAsTouched();
    const problem = this.isFromQuotation ? this.firstQuotationProblem() : this.firstProblem();
    if (problem) {
      this.messageService.add({ severity: 'warn', summary: 'Check the order', detail: problem });
      return;
    }
    if (this.isSaving) return;
    this.isSaving = true;

    if (this.isFromQuotation) { this.convert(); return; }

    const request = this.buildRequest();

    if (this.isEdit) {
      this.saleOrderService.updateSaleOrder(this.uuid!, request as UpdateSaleOrderRequest).subscribe({
        next: () => {
          this.isSaving = false;
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'The draft has been updated.' });
          this.router.navigate(['/portal/pages/sales/orders', this.uuid]);
        },
        error: (err) => this.failed(err)
      });
      return;
    }

    this.saleOrderService.createSaleOrder(request as CreateSaleOrderRequest).subscribe({
      next: (res) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'success', summary: 'Draft saved', detail: 'Review it, then confirm it.' });
        if (res.result) this.router.navigate(['/portal/pages/sales/orders', res.result]);
        else this.router.navigate(['/portal/pages/sales/orders']);
      },
      error: (err) => this.failed(err)
    });
  }

  private failed(err: any) {
    this.isSaving = false;
    this.messageService.add({
      severity: 'error', summary: 'Not saved',
      // The server says which line has no price and what is wrong, so show that.
      detail: err?.error?.message ?? 'The order could not be saved.'
    });
  }

  get backLink(): string[] {
    return this.isEdit ? ['/portal/pages/sales/orders', this.uuid!] : ['/portal/pages/sales/orders'];
  }

  // ── A33 PC-07 / PC-08: route per line, delivery preview ─────────────────────

  /** The organization has the Logistics module (D-11); without it there are no routes, no gate and no preview. */
  private get logisticsOn(): boolean {
    return !!this.tenantService.tenant()?.enabledFeatureCodes?.includes('MODULE_LOGISTICS');
  }

  /** A34 D-9 — the organization has the Manufacturing module (make-to-order routes are offered). */
  private get manufacturingOn(): boolean {
    return !!this.tenantService.tenant()?.enabledFeatureCodes?.includes('MODULE_MANUFACTURING');
  }

  /** Routes apply: the organization has Logistics and the server has not said otherwise. */
  get routesEnabled(): boolean {
    return this.logisticsOn && !this.serverRoutesOff;
  }

  /** The override is part of the order: picking it on a new order is creating it, on a draft editing it (contract §2). */
  get canChangeRoute(): boolean {
    return this.authService.hasPermission(this.isEdit ? 'SALE_ORDER_EDIT' : 'SALE_ORDER_CREATE');
  }

  /** Reading the routes accepts any of these (contract §2). */
  private get canReadRoutes(): boolean {
    return ['FULFILLMENT_ROUTE_VIEW', 'FULFILLMENT_ROUTE_MANAGE', 'FULFILLMENT_ROUTE_ASSIGN', 'SALE_ORDER_VIEW', 'INVENTORY_VIEW', 'DELIVERY_VIEW']
      .some(code => this.authService.hasPermission(code));
  }

  private ensureRoutesLoaded() {
    if (this.routesRequested || !this.routesEnabled || !this.canReadRoutes) return;
    this.routesRequested = true;
    this.routesService.getRoutes().subscribe({
      // A34 D-9 — MANUFACTURE routes only for an organization with manufacturing.
      next: (res) => {
        this.activeRoutes = routesVisibleToOrg(res.result ?? [], this.manufacturingOn);
        this.unavailableRoutes = (res.result ?? []).filter(r => r.isAvailable === false);
      },
      // Without the list the line can still inherit, and keeps the override it has.
      error: () => { this.activeRoutes = []; this.unavailableRoutes = []; }
    });
  }

  /** The active routes, and the route a loaded line already carries when it is no longer among them. */
  routeOptionsFor(i: number): { label: string; value: string; disabled?: boolean }[] {
    const options: { label: string; value: string; disabled?: boolean }[] = this.activeRoutes.map(r => ({ label: r.name, value: r.uuid }));
    const own = this.lineControl(i, 'fulfillmentRouteUuid').value as string | null;
    if (own && !options.some(o => o.value === own)) {
      const unavailable = this.unavailableRoutes.find(r => r.uuid === own);
      const label = unavailable ? `${unavailable.name} — ${unavailable.unavailableReason || 'not available'}` : null;
      options.push({ label: label ?? this.retiredRouteLabels.get(own) ?? 'Route no longer offered', value: own });
    }
    // A37 — the unavailable ones are shown, disabled, so it is clear why they cannot be picked.
    options.push(...unavailableRouteOptions(this.unavailableRoutes, options.map(o => o.value)));
    return options;
  }

  /**
   * A37 RTE-03 — the server's warning when the line's route is unavailable (fallback or nothing applies): the live
   * delivery preview's answer, else the one the saved line came with.
   */
  lineRouteWarning(i: number): string | null {
    const preview = this.previewLineFor(i);
    if (preview) return preview.routeWarning ?? null;
    return (this.lineControl(i, 'routeWarning')?.value as string | null) ?? null;
  }

  /** The preview's answer for line `i`, if the line was in the last request. */
  private previewLineFor(i: number): DeliveryPreviewLineModel | null {
    const control = this.lines.at(i);
    const position = this.previewControls.indexOf(control);
    if (!this.preview || position < 0) return null;
    return this.preview.lines.find(l => l.lineNumber === position + 1) ?? null;
  }

  /** What the line would inherit, for the dropdown's empty choice. */
  inheritedRouteLabel(i: number): string {
    return inheritPlaceholder(this.previewLineFor(i), !!this.lineControl(i, 'fulfillmentRouteUuid').value);
  }

  /**
   * Where line `i`'s route comes from. An override shows at once; otherwise what the preview said, once it has
   * caught up with the line (a cleared override reads as unknown until it does).
   */
  lineRouteSource(i: number): FulfillmentRouteSource | null {
    if (!this.routesEnabled) return null;
    if (this.lineControl(i, 'fulfillmentRouteUuid').value) return 'LINE_OVERRIDE';
    const p = this.previewLineFor(i);
    if (!p || p.routeSource === 'LINE_OVERRIDE') return null;
    return p.routeSource;
  }

  lineRouteDisplayFor(i: number): LineRouteDisplay | null {
    const source = this.lineRouteSource(i);
    if (!source) return null;
    const p = this.previewLineFor(i);
    if (source !== 'LINE_OVERRIDE') return lineRouteDisplay(p ?? { routeSource: source });

    const own = this.lineControl(i, 'fulfillmentRouteUuid').value as string;
    const answered = p?.routeSource === 'LINE_OVERRIDE' && (p.fulfillmentRouteUuid ?? p.effectiveRouteUuid) === own;
    return lineRouteDisplay({
      routeSource: 'LINE_OVERRIDE',
      routeBlocker: answered ? p!.routeBlocker : null,
      effectiveRouteName: this.routeOptionsFor(i).find(o => o.value === own)?.label ?? (answered ? p!.effectiveRouteName : null),
      effectiveRouteCode: own
    });
  }

  readonly routeLegend = ROUTE_LEGEND;

  /** The preview request for the form as it stands; null when routes are off or no line is complete yet. */
  private buildPreviewRequest(): { request: SaleOrderDeliveryPreviewRequest; controls: AbstractControl[] } | null {
    if (!this.routesEnabled) return null;
    const controls = this.lines.controls.filter(c => {
      const v = (c as FormGroup).getRawValue();
      return !!v.variantUuid && v.quantity != null && v.quantity > 0;
    });
    if (controls.length === 0) return null;

    const v = this.form.getRawValue();
    return {
      controls,
      request: {
        saleOrderUuid: this.uuid ?? null,
        deliveryMode: v.deliveryMode,
        // D-4 — a collected order with a line that ships sends its address too, so that blocker can clear.
        shippingAddressId: this.showAddress ? (v.shippingAddressId as string | null) ?? null : null,
        lines: controls.map(c => {
          const l = (c as FormGroup).getRawValue();
          return { variantUuid: l.variantUuid as string, quantity: l.quantity as number, fulfillmentRouteUuid: (l.fulfillmentRouteUuid as string | null) ?? null };
        })
      }
    };
  }

  /** Only what changes the preview starts a new one; a price arriving or a discount typed does not. */
  private previewKey(): string {
    const built = this.buildPreviewRequest();
    return built ? JSON.stringify(built.request) : '';
  }

  private refreshPreview() {
    const built = this.buildPreviewRequest();
    const seq = ++this.previewSeq;
    if (!built) {
      this.preview = null;
      this.previewControls = [];
      this.isLoadingPreview = false;
      this.previewFailed = false;
      return;
    }
    this.isLoadingPreview = true;
    this.saleOrderService.previewDeliveries(built.request).subscribe({
      next: (res) => {
        if (seq !== this.previewSeq) return;            // a newer request is on its way
        this.isLoadingPreview = false;
        this.previewFailed = false;
        const result = res.result ?? null;
        if (result && result.routesEnabled === false) {
          this.serverRoutesOff = true;
          this.preview = null;
          this.previewControls = [];
          return;
        }
        this.preview = result;
        this.previewControls = built.controls;
        // Whether a line ships may have changed whether the address belongs in the request: ask again if so
        // (the key is unchanged otherwise, so this never loops).
        this.previewTrigger$.next();
      },
      error: () => {
        if (seq !== this.previewSeq) return;
        this.isLoadingPreview = false;
        this.previewFailed = true;
      }
    });
  }

  /** The preview names lines by the form's own numbers and labels. */
  readonly previewLineRef = (lineNumber: number): PreviewLineRef | null => {
    const control = this.previewControls[lineNumber - 1];
    const index = control ? this.lines.controls.indexOf(control) : -1;
    if (index < 0) return null;
    const v = (control as FormGroup).getRawValue();
    return { number: index + 1, description: (v.label as string) || (v.sku as string) || `Line ${index + 1}` };
  };
}

/** How long the form waits after the last change before asking for a new preview. */
export const PREVIEW_DEBOUNCE_MS = 400;

/** A34 — what a line's lead-time calculation was made for: a changed variant or quantity voids it. */
function leadTimeBasis(variantUuid: unknown, quantity: unknown): string | null {
  return variantUuid && quantity != null ? `${variantUuid}|${quantity}` : null;
}
