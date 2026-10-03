import { Component, OnInit, effect } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import {
  AbstractControl, FormBuilder, FormGroup, ReactiveFormsModule, ValidationErrors, Validators
} from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { AutoCompleteModule, AutoCompleteCompleteEvent } from 'primeng/autocomplete';
import { MessageService } from 'primeng/api';
import { Observable } from 'rxjs';

import {
  SalesPreorderService, SaleQuotation, CreateSaleQuotationRequest, UpdateSaleQuotationRequest
} from '../../../../services/sales-preorder.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { TenantService } from '../../../service/tenant.service';
import { fromDateOnly, toDateOnly } from '../../../../shared/date-only';
import { QUOTATIONS_ROUTE } from '../sale-quotation.shared';

/** The customer must be one picked from the list, not text typed into the box. */
function customerPicked(control: AbstractControl): ValidationErrors | null {
  const value = control.value as BusinessPartnerModel | string | null;
  return value && typeof value === 'object' && !!value.uuid ? null : { customerRequired: true };
}

/** BR-C2-03: valid to on or after valid from. */
function validToNotBeforeFrom(group: AbstractControl): ValidationErrors | null {
  const from = group.get('validFrom')?.value as Date | null;
  const to = group.get('validTo')?.value as Date | null;
  return from && to && toDateOnly(to) < toDateOnly(from) ? { validToBeforeFrom: true } : null;
}

/**
 * A sale quotation's header: "+ New Quotation" makes a DRAFT (then its lines are added on the quotation), and
 * "Edit header" changes a draft's. The customer cannot change once the quotation exists.
 */
@Component({
  selector: 'app-sale-quotation-form',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule, ButtonModule, ToastModule, DropdownModule, CalendarModule,
    InputTextModule, TextareaModule, AutoCompleteModule
  ],
  templateUrl: './sale-quotation-form.component.html',
  styleUrls: ['./sale-quotation-form.component.scss'],
  providers: [MessageService]
})
export class SaleQuotationFormComponent implements OnInit {
  readonly quotationsRoute = QUOTATIONS_ROUTE;

  uuid: string | null = null;
  quotation: SaleQuotation | null = null;
  form: FormGroup;

  isLoading = false;
  isSaving = false;
  notFound = false;
  /** A quotation past DRAFT is read-only. */
  notEditable = false;
  error: string | null = null;

  customerSuggestions: BusinessPartnerModel[] = [];
  currencyOptions: { label: string; value: string }[] = [];

  constructor(
    fb: FormBuilder,
    private route: ActivatedRoute,
    private router: Router,
    private salesPreorderService: SalesPreorderService,
    private partnerService: BusinessPartnerService,
    private currenciesService: CurrenciesService,
    private tenantService: TenantService
  ) {
    this.form = fb.group({
      customer:              [null as BusinessPartnerModel | string | null, [customerPicked]],
      customerReference:     ['', Validators.maxLength(50)],
      customerReferenceDate: [null as Date | null],
      currencyId:            [null as string | null],
      validFrom:             [this.today(), Validators.required],
      validTo:               [null as Date | null, Validators.required],
      paymentTerms:          ['', Validators.maxLength(200)],
      deliveryTerms:         ['', Validators.maxLength(200)],
      notes:                 ['', Validators.maxLength(2000)],
      internalNotes:         ['', Validators.maxLength(2000)]
    }, { validators: validToNotBeforeFrom });

    // The tenant is loaded once by the shell, and may arrive after the currency list or before it.
    effect(() => {
      this.tenantService.tenant();
      this.applyDefaultCurrency();
    });
  }

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid');

    this.currenciesService.getAll().subscribe({
      next: (res) => {
        this.currencyOptions = (res.result ?? []).map(c => ({ label: c.code ? `${c.name} (${c.code})` : c.name, value: c.id }));
        this.applyDefaultCurrency();
      },
      error: () => { this.error = 'The currency list could not be loaded.'; }
    });

    if (this.uuid) this.load(this.uuid);
  }

  get isEdit(): boolean { return !!this.uuid; }

  get backLink(): string[] { return this.uuid ? [QUOTATIONS_ROUTE, this.uuid] : [QUOTATIONS_ROUTE]; }

  private today(): Date { return new Date(new Date().setHours(0, 0, 0, 0)); }

  /** A new quotation is in the organization's base currency unless one is chosen (the server does the same). */
  private applyDefaultCurrency() {
    const control = this.form?.get('currencyId');
    if (this.isEdit || !control || control.value) return;
    const base = this.tenantService.tenant()?.baseCurrency;
    if (base && this.currencyOptions.some(o => o.value === base)) control.setValue(base);
  }

  /** The server refuses a new currency while priced lines exist: their prices are in the old one. */
  get currencyLocked(): boolean {
    return !!this.quotation?.lines.some(l => l.lineType !== 'REJECTED');
  }

  private load(uuid: string) {
    this.isLoading = true;
    this.salesPreorderService.getQuotation(uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        const q = res.result;
        if (!res.success || !q) { this.notFound = true; return; }
        this.quotation = q;
        if (q.status !== 'DRAFT' || !q.isEditable) { this.notEditable = true; return; }

        this.form.patchValue({
          customer: { uuid: q.partnerId, companyName: q.partnerName || q.partnerId } as BusinessPartnerModel,
          customerReference: q.customerReference ?? '',
          customerReferenceDate: q.customerReferenceDate ? fromDateOnly(q.customerReferenceDate) : null,
          currencyId: q.currencyId,
          validFrom: fromDateOnly(q.validFrom),
          validTo: fromDateOnly(q.validTo),
          paymentTerms: q.paymentTerms ?? '',
          deliveryTerms: q.deliveryTerms ?? '',
          notes: q.notes ?? '',
          internalNotes: q.internalNotes ?? ''
        });
        this.form.get('customer')!.disable();
        if (this.currencyLocked) this.form.get('currencyId')!.disable();
      },
      error: (err) => {
        this.isLoading = false;
        this.notFound = err?.status === 404;
        if (!this.notFound) this.error = 'Failed to load the sale quotation.';
      }
    });
  }

  searchCustomers(event: AutoCompleteCompleteEvent) {
    this.partnerService.getPartners({ isCustomer: true, active: true, search: event.query, pageSize: 20 }).subscribe({
      next: (res) => { this.customerSuggestions = res.result?.data ?? []; },
      error: () => { this.customerSuggestions = []; }
    });
  }

  invalid(name: string): boolean {
    const c = this.form.get(name)!;
    return c.invalid && (c.dirty || c.touched);
  }

  save() {
    if (this.isSaving || this.notEditable || this.notFound) return;
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.error = this.form.hasError('validToBeforeFrom')
        ? 'Valid to must be on or after valid from.'
        : 'Check the highlighted fields.';
      return;
    }

    const v = this.form.getRawValue();
    const text = (s: string | null | undefined) => s?.trim() || null;
    const header: UpdateSaleQuotationRequest = {
      customerReference: text(v.customerReference),
      customerReferenceDate: v.customerReferenceDate ? toDateOnly(v.customerReferenceDate) : null,
      currencyId: v.currencyId || null,
      validFrom: toDateOnly(v.validFrom),
      validTo: toDateOnly(v.validTo),
      paymentTerms: text(v.paymentTerms),
      deliveryTerms: text(v.deliveryTerms),
      notes: text(v.notes),
      internalNotes: text(v.internalNotes)
    };

    this.error = null;
    this.isSaving = true;
    const call: Observable<{ result?: string | null }> = this.uuid
      ? this.salesPreorderService.updateQuotation(this.uuid, header)
      : this.salesPreorderService.createQuotation({ ...header, partnerId: (v.customer as BusinessPartnerModel).uuid! } as CreateSaleQuotationRequest);

    call.subscribe({
      next: (res) => {
        this.isSaving = false;
        this.router.navigate([QUOTATIONS_ROUTE, this.uuid ?? (res.result as string)]);
      },
      error: (err) => {
        this.isSaving = false;
        this.error = err?.error?.message ?? 'The quotation could not be saved.';
      }
    });
  }
}
