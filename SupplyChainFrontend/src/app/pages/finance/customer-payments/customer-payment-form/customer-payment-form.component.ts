import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import {
  AbstractControl, FormBuilder, FormGroup, FormsModule, ReactiveFormsModule, ValidationErrors, Validators
} from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { AutoCompleteModule, AutoCompleteCompleteEvent } from 'primeng/autocomplete';
import { SelectButtonModule } from 'primeng/selectbutton';
import { MessageService } from 'primeng/api';

import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { SalesInvoiceService, SalesInvoiceListItemModel } from '../../../../services/sales-invoice.service';
import {
  CustomerPaymentService, CUSTOMER_PAYMENT_METHODS, RecordCustomerPaymentRequest
} from '../../../../services/customer-payment.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { AuthService } from '../../../service/auth.service';
import { toDateOnly } from '../../../../shared/date-only';
import { PaymentAllocationEditorComponent } from '../payment-allocation-editor/payment-allocation-editor.component';
import {
  AllocationAmounts, allocationProblem, planOldestFirst, roundMoney, toAllocations
} from '../payment-allocation-editor/payment-allocation';

/** Leave the allocation to the server (oldest invoice first), or say which invoices this pays. */
export type AllocationMode = 'FIFO' | 'MANUAL';

/** One invoice the oldest-first rule would pay, and how much of it. */
export interface FifoRow {
  invoice: SalesInvoiceListItemModel;
  amount: number;
}

/** The customer must be one picked from the list, not text typed into the box. */
function customerPicked(control: AbstractControl): ValidationErrors | null {
  const value = control.value as BusinessPartnerModel | string | null;
  return value && typeof value === 'object' && !!value.uuid ? null : { customerRequired: true };
}

@Component({
  selector: 'app-customer-payment-form',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule, FormsModule,
    ButtonModule, ToastModule, TooltipModule, DropdownModule, CalendarModule,
    InputNumberModule, InputTextModule, TextareaModule, AutoCompleteModule, SelectButtonModule,
    PaymentAllocationEditorComponent
  ],
  templateUrl: './customer-payment-form.component.html',
  styleUrls: ['./customer-payment-form.component.scss'],
  providers: [MessageService]
})
export class CustomerPaymentFormComponent implements OnInit {
  form: FormGroup;

  methodOptions = CUSTOMER_PAYMENT_METHODS.map(m => ({ label: m.label, value: m.value }));
  modeOptions = [
    { label: 'Oldest invoices first', value: 'FIFO' },
    { label: 'Choose the invoices',   value: 'MANUAL' }
  ];

  /** Money cannot have been received tomorrow. */
  maxDate = new Date();

  customerSuggestions: BusinessPartnerModel[] = [];
  currencyOptions: { label: string; value: string }[] = [];

  /** Every unpaid invoice the customer has, in any currency. */
  openInvoices: SalesInvoiceListItemModel[] = [];
  truncated = false;
  isLoadingInvoices = false;

  mode: AllocationMode = 'FIFO';
  amounts: AllocationAmounts = {};

  isSaving = false;

  constructor(
    private fb: FormBuilder,
    private route: ActivatedRoute,
    private router: Router,
    private partnerService: BusinessPartnerService,
    private invoiceService: SalesInvoiceService,
    private paymentService: CustomerPaymentService,
    private currenciesService: CurrenciesService,
    public authService: AuthService,
    private messageService: MessageService
  ) {
    this.form = this.fb.group({
      customer:      [null as BusinessPartnerModel | string | null, [customerPicked]],
      method:        ['BANK_TRANSFER', Validators.required],
      amount:        [null as number | null, [Validators.required, Validators.min(0.01)]],
      currencyCode:  [null as string | null, Validators.required],
      paymentDate:   [new Date() as Date | null, Validators.required],
      chequeNumber:  ['', Validators.maxLength(30)],
      bankReference: ['', Validators.maxLength(100)],
      notes:         ['', Validators.maxLength(500)]
    });
  }

  ngOnInit() {
    this.currenciesService.getAll().subscribe({
      next: (res) => {
        this.currencyOptions = (res.result ?? [])
          .filter(c => !!c.code)
          .map(c => ({ label: `${c.code} — ${c.name}`, value: c.code! }));
      },
      error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'The currency list could not be loaded.' })
    });

    const params = this.route.snapshot.queryParamMap;
    const partnerId = params.get('partnerId');
    if (partnerId) this.startFor(partnerId, params.get('invoiceUuid'));
  }

  // ── Form state ──────────────────────────────────────────────────────────────

  get customer(): BusinessPartnerModel | null {
    const v = this.form.get('customer')?.value;
    return v && typeof v === 'object' && v.uuid ? v : null;
  }

  get method(): string { return this.form.get('method')?.value; }
  get isCheque(): boolean { return this.method === 'CHEQUE'; }
  get currencyCode(): string { return this.form.get('currencyCode')?.value ?? ''; }
  get amount(): number { return roundMoney(this.form.get('amount')?.value ?? 0); }

  /** The invoices this payment could be applied to: the customer's, in the currency it arrived in. */
  get matching(): SalesInvoiceListItemModel[] {
    return this.openInvoices.filter(i => i.currencyCode === this.currencyCode);
  }

  /** How many of the customer's unpaid invoices are in some other currency, and so cannot be paid by this. */
  get otherCurrencyCount(): number {
    return this.currencyCode ? this.openInvoices.length - this.matching.length : 0;
  }

  // ── Starting from an invoice or a customer ──────────────────────────────────

  /** Arrived from an invoice's "Record payment": the customer is known, and so is what is owed. */
  private startFor(partnerId: string, invoiceUuid: string | null) {
    this.partnerService.getPartnerById(partnerId).subscribe({
      next: (res) => {
        if (!res.result) return;
        this.form.get('customer')?.setValue(res.result);
        this.loadOpenInvoices(partnerId, () => this.preselect(invoiceUuid));
      },
      error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'The customer could not be loaded.' })
    });
  }

  /** Pays that invoice in full, as the starting point: the amount, the currency and the allocation follow from it. */
  private preselect(invoiceUuid: string | null) {
    if (!invoiceUuid) return;

    const invoice = this.openInvoices.find(i => i.uuid === invoiceUuid);
    if (!invoice) {
      this.messageService.add({ severity: 'info', summary: 'Nothing to pay', detail: 'That invoice has nothing left owing.' });
      return;
    }

    this.form.patchValue({ currencyCode: invoice.currencyCode, amount: invoice.balanceDue });
    this.mode = 'MANUAL';
    this.amounts = { [invoice.uuid]: invoice.balanceDue };
  }

  // ── Customer ────────────────────────────────────────────────────────────────

  searchCustomers(event: AutoCompleteCompleteEvent) {
    this.partnerService.getPartners({ isCustomer: true, active: true, search: event.query, pageSize: 20 }).subscribe({
      next: (res) => { this.customerSuggestions = res.result?.data ?? []; },
      error: () => { this.customerSuggestions = []; }
    });
  }

  onCustomerSelected(partner: BusinessPartnerModel) {
    this.amounts = {};
    this.openInvoices = [];
    if (partner?.uuid) this.loadOpenInvoices(partner.uuid);
  }

  onCustomerCleared() {
    this.amounts = {};
    this.openInvoices = [];
    this.truncated = false;
  }

  private loadOpenInvoices(partnerId: string, then?: () => void) {
    this.isLoadingInvoices = true;

    this.invoiceService.getOpenInvoices(partnerId).subscribe({
      next: (open) => {
        this.isLoadingInvoices = false;
        this.openInvoices = open.invoices;
        this.truncated = open.truncated;

        // One currency owing means that is the one the money most likely came in; more than one is for the user to say.
        const currencies = [...new Set(open.invoices.map(i => i.currencyCode))];
        if (!this.currencyCode && currencies.length === 1) this.form.get('currencyCode')?.setValue(currencies[0]);

        then?.();
      },
      error: (err) => {
        this.isLoadingInvoices = false;
        this.openInvoices = [];
        this.messageService.add({
          severity: 'error', summary: 'Error', detail: err?.error?.message ?? "The customer's invoices could not be loaded."
        });
      }
    });
  }

  // ── Method and currency ─────────────────────────────────────────────────────

  /** A cheque is matched to its receipt by number, so it is asked for; no other method has one. */
  onMethodChange() {
    const cheque = this.form.get('chequeNumber')!;
    if (this.isCheque) {
      cheque.addValidators(Validators.required);
    } else {
      cheque.removeValidators(Validators.required);
      cheque.setValue('');
    }
    cheque.updateValueAndValidity();
  }

  /** Another currency is another set of invoices, so what was chosen among the old ones no longer applies. */
  onCurrencyChange() {
    this.amounts = {};
  }

  // ── Allocation ──────────────────────────────────────────────────────────────

  setMode(mode: AllocationMode) {
    this.mode = mode;
  }

  /** What the server will do when left to decide: the oldest invoice first, as far as the money goes. */
  get fifoRows(): FifoRow[] {
    const plan = planOldestFirst(this.matching, this.amount);
    return this.matching
      .filter(i => (plan[i.uuid] ?? 0) > 0)
      .map(i => ({ invoice: i, amount: plan[i.uuid]! }));
  }

  get fifoApplied(): number {
    return roundMoney(this.fifoRows.reduce((sum, r) => sum + r.amount, 0));
  }

  get manualProblem(): string | null {
    return allocationProblem(this.matching, this.amounts, this.amount);
  }

  // ── Saving ──────────────────────────────────────────────────────────────────

  buildRequest(): RecordCustomerPaymentRequest {
    const v = this.form.getRawValue();
    const text = (s: string | null) => (s ?? '').trim() || undefined;

    return {
      partnerId: (v.customer as BusinessPartnerModel).uuid!,
      amount: this.amount,
      method: v.method,
      currencyCode: v.currencyCode,
      paymentDate: v.paymentDate ? toDateOnly(v.paymentDate as Date) : undefined,
      chequeNumber: this.isCheque ? text(v.chequeNumber) : undefined,
      bankReference: text(v.bankReference),
      notes: text(v.notes),
      // Left out, the server pays the oldest invoices first. A list, even an empty one, is applied as written.
      allocations: this.mode === 'MANUAL' ? toAllocations(this.matching, this.amounts) : undefined
    };
  }

  /** The first thing wrong with the form, in words, for the toast. */
  firstProblem(): string | null {
    if (this.form.get('customer')?.invalid) return 'Choose the customer from the list.';
    if (this.form.get('amount')?.invalid) return 'Enter the amount received, above zero.';
    if (this.form.get('currencyCode')?.invalid) return 'Choose the currency the money arrived in.';
    if (this.form.get('paymentDate')?.invalid) return 'Enter the date the money was received.';
    if (this.isCheque && this.form.get('chequeNumber')?.invalid) return 'A cheque needs its cheque number.';
    if (this.form.invalid) return 'Some details are not valid.';
    if (this.mode === 'MANUAL') return this.manualProblem;
    return null;
  }

  save() {
    this.form.markAllAsTouched();
    const problem = this.firstProblem();
    if (problem) {
      this.messageService.add({ severity: 'warn', summary: 'Check the payment', detail: problem });
      return;
    }
    if (this.isSaving) return;
    this.isSaving = true;

    this.paymentService.recordPayment(this.buildRequest()).subscribe({
      next: (res) => {
        this.isSaving = false;
        const done = res.result;
        this.messageService.add({
          severity: 'success', summary: 'Payment recorded',
          detail: done ? this.describe(done.paymentNumber, done.allocatedAmount, done.allocations.length, done.unallocatedAmount, done.currencyCode) : 'Payment recorded.'
        });

        if (done && this.authService.hasPermission('CUSTOMER_PAYMENT_VIEW')) {
          this.router.navigate(['/portal/pages/finance/customer-payments', done.paymentUuid]);
        } else {
          this.reset();
        }
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({
          severity: 'error', summary: 'Not recorded',
          // The server says what it will not take: a cheque with no number, an invoice already paid.
          detail: err?.error?.message ?? 'The payment could not be recorded.'
        });
      }
    });
  }

  private describe(number: string, applied: number, invoices: number, onAccount: number, currency: string): string {
    const money = (n: number) => `${n.toFixed(2)} ${currency}`;
    const appliedText = invoices > 0
      ? `${money(applied)} applied to ${invoices} invoice${invoices === 1 ? '' : 's'}`
      : 'not applied to any invoice';
    const heldText = onAccount > 0 ? `, ${money(onAccount)} held on the customer's account` : '';
    return `${number}: ${appliedText}${heldText}`;
  }

  /** Ready for the next payment. */
  private reset() {
    this.form.reset({ method: 'BANK_TRANSFER', paymentDate: new Date(), chequeNumber: '', bankReference: '', notes: '' });
    this.onMethodChange();
    this.openInvoices = [];
    this.amounts = {};
    this.mode = 'FIFO';
  }
}
