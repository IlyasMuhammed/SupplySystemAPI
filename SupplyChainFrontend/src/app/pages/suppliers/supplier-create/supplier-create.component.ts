import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { CardModule } from 'primeng/card';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { MultiSelectModule } from 'primeng/multiselect';
import { CheckboxModule } from 'primeng/checkbox';
import { InputNumberModule } from 'primeng/inputnumber';
import { DividerModule } from 'primeng/divider';
import { MessageService } from 'primeng/api';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { SupplierService, SupplierTypeMappingInput } from '../../../services/supplier.service';
import { LookupValuesService } from '../../../services/lookup-values.service';
import { CurrenciesService, CurrencyModel } from '../../../services/currencies.service';
import { PaymentTermsService, PaymentTermModel } from '../../../services/payment-terms.service';
import { CountriesService } from '../../../services/countries.service';
import { CitiesService, CityModel } from '../../../services/cities.service';

@Component({
  selector: 'app-supplier-create',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule,
    ButtonModule, InputTextModule, TextareaModule, CardModule, ToastModule,
    DropdownModule, MultiSelectModule, CheckboxModule, InputNumberModule, DividerModule
  ],
  templateUrl: './supplier-create.component.html',
  styleUrls: ['./supplier-create.component.scss'],
  providers: [MessageService]
})
export class SupplierCreateComponent implements OnInit {
  supplierForm!: FormGroup;
  isSubmitting = false;
  isLoadingLookups = true;

  supplierTypeOptions: { label: string; value: string }[] = [];
  industryOptions:     { label: string; value: string }[] = [];
  paymentTermsOptions: { label: string; value: string }[] = [];
  currencyOptions:     { label: string; value: string; symbol: string | null }[] = [];
  countryOptions:      { label: string; value: string }[] = [];
  cityOptions:         { label: string; value: string }[] = [];
  isCitiesLoading = false;

  constructor(
    private fb: FormBuilder,
    private supplierService: SupplierService,
    private lookupService: LookupValuesService,
    private currenciesService: CurrenciesService,
    private paymentTermsService: PaymentTermsService,
    private countriesService: CountriesService,
    private citiesService: CitiesService,
    public router: Router,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.initForm();
    this.loadLookups();
  }

  private initForm() {
    this.supplierForm = this.fb.group({
      supplierName:          ['', [Validators.required, Validators.minLength(2), Validators.maxLength(200)]],
      supplierCode:          ['', [Validators.required, Validators.maxLength(10)]],
      registrationNo:        [''],
      taxId:                 [''],
      // Address
      country:               [null],
      provinceState:         [''],
      city:                  [{ value: null, disabled: true }],
      addressLine1:          [''],
      addressLine2:          [''],
      postalCode:            [''],
      // Contact
      phone:                 [''],
      fax:                   [''],
      email:                 ['', [Validators.email]],
      website:               [''],
      // Primary contact
      primaryContactName:    [''],
      primaryContactTitle:   [''],
      primaryContactPhone:   [''],
      primaryContactEmail:   ['', [Validators.email]],
      // Commercial
      preferredPaymentTerms: [null],
      preferredCurrency:     [null],
      creditLimit:           [null],
      leadTimeDays:          [null],
      // Classification
      supplierTypeIds:       [[]],
      industryIds:           [[]],
      // Additional
      notes:                 [''],
      isPreferredSupplier:   [false]
    });
  }

  private loadLookups() {
    this.isLoadingLookups = true;
    forkJoin({
      types:    this.lookupService.getByType('supplier-type').pipe(catchError(() => of({ success: false, result: [] as any[], message: '' }))),
      industry: this.lookupService.getByType('industry-category').pipe(catchError(() => of({ success: false, result: [] as any[], message: '' }))),
      payment:  this.paymentTermsService.getAll().pipe(catchError(() => of({ success: false, result: [] as PaymentTermModel[], message: '' }))),
      currency: this.currenciesService.getAll().pipe(catchError(() => of({ success: false, result: [] as CurrencyModel[], message: '' }))),
      country:  this.countriesService.getAllCountries().pipe(catchError(() => of({ success: false, result: [] as any[], message: '' })))
    }).subscribe(res => {
      this.isLoadingLookups = false;
      this.supplierTypeOptions = (res.types.result ?? []).filter((i: any) => i.isActive)
        .map((i: any) => ({ label: i.displayName, value: i.id }));
      this.industryOptions = (res.industry.result ?? []).filter((i: any) => i.isActive)
        .map((i: any) => ({ label: i.displayName, value: i.id }));
      this.paymentTermsOptions = (res.payment.result ?? [])
        .map((p: PaymentTermModel) => ({ label: p.days ? `${p.name} (${p.days} days)` : p.name, value: p.id }));
      this.currencyOptions = (res.currency.result ?? [])
        .map((c: CurrencyModel) => ({ label: `${c.name}${c.code ? ' (' + c.code + ')' : ''}`, value: c.id, symbol: c.symbol }));
      this.countryOptions = (res.country.result ?? [])
        .map((c: any) => ({ label: c.name, value: c.id }));
    });
  }

  onCountryChange() {
    const countryId: string | null = this.supplierForm.get('country')?.value ?? null;
    const cityCtrl = this.supplierForm.get('city')!;
    cityCtrl.setValue(null);
    this.cityOptions = [];
    cityCtrl.disable();

    if (!countryId) return;

    this.isCitiesLoading = true;
    this.citiesService.getCitiesByCountry(countryId).subscribe({
      next: (res) => {
        this.isCitiesLoading = false;
        this.cityOptions = (res.result ?? []).map((c: CityModel) => ({ label: c.name, value: c.id }));
        if (this.cityOptions.length > 0) cityCtrl.enable();
        else this.messageService.add({ severity: 'info', summary: 'No Cities', detail: 'No cities are registered for the selected country.' });
      },
      error: () => {
        this.isCitiesLoading = false;
        this.messageService.add({ severity: 'warn', summary: 'Warning', detail: 'Failed to load cities for the selected country.' });
      }
    });
  }

  // Return the symbol for the currently selected currency
  get selectedCurrencySymbol(): string {
    const id = this.supplierForm.value.preferredCurrency;
    return this.currencyOptions.find(c => c.value === id)?.symbol ?? '';
  }

  onSubmit() {
    if (this.supplierForm.invalid) {
      this.supplierForm.markAllAsTouched();
      return;
    }

    const raw = this.supplierForm.getRawValue();

    const mapToInput = (ids: string[]): SupplierTypeMappingInput[] =>
      (ids ?? []).map((id, idx) => ({ lookupValueId: id, isPrimary: idx === 0, notes: undefined }));

    const payload = {
      ...raw,
      // Send city name string (backend uses string field)
      city:            this.cityOptions.find(c => c.value === raw.city)?.label ?? raw.city ?? undefined,
      country:         this.countryOptions.find(c => c.value === raw.country)?.label ?? raw.country ?? undefined,
      supplierTypeIds: mapToInput(raw.supplierTypeIds),
      industryIds:     mapToInput(raw.industryIds),
      creditLimit:     raw.creditLimit   || undefined,
      leadTimeDays:    raw.leadTimeDays  || undefined,
      preferredPaymentTerms: raw.preferredPaymentTerms || undefined,
      preferredCurrency:     raw.preferredCurrency     || undefined
    };

    this.isSubmitting = true;
    this.supplierService.createSupplier(payload).subscribe({
      next: (response) => {
        this.isSubmitting = false;
        if (response.success) {
          this.messageService.add({ severity: 'success', summary: 'Success', detail: 'Supplier created successfully' });
          setTimeout(() => this.router.navigate(['/portal/pages/suppliers/supplier-list']), 1500);
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: response.message || 'Failed to create supplier' });
        }
      },
      error: (error) => {
        this.isSubmitting = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: error.error?.message || 'Failed to create supplier. Please try again.' });
      }
    });
  }

  get f() { return this.supplierForm.controls; }
}
