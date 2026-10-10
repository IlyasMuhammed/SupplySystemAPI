import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormBuilder, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';

import {
  CUSTOMER_TYPE_OPTIONS, CustomerDetail, CustomerService, CustomerType, CustomerUpsert
} from '../../../services/customer.service';
import { CurrenciesService, CurrencyModel } from '../../../services/currencies.service';

/**
 * A37 §15.3/§15.4 — create / edit a customer (API-CONTRACT §5). One dialog for the list's "New customer" and the detail
 * page's "Edit". The server's 400 message is shown as it comes. The walk-in customer keeps its type (CUST-01) and has no
 * credit limit (CUST-04); the credit limit is offered only with FEATURE_CREDIT_MANAGEMENT (the server ignores it otherwise).
 */
@Component({
  selector: 'app-customer-form-dialog',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, ButtonModule, DialogModule, DropdownModule, InputNumberModule, InputTextModule, TextareaModule],
  template: `
<p-dialog [visible]="visible" (visibleChange)="close($event)" [modal]="true" [draggable]="false" [style]="{ width: '46rem' }"
          [breakpoints]="{ '760px': '96vw' }" [header]="customer ? 'Edit customer' : 'New customer'" data-testid="customer-dialog">
  <form [formGroup]="form" *ngIf="visible" (ngSubmit)="save()">
    <div class="sf-pill er cf-error" *ngIf="error" role="alert" data-testid="customer-form-error">{{ error }}</div>

    <div class="sf-fields">
      <div class="sf-fld cf-wide">
        <label for="custName">Name <span class="req">*</span></label>
        <input pInputText id="custName" formControlName="name" maxlength="200" data-testid="customer-name" />
      </div>
      <div class="sf-fld">
        <label for="custType">Type</label>
        <p-dropdown inputId="custType" [options]="typeOptions" formControlName="customerType" optionLabel="label" optionValue="value"
                    optionDisabled="disabled" appendTo="body" styleClass="w-full" data-testid="customer-type"></p-dropdown>
        <small class="sf-mu" *ngIf="isWalkIn" data-testid="walk-in-type-note">The walk-in customer always stays a walk-in.</small>
      </div>
      <div class="sf-fld">
        <label for="custTax">Tax ID</label>
        <input pInputText id="custTax" formControlName="taxId" maxlength="50" />
      </div>
      <div class="sf-fld">
        <label for="custPhone">Phone</label>
        <input pInputText id="custPhone" formControlName="phone" maxlength="50" />
      </div>
      <div class="sf-fld">
        <label for="custMobile">Mobile</label>
        <input pInputText id="custMobile" formControlName="mobile" maxlength="50" />
      </div>
      <div class="sf-fld cf-wide">
        <label for="custEmail">Email</label>
        <input pInputText id="custEmail" type="email" formControlName="email" maxlength="200" />
      </div>

      <div class="sf-fld" *ngIf="creditEnabled" data-testid="credit-limit-field">
        <label for="custCredit">Credit limit</label>
        <p-inputnumber inputId="custCredit" formControlName="creditLimit" mode="decimal" [minFractionDigits]="2" [maxFractionDigits]="2"
                       [min]="0" styleClass="w-full"></p-inputnumber>
        <small class="sf-mu" *ngIf="isWalkIn">Walk-in customers cannot have a credit limit.</small>
      </div>
      <div class="sf-fld">
        <label for="custTerms">Payment terms (days)</label>
        <p-inputnumber inputId="custTerms" formControlName="paymentTermsDays" [min]="0" [max]="365" [useGrouping]="false" styleClass="w-full"></p-inputnumber>
      </div>
      <div class="sf-fld">
        <label for="custCurrency">Default sale currency</label>
        <p-dropdown inputId="custCurrency" [options]="currencies" formControlName="defaultSaleCurrencyId" optionLabel="code" optionValue="id"
                    [showClear]="true" placeholder="Organization base currency" appendTo="body" styleClass="w-full"></p-dropdown>
      </div>

      <div class="sf-fld cf-wide">
        <label for="custAddr1">Address</label>
        <input pInputText id="custAddr1" formControlName="addressLine1" placeholder="Line 1" maxlength="200" />
        <input pInputText formControlName="addressLine2" placeholder="Line 2" maxlength="200" class="cf-mt" />
      </div>
      <div class="sf-fld">
        <label for="custCity">City</label>
        <input pInputText id="custCity" formControlName="city" maxlength="100" />
      </div>
      <div class="sf-fld">
        <label for="custProvince">Province / state</label>
        <input pInputText id="custProvince" formControlName="provinceState" maxlength="100" />
      </div>
      <div class="sf-fld">
        <label for="custPostal">Postal code</label>
        <input pInputText id="custPostal" formControlName="postalCode" maxlength="20" />
      </div>
      <div class="sf-fld">
        <label for="custCountry">Country</label>
        <input pInputText id="custCountry" formControlName="country" maxlength="100" />
      </div>
      <div class="sf-fld cf-wide">
        <label for="custNotes">Notes</label>
        <textarea pTextarea id="custNotes" formControlName="notes" rows="2"></textarea>
      </div>
    </div>
  </form>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="close(false)"></p-button>
    <p-button [label]="customer ? 'Save' : 'Create customer'" icon="pi pi-check" [loading]="saving" (onClick)="save()"
              data-testid="customer-save"></p-button>
  </ng-template>
</p-dialog>
  `,
  styles: [`
    .cf-error { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .cf-wide { grid-column: 1 / -1; }
    .cf-mt { margin-top: .4rem; }
    .req { color: var(--sms-danger); }
    :host ::ng-deep .w-full { width: 100%; }
  `]
})
export class CustomerFormDialogComponent implements OnChanges {
  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();
  /** null = new customer. */
  @Input() customer: CustomerDetail | null = null;
  /** FEATURE_CREDIT_MANAGEMENT is enabled. */
  @Input() creditEnabled = false;
  /** The customer's uuid after a successful save. */
  @Output() saved = new EventEmitter<string>();

  form: FormGroup;
  saving = false;
  error: string | null = null;
  currencies: CurrencyModel[] = [];
  private currenciesLoaded = false;

  constructor(private fb: FormBuilder, private service: CustomerService, private currenciesService: CurrenciesService) {
    this.form = this.fb.group({
      name: [''], customerType: ['COMPANY' as CustomerType], phone: [''], mobile: [''], email: [''], taxId: [''],
      creditLimit: [0], paymentTermsDays: [0], defaultSaleCurrencyId: [null as string | null],
      addressLine1: [''], addressLine2: [''], city: [''], provinceState: [''], postalCode: [''], country: [''], notes: ['']
    });
  }

  /** The walk-in customer (CUST-01): type locked, no credit limit. */
  get isWalkIn(): boolean { return !!this.customer && (this.customer.isSystem || this.customer.customerType === 'WALK_IN'); }

  /** WALK_IN is a system row: it cannot be picked for another customer, and the walk-in cannot leave it. */
  get typeOptions(): { label: string; value: CustomerType; disabled: boolean }[] {
    return CUSTOMER_TYPE_OPTIONS.map(o => ({ ...o, disabled: this.isWalkIn ? o.value !== 'WALK_IN' : o.value === 'WALK_IN' }));
  }

  ngOnChanges(changes: SimpleChanges): void {
    if ((changes['visible'] || changes['customer']) && this.visible) this.reset();
  }

  private reset(): void {
    this.error = null;
    this.saving = false;
    const c = this.customer;
    this.form.reset({
      name: c?.name ?? '', customerType: c?.customerType ?? 'COMPANY', phone: c?.phone ?? '', mobile: c?.mobile ?? '',
      email: c?.email ?? '', taxId: c?.taxId ?? '', creditLimit: c?.creditLimit ?? 0, paymentTermsDays: c?.paymentTermsDays ?? 0,
      defaultSaleCurrencyId: c?.defaultSaleCurrencyId ?? null, addressLine1: c?.addressLine1 ?? '', addressLine2: c?.addressLine2 ?? '',
      city: c?.city ?? '', provinceState: c?.provinceState ?? '', postalCode: c?.postalCode ?? '', country: c?.country ?? '',
      notes: c?.notes ?? ''
    });
    if (this.isWalkIn) { this.form.get('customerType')!.disable(); this.form.get('creditLimit')!.disable(); }
    else { this.form.get('customerType')!.enable(); this.form.get('creditLimit')!.enable(); }
    this.loadCurrencies();
  }

  private loadCurrencies(): void {
    if (this.currenciesLoaded) return;
    this.currenciesLoaded = true;
    this.currenciesService.getAll().subscribe({
      next: res => { this.currencies = res.result ?? []; },
      error: () => { this.currencies = []; }
    });
  }

  close(visible: boolean): void {
    if (visible) return;
    this.visible = false;
    this.visibleChange.emit(false);
  }

  /**
   * The request body — the full form every time (PUT replaces every field): trimmed text, empty → null. The credit limit
   * is typed only with credit management; otherwise a new customer leaves it out and an edit sends the stored one.
   */
  payload(): CustomerUpsert {
    const v = this.form.getRawValue();
    const text = (s: unknown) => (typeof s === 'string' && s.trim() ? s.trim() : null);
    const body: CustomerUpsert = {
      name: (v.name ?? '').trim(),
      customerType: v.customerType,
      phone: text(v.phone), mobile: text(v.mobile), email: text(v.email), taxId: text(v.taxId),
      paymentTermsDays: v.paymentTermsDays ?? 0,
      defaultSaleCurrencyId: v.defaultSaleCurrencyId ?? null,
      addressLine1: text(v.addressLine1), addressLine2: text(v.addressLine2), city: text(v.city),
      provinceState: text(v.provinceState), postalCode: text(v.postalCode), country: text(v.country), notes: text(v.notes)
    };
    // PUT replaces every field, so an edit always carries the limit — unchanged when credit management is off.
    if (this.isWalkIn) body.creditLimit = 0;
    else if (this.creditEnabled) body.creditLimit = v.creditLimit ?? 0;
    else if (this.customer) body.creditLimit = this.customer.creditLimit ?? 0;
    if (this.customer?.rowVersion) body.rowVersion = this.customer.rowVersion;
    return body;
  }

  save(): void {
    if (this.saving) return;
    const body = this.payload();
    // The same words as the server (contract §5), before a round trip.
    if (!body.name) { this.error = 'Name is required.'; return; }
    this.error = null;
    this.saving = true;
    const call = this.customer ? this.service.updateCustomer(this.customer.uuid, body) : this.service.createCustomer(body);
    call.subscribe({
      next: res => {
        this.saving = false;
        if (res && res.success === false) { this.error = res.message || 'The customer could not be saved.'; return; }
        const uuid = this.customer?.uuid ?? (res?.result as string | undefined) ?? '';
        this.saved.emit(uuid);
        this.close(false);
      },
      error: err => {
        this.saving = false;
        this.error = err?.status === 409
          ? 'Someone else changed this customer. Close the dialog and open it again to see their changes.'
          : err?.error?.message || 'The customer could not be saved.';
      }
    });
  }
}
