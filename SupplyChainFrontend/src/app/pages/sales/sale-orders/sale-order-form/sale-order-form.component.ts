import { Component, OnInit, effect } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import {
  AbstractControl, FormArray, FormBuilder, FormGroup, ReactiveFormsModule, ValidationErrors, Validators
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
  SaleOrderDefaultsModel
} from '../../../../services/sale-order.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { InventoryService, ProductListItemModel } from '../../../../services/inventory.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { AddressService } from '../../../../services/address.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { AddressModel, AddressRequest } from '../../../../services/logistics.service';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';

/** Where a line's indicative unit price stands. The server prices the order when it is saved; this only previews it. */
export type PriceState = 'none' | 'loading' | 'found' | 'missing' | 'unavailable';

/** The customer must be one picked from the list, not text typed into the box. */
function customerPicked(control: AbstractControl): ValidationErrors | null {
  const value = control.value as BusinessPartnerModel | string | null;
  return value && typeof value === 'object' && !!value.uuid ? null : { customerRequired: true };
}

function atLeastOneLine(control: AbstractControl): ValidationErrors | null {
  return (control as FormArray).length > 0 ? null : { noLines: true };
}

@Component({
  selector: 'app-sale-order-form',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule,
    ButtonModule, ToastModule, TooltipModule, DialogModule, DropdownModule, CalendarModule,
    InputNumberModule, InputTextModule, TextareaModule, AutoCompleteModule, SelectButtonModule,
    ProductVariantPickerComponent
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

  /** Kept from the loaded order so an edit does not reset what the form does not show. */
  private intimationDepartmentId?: number;

  /** The latest price request per line, so a slow answer for an old quantity cannot overwrite a newer one. */
  private priceRequests = new Map<AbstractControl, number>();
  private requestSeq = 0;

  constructor(
    private fb: FormBuilder,
    private route: ActivatedRoute,
    private router: Router,
    private saleOrderService: SaleOrderService,
    private partnerService: BusinessPartnerService,
    private inventoryService: InventoryService,
    private pricingService: PricingRuleService,
    private addressService: AddressService,
    private currenciesService: CurrenciesService,
    private tenantService: TenantService,
    public authService: AuthService,
    private messageService: MessageService
  ) {
    this.form = this.fb.group({
      customer:             [null as BusinessPartnerModel | string | null, [customerPicked]],
      deliveryMode:         ['SHIP', Validators.required],
      shippingAddressId:    [null as string | null, Validators.required],
      expectedDeliveryDate: [null as Date | null],
      currencyId:           [null as string | null, Validators.required],
      notes:                ['', Validators.maxLength(2000)],
      lines:                this.fb.array([], [atLeastOneLine])
    });

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
    });
  }

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid');

    this.isLoadingCurrencies = true;
    this.currenciesService.getAll().subscribe({
      next: (res) => {
        this.isLoadingCurrencies = false;
        this.currencyOptions = (res.result ?? []).map(c => ({ label: c.code ? `${c.name} (${c.code})` : c.name, value: c.id }));
        this.applyDefaultCurrency();
      },
      error: () => {
        this.isLoadingCurrencies = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'The currency list could not be loaded.' });
      }
    });

    this.inventoryService.getProducts({ activeOnly: true, pageSize: 500, availableFor: 'RETAIL' }).subscribe({
      next: (res) => { this.products = res.result?.data ?? []; },
      error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'The product list could not be loaded.' })
    });

    // Without the defaults the form still works: orders start as shipped, and the server has the last word.
    this.saleOrderService.getDefaults().subscribe({
      next: (res) => this.applyDefaults(res.result),
      error: () => { /* nothing to tell the user: the answer only sets where the form starts */ }
    });

    if (this.uuid) this.loadOrder(this.uuid);
    else this.addLine();
  }

  get isEdit(): boolean { return !!this.uuid; }

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
      expectedDeliveryDate: order.expectedDeliveryDate ? this.parseDate(order.expectedDeliveryDate) : null,
      notes: order.notes ?? ''
    });
    this.onModeChange();

    this.lines.clear();
    for (const l of order.lines.filter(l => l.status !== 'CANCELLED')) {
      const group = this.newLine();
      group.patchValue({
        variantUuid: l.variantUuid,
        label: l.itemDescription ?? l.variantSku ?? `Variant ${l.variantUuid}`,
        sku: l.variantSku ?? '',
        pickerOpen: false,
        quantity: l.quantity,
        discountPercent: l.discountPercent,
        taxPercent: l.taxPercent,
        unitPrice: l.unitPrice,
        priceState: 'found'
      });
      this.lines.push(group);
    }

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

  /** "2026-09-30T00:00:00Z" is the 30th, wherever the reader is. */
  private parseDate(iso: string): Date {
    const [y, m, d] = iso.slice(0, 10).split('-').map(Number);
    return new Date(y, m - 1, d);
  }

  private formatDate(date: Date): string {
    const p = (n: number) => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${p(date.getMonth() + 1)}-${p(date.getDate())}`;
  }

  // ── Customer ────────────────────────────────────────────────────────────────

  searchCustomers(event: AutoCompleteCompleteEvent) {
    this.partnerService.getPartners({ isCustomer: true, active: true, search: event.query, pageSize: 20 }).subscribe({
      next: (res) => { this.customerSuggestions = res.result?.data ?? []; },
      error: () => { this.customerSuggestions = []; }
    });
  }

  onCustomerSelected(partner: BusinessPartnerModel) {
    // A different customer has different addresses and, possibly, different prices.
    this.form.get('shippingAddressId')?.setValue(null);
    this.addresses = [];
    if (partner?.uuid) this.loadAddresses(partner.uuid);
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

  /** A collected order has no address; a shipped one cannot do without. */
  onModeChange() {
    const address = this.form.get('shippingAddressId')!;
    if (this.isShip) {
      address.addValidators(Validators.required);
    } else {
      address.removeValidators(Validators.required);
      address.setValue(null);
    }
    address.updateValueAndValidity();
  }

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

  private newLine(): FormGroup {
    return this.fb.group({
      productUuid:     [null as string | null],
      variantUuid:     [null as string | null, Validators.required],
      label:           [''],
      sku:             [''],
      // An order line being edited shows what it is; a new one shows the picker. "Change" opens it.
      pickerOpen:      [true],
      quantity:        [null as number | null, [Validators.required, Validators.min(0.0001), Validators.max(999999)]],
      discountPercent: [0, [Validators.min(0), Validators.max(100)]],
      taxPercent:      [0, [Validators.min(0), Validators.max(1000)]],
      unitPrice:       [null as number | null],
      priceState:      ['none' as PriceState]
    });
  }

  addLine() {
    this.lines.push(this.newLine());
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
      priceState: 'none'
    });
    line.get('variantUuid')?.markAsTouched();
    this.refreshPrice(i);
  }

  changeLineItem(i: number) {
    this.lineControl(i, 'pickerOpen').setValue(true);
  }

  /** Previews what the pricing rules would give this line for this customer and quantity. Best effort: saving decides. */
  refreshPrice(i: number) {
    const line = this.lines.at(i);
    if (!line) return;

    const variantUuid = line.get('variantUuid')?.value as string | null;
    const quantity = line.get('quantity')?.value as number | null;
    const customer = this.customer;

    if (!variantUuid || !quantity || quantity <= 0 || !customer) {
      line.patchValue({ unitPrice: null, priceState: 'none' });
      return;
    }
    if (!this.authService.hasPermission('INVENTORY_VIEW')) {
      line.patchValue({ unitPrice: null, priceState: 'unavailable' });
      return;
    }

    const seq = ++this.requestSeq;
    this.priceRequests.set(line, seq);
    line.patchValue({ priceState: 'loading' });

    this.pricingService.resolvePrice(variantUuid, customer.uuid!, quantity).subscribe({
      next: (res) => {
        if (this.priceRequests.get(line) !== seq) return;
        const price = res.result;
        if (price?.found && price.unitPrice != null) line.patchValue({ unitPrice: price.unitPrice, priceState: 'found' });
        else line.patchValue({ unitPrice: null, priceState: 'missing' });
      },
      error: () => {
        if (this.priceRequests.get(line) !== seq) return;
        line.patchValue({ unitPrice: null, priceState: 'unavailable' });
      }
    });
  }

  /** qty × price less the discount plus the tax, to the cent — the server's own formula. Null until there is a price. */
  lineTotal(i: number): number | null {
    const v = this.lines.at(i).value;
    if (v.unitPrice == null || !v.quantity) return null;
    const total = v.quantity * v.unitPrice * (1 - (v.discountPercent ?? 0) / 100) * (1 + (v.taxPercent ?? 0) / 100);
    return Math.round((total + Number.EPSILON) * 100) / 100;
  }

  get estimatedTotal(): number {
    return this.lines.controls.reduce((sum, _, i) => sum + (this.lineTotal(i) ?? 0), 0);
  }

  /** Lines that have no price to add up yet. */
  get unpricedLines(): number {
    return this.lines.controls.filter((_, i) => this.lineTotal(i) === null).length;
  }

  // ── Saving ──────────────────────────────────────────────────────────────────

  private buildLines(): SaleOrderLineRequest[] {
    return this.lines.controls.map(c => {
      const v = c.value;
      return {
        variantUuid: v.variantUuid as string,
        quantity: v.quantity as number,
        discountPercent: v.discountPercent ?? 0,
        taxPercent: v.taxPercent ?? 0
      };
    });
  }

  /** What saving will send: a create for a new order, an update for one being edited. Exported so a spec can pin it. */
  buildRequest(): CreateSaleOrderRequest | UpdateSaleOrderRequest {
    const v = this.form.getRawValue();
    const ship = v.deliveryMode === 'SHIP';
    const expected = v.expectedDeliveryDate ? this.formatDate(v.expectedDeliveryDate as Date) : undefined;
    const notes = (v.notes ?? '').trim() || undefined;

    if (this.isEdit) {
      return {
        expectedDeliveryDate: expected,
        currencyId: v.currencyId ?? undefined,
        deliveryMode: v.deliveryMode,
        // An update replaces this outright, so what the form does not show is sent back as it was.
        shippingAddressId: ship ? v.shippingAddressId ?? undefined : undefined,
        intimationDepartmentId: this.intimationDepartmentId,
        notes,
        lines: this.buildLines()
      };
    }

    return {
      partnerId: (v.customer as BusinessPartnerModel).uuid!,
      expectedDeliveryDate: expected,
      currencyId: v.currencyId ?? undefined,
      deliveryMode: v.deliveryMode,
      shippingAddressId: ship ? v.shippingAddressId ?? undefined : undefined,
      notes,
      lines: this.buildLines()
    };
  }

  /** The first thing wrong with the form, in words, for the toast. */
  firstProblem(): string | null {
    if (this.form.get('customer')?.invalid) return 'Choose the customer from the list.';
    if (this.isShip && this.form.get('shippingAddressId')?.invalid) return 'A shipped order needs a shipping address.';
    if (this.form.get('currencyId')?.invalid) return 'Choose the currency.';
    if (this.lines.length === 0) return 'Add at least one line.';
    const bad = this.lines.controls.findIndex(c => c.invalid);
    if (bad >= 0) return `Line ${bad + 1} is incomplete: choose an item and a quantity above zero.`;
    if (this.form.invalid) return 'Some details are not valid.';
    return null;
  }

  save() {
    this.form.markAllAsTouched();
    const problem = this.firstProblem();
    if (problem) {
      this.messageService.add({ severity: 'warn', summary: 'Check the order', detail: problem });
      return;
    }
    if (this.isSaving) return;
    this.isSaving = true;

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
}
