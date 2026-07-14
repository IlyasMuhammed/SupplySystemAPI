import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators, FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { TabViewModule } from 'primeng/tabview';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { InputNumberModule } from 'primeng/inputnumber';
import { CheckboxModule } from 'primeng/checkbox';
import { DividerModule } from 'primeng/divider';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DropdownModule } from 'primeng/dropdown';
import { MultiSelectModule } from 'primeng/multiselect';
import { MessageService, ConfirmationService } from 'primeng/api';
import {
  SupplierService,
  SupplierDetailModel,
  ContactModel,
  BankDetailModel,
  DocumentModel,
  SupplierTypeMappingInput
} from '../../../services/supplier.service';
import { LookupValuesService, LookupValueModel } from '../../../services/lookup-values.service';
import { CountriesService, CountryModel } from '../../../services/countries.service';
import { CitiesService, CityModel } from '../../../services/cities.service';

@Component({
  selector: 'app-supplier-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, ReactiveFormsModule,
    ButtonModule, CardModule, TabViewModule, TagModule, ToastModule,
    DialogModule, InputTextModule, TextareaModule, InputNumberModule,
    CheckboxModule, DividerModule, TooltipModule, ConfirmDialogModule,
    DropdownModule, MultiSelectModule
  ],
  templateUrl: './supplier-detail.component.html',
  styleUrls: ['./supplier-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class SupplierDetailComponent implements OnInit {
  uuid = '';
  supplier: SupplierDetailModel = this.createEmptySupplier();
  isLoading = true;

  // ── Edit information dialog ───────────────────────────────────────────────
  showEditDialog   = false;
  editForm!: FormGroup;
  isSavingEdit     = false;
  isLoadingLookups = false;

  supplierTypeOptions: { label: string; value: string }[] = [];
  industryOptions:     { label: string; value: string }[] = [];
  paymentTermsOptions: { label: string; value: string }[] = [];
  currencyOptions:     { label: string; value: string }[] = [];
  countryOptions:      { label: string; value: string }[] = [];
  cityOptions:         { label: string; value: string }[] = [];
  isCitiesLoading = false;

  // ── Status action dialogs ─────────────────────────────────────────────────
  showRejectDialog    = false;
  showBlacklistDialog = false;
  showSuspendDialog   = false;
  rejectReason    = '';
  blacklistReason = '';
  suspendReason   = '';
  suspendReviewDate: Date | null = null;
  isActioning = false;

  // ── Add contact dialog ────────────────────────────────────────────────────
  showContactDialog = false;
  contactForm!: FormGroup;
  isSavingContact = false;

  // ── Bank details ──────────────────────────────────────────────────────────
  bankDetail: BankDetailModel | null = null;
  bankAccessDenied = false;
  showBankEditDialog = false;
  bankForm!: FormGroup;
  isSavingBank = false;

  // ── Documents ─────────────────────────────────────────────────────────────
  documents: DocumentModel[] = [];
  showDocumentDialog = false;
  docForm!: FormGroup;
  isSavingDoc = false;
  selectedFile: File | null = null;
  uploadMode: 'file' | 'url' = 'file';

  documentTypeOptions = [
    { label: 'Contract',      value: 'CONTRACT' },
    { label: 'Certificate',   value: 'CERTIFICATE' },
    { label: 'Tax Document',  value: 'TAX' },
    { label: 'Bank Document', value: 'BANK' },
    { label: 'Other',         value: 'OTHER' }
  ];

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private fb: FormBuilder,
    private supplierService: SupplierService,
    private lookupService: LookupValuesService,
    private countriesService: CountriesService,
    private citiesService: CitiesService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  private createEmptySupplier(): SupplierDetailModel {
    return {
      id: 0, uuid: '', supplierName: '',
      isPreferredSupplier: false, isActive: false, createdDate: '',
      supplierTypes: [], industries: [], contacts: []
    };
  }

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.initForms();
    this.loadSupplier();
  }

  private initForms() {
    this.editForm = this.fb.group({
      supplierName:          ['', [Validators.required, Validators.minLength(2), Validators.maxLength(200)]],
      registrationNo:        [''],
      taxId:                 [''],
      country:               [null],
      provinceState:         [''],
      city:                  [{ value: null, disabled: true }],
      addressLine1:          [''],
      addressLine2:          [''],
      postalCode:            [''],
      phone:                 [''],
      fax:                   [''],
      email:                 ['', [Validators.email]],
      website:               [''],
      primaryContactName:    [''],
      primaryContactTitle:   [''],
      primaryContactPhone:   [''],
      primaryContactEmail:   ['', [Validators.email]],
      preferredPaymentTerms: [null],
      preferredCurrency:     [null],
      creditLimit:           [null],
      leadTimeDays:          [null],
      supplierTypeIds:       [[]],
      industryIds:           [[]],
      notes:                 [''],
      isPreferredSupplier:   [false]
    });

    this.contactForm = this.fb.group({
      contactName: ['', [Validators.required]],
      title:       [''],
      phone:       [''],
      email:       ['', [Validators.email]],
      isPrimary:   [false]
    });

    this.bankForm = this.fb.group({
      bankName:      [''],
      bankAccountNo: [''],
      bankIban:      [''],
      bankSwift:     ['']
    });

    this.docForm = this.fb.group({
      fileName:     ['', [Validators.required]],
      fileUrl:      ['', [Validators.required]],
      documentType: [null]
    });
  }

  // ── Lookups ───────────────────────────────────────────────────────────────

  private loadLookups(): Promise<void> {
    if (this.supplierTypeOptions.length > 0) return Promise.resolve();
    this.isLoadingLookups = true;
    return new Promise((resolve) => {
      forkJoin({
        types:     this.lookupService.getByType('supplier-type'),
        industry:  this.lookupService.getByType('industry-category'),
        allDrops:  this.lookupService.getAllDropdowns(),
        countries: this.countriesService.getAllCountries()
      }).subscribe({
        next: (res) => {
          this.supplierTypeOptions = this.toOptions(res.types.result);
          this.industryOptions     = this.toOptions(res.industry.result);
          this.paymentTermsOptions = (res.allDrops.result?.paymentTerms ?? [])
            .map(p => ({ label: p.name, value: p.id }));
          this.currencyOptions     = (res.allDrops.result?.currency ?? [])
            .map(c => ({ label: c.name, value: c.id }));
          this.countryOptions      = (res.countries.result ?? [])
            .filter((c: CountryModel) => c.isActive)
            .map((c: CountryModel) => ({ label: c.name, value: c.id }));
          this.isLoadingLookups = false;
          resolve();
        },
        error: () => {
          this.isLoadingLookups = false;
          this.messageService.add({ severity: 'warn', summary: 'Warning', detail: 'Some lookup options failed to load' });
          resolve();
        }
      });
    });
  }

  private toOptions(items: LookupValueModel[] | null): { label: string; value: string }[] {
    return (items ?? []).filter(i => i.isActive).map(i => ({ label: i.displayName, value: i.id }));
  }

  // ── Edit supplier info ────────────────────────────────────────────────────

  openEditDialog() {
    this.editForm.patchValue({
      supplierName:          this.supplier.supplierName,
      registrationNo:        this.supplier.registrationNo    ?? '',
      taxId:                 this.supplier.taxId             ?? '',
      country:               null,
      provinceState:         this.supplier.provinceState     ?? '',
      city:                  null,
      addressLine1:          this.supplier.addressLine1      ?? '',
      addressLine2:          this.supplier.addressLine2      ?? '',
      postalCode:            this.supplier.postalCode        ?? '',
      phone:                 this.supplier.phone             ?? '',
      fax:                   this.supplier.fax               ?? '',
      email:                 this.supplier.email             ?? '',
      website:               this.supplier.website           ?? '',
      primaryContactName:    this.supplier.primaryContactName  ?? '',
      primaryContactTitle:   this.supplier.primaryContactTitle ?? '',
      primaryContactPhone:   this.supplier.primaryContactPhone ?? '',
      primaryContactEmail:   this.supplier.primaryContactEmail ?? '',
      preferredPaymentTerms: this.supplier.preferredPaymentTerms ?? null,
      preferredCurrency:     this.supplier.preferredCurrency     ?? null,
      creditLimit:           this.supplier.creditLimit   ?? null,
      leadTimeDays:          this.supplier.leadTimeDays  ?? null,
      supplierTypeIds:       (this.supplier.supplierTypes ?? []).map(t => t.lookupValueId),
      industryIds:           (this.supplier.industries   ?? []).map(i => i.lookupValueId),
      notes:                 this.supplier.notes ?? '',
      isPreferredSupplier:   this.supplier.isPreferredSupplier
    });
    this.editForm.get('city')!.disable();
    this.cityOptions = [];

    this.loadLookups().then(() => {
      // Pre-select country by matching the stored name
      const countryMatch = this.countryOptions.find(c => c.label === (this.supplier.country ?? ''));
      if (countryMatch) {
        this.editForm.get('country')!.setValue(countryMatch.value);
        // Load cities for this country and pre-select city by name
        this.isCitiesLoading = true;
        this.citiesService.getCitiesByCountry(countryMatch.value).subscribe({
          next: (res) => {
            this.isCitiesLoading = false;
            this.cityOptions = (res.result ?? []).map((c: CityModel) => ({ label: c.name, value: c.id }));
            if (this.cityOptions.length > 0) {
              this.editForm.get('city')!.enable();
              const cityMatch = this.cityOptions.find(c => c.label === (this.supplier.city ?? ''));
              if (cityMatch) this.editForm.get('city')!.setValue(cityMatch.value);
            }
          },
          error: () => { this.isCitiesLoading = false; }
        });
      }
      this.showEditDialog = true;
    });
  }

  onEditCountryChange() {
    const countryId = this.editForm.get('country')?.value;
    const cityCtrl = this.editForm.get('city')!;
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
      },
      error: () => { this.isCitiesLoading = false; }
    });
  }

  saveEdit() {
    if (this.editForm.invalid) { this.editForm.markAllAsTouched(); return; }

    const raw = this.editForm.getRawValue();
    const mapToInput = (ids: string[]): SupplierTypeMappingInput[] =>
      (ids ?? []).map((id, idx) => ({ lookupValueId: id, isPrimary: idx === 0, notes: undefined }));

    const countryName = this.countryOptions.find(c => c.value === raw.country)?.label ?? raw.country ?? undefined;
    const cityName    = this.cityOptions.find(c => c.value === raw.city)?.label ?? raw.city ?? undefined;

    const payload = {
      ...raw,
      country:               countryName,
      city:                  cityName,
      supplierTypeIds:       mapToInput(raw.supplierTypeIds),
      industryIds:           mapToInput(raw.industryIds),
      creditLimit:           raw.creditLimit           || undefined,
      leadTimeDays:          raw.leadTimeDays          || undefined,
      preferredPaymentTerms: raw.preferredPaymentTerms || undefined,
      preferredCurrency:     raw.preferredCurrency     || undefined
    };

    this.isSavingEdit = true;
    this.supplierService.patchSupplier(this.uuid, payload).subscribe({
      next: (res) => {
        this.isSavingEdit = false;
        this.showEditDialog = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Supplier information updated successfully.' });
          this.loadSupplier();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isSavingEdit = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to save changes.' });
      }
    });
  }

  get ef() { return this.editForm.controls; }

  // ── Load supplier ─────────────────────────────────────────────────────────

  loadSupplier() {
    this.isLoading = true;
    this.supplierService.getSupplierById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success) {
          this.supplier = res.result;
          this.loadDocuments();
          this.loadBankDetail();
          this.loadLookups();
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load supplier' });
      }
    });
  }

  resolveLookupLabel(options: { label: string; value: string }[], uuid: string | null | undefined): string {
    if (!uuid) return '—';
    return options.find(o => o.value === uuid)?.label ?? uuid;
  }

  resolveDocUrl(url: string): string {
    if (!url) return '';
    return url.startsWith('/') ? `https://localhost:51800${url}` : url;
  }

  private loadBankDetail() {
    this.supplierService.getBankDetail(this.uuid).subscribe({
      next: (res) => { if (res.success) this.bankDetail = res.result; },
      error: (err) => { if (err.status === 403) this.bankAccessDenied = true; }
    });
  }

  private loadDocuments() {
    this.supplierService.getDocuments(this.uuid).subscribe({
      next: (res) => { if (res.success) this.documents = res.result ?? []; },
      error: () => {}
    });
  }

  // ── Status helpers ────────────────────────────────────────────────────────

  getStatusSeverity(status: string | undefined): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (status) {
      case 'ACTIVE':      return 'success';
      case 'PENDING':     return 'warn';
      case 'REJECTED':    return 'danger';
      case 'BLACKLISTED': return 'danger';
      case 'SUSPENDED':   return 'secondary';
      case 'INACTIVE':    return 'secondary';
      default:            return 'info';
    }
  }

  canApprove()   { return ['PENDING', 'SUSPENDED'].includes(this.supplier?.status ?? ''); }
  canReject()    { return this.supplier?.status === 'PENDING'; }
  canBlacklist() { return ['ACTIVE', 'SUSPENDED'].includes(this.supplier?.status ?? ''); }
  canSuspend()   { return this.supplier?.status === 'ACTIVE'; }

  // ── Approve ───────────────────────────────────────────────────────────────

  approveSupplier() {
    this.confirmationService.confirm({
      message: 'Approve this supplier and set status to ACTIVE?',
      header: 'Confirm Approval',
      icon: 'pi pi-check-circle',
      accept: () => {
        this.isActioning = true;
        this.supplierService.approveSupplier(this.uuid).subscribe({
          next: (res) => {
            this.isActioning = false;
            if (res.success) {
              this.messageService.add({ severity: 'success', summary: 'Approved', detail: 'Supplier has been approved.' });
              this.loadSupplier();
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
            }
          },
          error: (err) => {
            this.isActioning = false;
            this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Approval failed.' });
          }
        });
      }
    });
  }

  // ── Reject ────────────────────────────────────────────────────────────────

  openRejectDialog() { this.rejectReason = ''; this.showRejectDialog = true; }

  submitReject() {
    if (!this.rejectReason.trim()) return;
    this.isActioning = true;
    this.supplierService.rejectSupplier(this.uuid, { reason: this.rejectReason }).subscribe({
      next: (res) => {
        this.isActioning = false;
        this.showRejectDialog = false;
        if (res.success) {
          this.messageService.add({ severity: 'info', summary: 'Rejected', detail: 'Supplier has been rejected.' });
          this.loadSupplier();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isActioning = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Rejection failed.' });
      }
    });
  }

  // ── Blacklist ─────────────────────────────────────────────────────────────

  openBlacklistDialog() { this.blacklistReason = ''; this.showBlacklistDialog = true; }

  submitBlacklist() {
    if (!this.blacklistReason.trim()) return;
    this.isActioning = true;
    this.supplierService.blacklistSupplier(this.uuid, { reason: this.blacklistReason }).subscribe({
      next: (res) => {
        this.isActioning = false;
        this.showBlacklistDialog = false;
        if (res.success) {
          this.messageService.add({ severity: 'warn', summary: 'Blacklisted', detail: 'Supplier has been blacklisted.' });
          this.loadSupplier();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isActioning = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Blacklist failed.' });
      }
    });
  }

  // ── Suspend ───────────────────────────────────────────────────────────────

  openSuspendDialog() { this.suspendReason = ''; this.suspendReviewDate = null; this.showSuspendDialog = true; }

  submitSuspend() {
    if (!this.suspendReason.trim()) return;
    this.isActioning = true;
    const reviewDate = this.suspendReviewDate ? this.suspendReviewDate.toISOString() : undefined;
    this.supplierService.suspendSupplier(this.uuid, { reason: this.suspendReason, reviewDate }).subscribe({
      next: (res) => {
        this.isActioning = false;
        this.showSuspendDialog = false;
        if (res.success) {
          this.messageService.add({ severity: 'warn', summary: 'Suspended', detail: 'Supplier has been suspended.' });
          this.loadSupplier();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isActioning = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Suspend failed.' });
      }
    });
  }

  // ── Contacts ──────────────────────────────────────────────────────────────

  openContactDialog() { this.contactForm.reset({ isPrimary: false }); this.showContactDialog = true; }

  saveContact() {
    if (this.contactForm.invalid) { this.contactForm.markAllAsTouched(); return; }
    this.isSavingContact = true;
    this.supplierService.addContact(this.uuid, this.contactForm.value).subscribe({
      next: (res) => {
        this.isSavingContact = false;
        this.showContactDialog = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Contact added.' });
          this.loadSupplier();
        }
      },
      error: (err) => {
        this.isSavingContact = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to add contact.' });
      }
    });
  }

  // ── Bank details ──────────────────────────────────────────────────────────

  openBankEditDialog() {
    this.bankForm.patchValue({
      bankName:      this.bankDetail?.bankName      ?? '',
      bankAccountNo: this.bankDetail?.bankAccountNo ?? '',
      bankIban:      this.bankDetail?.bankIban       ?? '',
      bankSwift:     this.bankDetail?.bankSwift      ?? ''
    });
    this.showBankEditDialog = true;
  }

  saveBank() {
    this.isSavingBank = true;
    this.supplierService.upsertBankDetail(this.uuid, this.bankForm.value).subscribe({
      next: (res) => {
        this.isSavingBank = false;
        this.showBankEditDialog = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Bank details updated.' });
          this.loadBankDetail();
        }
      },
      error: (err) => {
        this.isSavingBank = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to save bank details.' });
      }
    });
  }

  // ── Documents ─────────────────────────────────────────────────────────────

  openDocumentDialog() {
    this.docForm.reset();
    this.selectedFile = null;
    this.uploadMode = 'file';
    this.showDocumentDialog = true;
  }

  onFileSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    this.selectedFile = input.files?.[0] ?? null;
    if (this.selectedFile) {
      this.docForm.patchValue({ fileName: this.selectedFile.name });
      this.docForm.get('fileUrl')?.clearValidators();
      this.docForm.get('fileUrl')?.updateValueAndValidity();
    }
  }

  setUploadMode(mode: 'file' | 'url') {
    this.uploadMode = mode;
    this.selectedFile = null;
    this.docForm.reset();
    if (mode === 'url') {
      this.docForm.get('fileUrl')?.setValidators([Validators.required]);
      this.docForm.get('fileName')?.setValidators([Validators.required]);
    } else {
      this.docForm.get('fileUrl')?.clearValidators();
      this.docForm.get('fileName')?.clearValidators();
    }
    this.docForm.get('fileUrl')?.updateValueAndValidity();
    this.docForm.get('fileName')?.updateValueAndValidity();
  }

  saveDocument() {
    this.isSavingDoc = true;

    if (this.uploadMode === 'file') {
      if (!this.selectedFile) {
        this.messageService.add({ severity: 'warn', summary: 'No file', detail: 'Please select a file to upload.' });
        this.isSavingDoc = false;
        return;
      }
      this.supplierService.uploadDocument(this.uuid, this.selectedFile, this.docForm.value.documentType || undefined).subscribe({
        next: (res) => {
          this.isSavingDoc = false;
          this.showDocumentDialog = false;
          if (res.success) {
            this.messageService.add({ severity: 'success', summary: 'Uploaded', detail: 'Document uploaded successfully.' });
            this.loadDocuments();
          } else {
            this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
          }
        },
        error: (err) => {
          this.isSavingDoc = false;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Upload failed.' });
        }
      });
    } else {
      if (this.docForm.invalid) { this.docForm.markAllAsTouched(); this.isSavingDoc = false; return; }
      this.supplierService.attachDocument(this.uuid, this.docForm.value).subscribe({
        next: (res) => {
          this.isSavingDoc = false;
          this.showDocumentDialog = false;
          if (res.success) {
            this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Document attached.' });
            this.loadDocuments();
          } else {
            this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
          }
        },
        error: (err) => {
          this.isSavingDoc = false;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to attach document.' });
        }
      });
    }
  }

  deleteDocument(doc: DocumentModel) {
    this.confirmationService.confirm({
      message: `Remove document "${doc.fileName}"?`,
      header: 'Confirm Delete',
      icon: 'pi pi-trash',
      accept: () => {
        this.supplierService.softDeleteDocument(this.uuid, doc.id).subscribe({
          next: (res) => {
            if (res.success) {
              this.messageService.add({ severity: 'success', summary: 'Deleted', detail: 'Document removed.' });
              this.loadDocuments();
            }
          },
          error: () => {
            this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to delete document.' });
          }
        });
      }
    });
  }

  goBack() {
    this.router.navigate(['/portal/pages/suppliers/supplier-list']);
  }
}
