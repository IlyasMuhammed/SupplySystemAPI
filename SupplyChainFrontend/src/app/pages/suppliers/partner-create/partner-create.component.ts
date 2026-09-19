import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { CardModule } from 'primeng/card';
import { ToastModule } from 'primeng/toast';
import { CheckboxModule } from 'primeng/checkbox';
import { DividerModule } from 'primeng/divider';
import { MessageService } from 'primeng/api';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../services/business-partner.service';

// Addendum 29 §1.6 — "Partner create/edit form with dynamic fields based on type flags." One form
// serves both: no :uuid route param is CREATE, a :uuid param loads that partner and switches to
// EDIT. Vehicle Types only shows once Carrier is checked, Service Categories only once Service
// Provider is checked — the dynamic-fields part of the task.
@Component({
  selector: 'app-partner-create',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule, RouterModule,
    ButtonModule, InputTextModule, TextareaModule, CardModule, ToastModule,
    CheckboxModule, DividerModule
  ],
  templateUrl: './partner-create.component.html',
  styleUrls: ['./partner-create.component.scss'],
  providers: [MessageService]
})
export class PartnerCreateComponent implements OnInit {
  partnerForm!: FormGroup;
  isSubmitting = false;
  isLoading = false;

  uuid: string | null = null;
  get isEditMode(): boolean { return !!this.uuid; }

  constructor(
    private fb: FormBuilder,
    private partnerService: BusinessPartnerService,
    private route: ActivatedRoute,
    public router: Router,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.initForm();

    this.uuid = this.route.snapshot.paramMap.get('uuid');
    if (this.uuid) this.loadPartner(this.uuid);
  }

  private initForm() {
    this.partnerForm = this.fb.group({
      companyName:       ['', [Validators.required, Validators.minLength(2), Validators.maxLength(200)]],
      partnerCode:       ['', [Validators.required, Validators.maxLength(10)]],
      isVendor:          [false],
      isCustomer:        [false],
      isCarrier:         [false],
      isServiceProvider: [false],
      vehicleTypes:      [''],
      serviceCategories: ['']
    });
  }

  // At least one capability must be selected — a partner that is nothing is not a valid row (the
  // FSD's own eight combinations all have at least one flag set; the backend validator enforces
  // the full rule, this is just the earliest, friendliest place to catch the obvious case).
  get hasNoCapabilitySelected(): boolean {
    const v = this.partnerForm.value;
    return !v.isVendor && !v.isCustomer && !v.isCarrier && !v.isServiceProvider;
  }

  private loadPartner(uuid: string) {
    this.isLoading = true;
    this.partnerService.getPartnerById(uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.partnerForm.patchValue(res.result);
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Business partner not found.' });
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load business partner.' });
      }
    });
  }

  save() {
    if (this.partnerForm.invalid || this.hasNoCapabilitySelected) {
      this.partnerForm.markAllAsTouched();
      if (this.hasNoCapabilitySelected) {
        this.messageService.add({
          severity: 'warn', summary: 'Check the form',
          detail: 'Select at least one of Vendor, Customer, Carrier or Service Provider.'
        });
      }
      return;
    }

    this.isSubmitting = true;
    // uuid is deliberately omitted on create — the backend always generates its own, and sending
    // an empty string previously made ASP.NET's Guid model binding reject the request outright.
    const model: BusinessPartnerModel = {
      ...(this.uuid ? { uuid: this.uuid } : {}),
      partnerType: '', // computed server-side from the flags (§1.2) — never sent as a real value
      ...this.partnerForm.value
    };

    const onSuccess = (success: boolean, message: string, createdUuid?: string) => {
      this.isSubmitting = false;
      if (success) {
        this.messageService.add({
          severity: 'success', summary: this.isEditMode ? 'Updated' : 'Created',
          detail: `Business partner ${this.isEditMode ? 'updated' : 'created'}.`
        });
        const targetUuid = this.isEditMode ? this.uuid! : createdUuid!;
        this.router.navigate(['/portal/pages/suppliers/partner-detail', targetUuid]);
      } else {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: message || 'Save failed.' });
      }
    };
    const onError = (err: { error?: { message?: string } }) => {
      this.isSubmitting = false;
      this.messageService.add({
        severity: 'error', summary: 'Error',
        detail: err?.error?.message || 'Save failed.'
      });
    };

    // Two separate subscribe calls rather than one shared Observable — updatePartner and
    // createPartner return different response shapes (ApiResponse vs ApiResponse<string>), and
    // unifying them into one variable defeats TypeScript's ability to infer either signature.
    if (this.isEditMode) {
      this.partnerService.updatePartner(this.uuid!, model).subscribe({
        next: (res) => onSuccess(res.success, res.message),
        error: onError
      });
    } else {
      this.partnerService.createPartner(model).subscribe({
        next: (res) => onSuccess(res.success, res.message, res.result),
        error: onError
      });
    }
  }

  cancel() {
    this.router.navigate(['/portal/pages/suppliers/partner-list']);
  }
}
