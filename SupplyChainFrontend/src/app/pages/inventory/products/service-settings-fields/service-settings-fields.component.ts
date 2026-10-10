import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AbstractControl, FormGroup, ReactiveFormsModule, ValidationErrors } from '@angular/forms';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { TooltipModule } from 'primeng/tooltip';
import { ServiceBillingModel, ServiceCategory, ServiceInvoicingPolicy } from '../../../../services/inventory.service';

/** A36 D-2 — a service product's settings, shared by the product create/edit page and the detail page's edit dialog. */
export const SERVICE_INVOICING_POLICY_OPTIONS: { label: string; value: ServiceInvoicingPolicy }[] = [
  { label: 'Fixed price',     value: 'FIXED_PRICE' },
  { label: 'Cost plus',       value: 'COST_PLUS' },
  { label: 'Time & material', value: 'TIME_AND_MATERIAL' }
];

export const SERVICE_BILLING_MODEL_OPTIONS: { label: string; value: ServiceBillingModel }[] = [
  { label: 'Inclusive',    value: 'INCLUSIVE' },
  { label: 'Pass-through', value: 'PASS_THROUGH' }
];

/** A37 D-10 — a service product's category. */
export const SERVICE_CATEGORY_OPTIONS: { label: string; value: ServiceCategory }[] = [
  { label: 'General',      value: 'GENERAL' },
  { label: 'Installation', value: 'INSTALLATION' },
  { label: 'Repair',       value: 'REPAIR' },
  { label: 'Maintenance',  value: 'MAINTENANCE' },
  { label: 'Consulting',   value: 'CONSULTING' }
];

export function serviceCategoryLabel(code: string | null | undefined): string {
  return SERVICE_CATEGORY_OPTIONS.find(o => o.value === code)?.label ?? (code || '—');
}

export function serviceInvoicingPolicyLabel(code: string | null | undefined): string {
  return SERVICE_INVOICING_POLICY_OPTIONS.find(o => o.value === code)?.label ?? (code || '—');
}

export function serviceBillingModelLabel(code: string | null | undefined): string {
  return SERVICE_BILLING_MODEL_OPTIONS.find(o => o.value === code)?.label ?? (code || '—');
}

/** SVC-P — "Estimated duration must be a positive number"; empty is allowed. */
function positiveOrEmpty(control: AbstractControl): ValidationErrors | null {
  const v = control.value;
  return v == null || v === '' || Number(v) > 0 ? null : { positive: true };
}

/** The controls to spread into a product form group. */
export function serviceSettingsControls(): Record<string, unknown[]> {
  return {
    serviceInvoicingPolicy: [null],
    serviceBillingModel:    [null],
    estimatedDurationHours: [null, positiveOrEmpty],
    hasServiceBom:          [false],
    isSubcontractable:      [false],
    serviceCategory:        [null],
    requiresSiteVisit:      [false]
  };
}

export const EMPTY_SERVICE_SETTINGS = {
  serviceInvoicingPolicy: null, serviceBillingModel: null, estimatedDurationHours: null,
  hasServiceBom: false, isSubcontractable: false, serviceCategory: null, requiresSiteVisit: false
} as const;

/** The service fields for a create/update payload: the form's values for a SERVICE, cleared (null/false) otherwise. */
export function serviceSettingsPayload(raw: {
  productType?: string | null; serviceInvoicingPolicy?: ServiceInvoicingPolicy | null; serviceBillingModel?: ServiceBillingModel | null;
  estimatedDurationHours?: number | null; hasServiceBom?: boolean; isSubcontractable?: boolean;
  serviceCategory?: ServiceCategory | null; requiresSiteVisit?: boolean;
}): {
  serviceInvoicingPolicy: ServiceInvoicingPolicy | null; serviceBillingModel: ServiceBillingModel | null;
  estimatedDurationHours: number | null; hasServiceBom: boolean; isSubcontractable: boolean;
  serviceCategory: ServiceCategory | null; requiresSiteVisit: boolean;
} {
  if (raw.productType !== 'SERVICE') return { ...EMPTY_SERVICE_SETTINGS };
  return {
    serviceInvoicingPolicy: raw.serviceInvoicingPolicy ?? null,
    serviceBillingModel:    raw.serviceBillingModel ?? null,
    estimatedDurationHours: raw.estimatedDurationHours ?? null,
    hasServiceBom:          !!raw.hasServiceBom,
    isSubcontractable:      !!raw.isSubcontractable,
    serviceCategory:        raw.serviceCategory ?? null,
    requiresSiteVisit:      !!raw.requiresSiteVisit
  };
}

/** Switching the type away from SERVICE clears the service settings. */
export function clearServiceSettingsUnlessService(form: FormGroup): void {
  if (form.get('productType')?.value !== 'SERVICE') form.patchValue({ ...EMPTY_SERVICE_SETTINGS });
}

@Component({
  selector: 'app-service-settings-fields',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, DropdownModule, InputNumberModule, ToggleSwitchModule, TooltipModule],
  template: `
<div class="sf-fields" [formGroup]="group" data-testid="service-settings">
  <div class="sf-fld">
    <label for="svcInvoicingPolicy">Invoicing policy
      <i class="pi pi-info-circle svc-info" tooltipPosition="top"
         pTooltip="How the customer is billed. Fixed price: one agreed price for the service. Cost plus: the actual cost of materials and subcontracted work plus a margin. Time & material: hours worked at the hourly rate (the selling price) plus the materials used."></i>
    </label>
    <p-dropdown inputId="svcInvoicingPolicy" [options]="policyOptions" formControlName="serviceInvoicingPolicy"
                optionLabel="label" optionValue="value" placeholder="Select policy" [showClear]="true"
                styleClass="w-full" appendTo="body"></p-dropdown>
    <small class="svc-hint" *ngIf="group.get('serviceInvoicingPolicy')?.value === 'TIME_AND_MATERIAL'">
      The default variant's selling price is the hourly rate and is required.
    </small>
  </div>

  <div class="sf-fld">
    <label for="svcBillingModel">Billing model
      <i class="pi pi-info-circle svc-info" tooltipPosition="top"
         pTooltip="Inclusive: materials used are part of the service price. Pass-through: materials and subcontracted work are billed to the customer on top of the service."></i>
    </label>
    <p-dropdown inputId="svcBillingModel" [options]="billingOptions" formControlName="serviceBillingModel"
                optionLabel="label" optionValue="value" placeholder="Select model" [showClear]="true"
                styleClass="w-full" appendTo="body"></p-dropdown>
  </div>

  <div class="sf-fld">
    <label for="svcDuration">Estimated duration (hours)
      <i class="pi pi-info-circle svc-info" tooltipPosition="top"
         pTooltip="Typical time to deliver this service once, used to plan service orders."></i>
    </label>
    <p-inputnumber inputId="svcDuration" formControlName="estimatedDurationHours" mode="decimal"
                   [minFractionDigits]="0" [maxFractionDigits]="2" [min]="0" placeholder="e.g. 2.5" styleClass="w-full"></p-inputnumber>
    <small class="svc-error" *ngIf="group.get('estimatedDurationHours')?.errors?.['positive']" data-testid="svc-duration-error">
      Estimated duration must be a positive number.
    </small>
  </div>

  <div class="sf-fld">
    <label for="svcCategory">Service category
      <i class="pi pi-info-circle svc-info" tooltipPosition="top"
         pTooltip="What kind of service this is, for reporting and for planning service orders."></i>
    </label>
    <p-dropdown inputId="svcCategory" [options]="categoryOptions" formControlName="serviceCategory"
                optionLabel="label" optionValue="value" placeholder="Select category" [showClear]="true"
                styleClass="w-full" appendTo="body" data-testid="svc-category"></p-dropdown>
  </div>

  <div class="sf-fld">
    <label>Options</label>
    <div class="svc-toggles">
      <span class="svc-toggle">
        <p-toggleswitch formControlName="hasServiceBom" inputId="svcHasBom"></p-toggleswitch>
        <label for="svcHasBom">Has service BOM</label>
        <i class="pi pi-info-circle svc-info" tooltipPosition="top"
           pTooltip="The service uses a standard list of parts, subcontracted work and labor hours (its Bill of Materials, maintained on the product's BOM tab). Until a BOM is active, orders run with ad-hoc materials."></i>
      </span>
      <span class="svc-toggle">
        <p-toggleswitch formControlName="isSubcontractable" inputId="svcSubcontract"></p-toggleswitch>
        <label for="svcSubcontract">Is subcontractable</label>
        <i class="pi pi-info-circle svc-info" tooltipPosition="top"
           pTooltip="The service can be performed by an outside vendor and bought in on a purchase order."></i>
      </span>
      <span class="svc-toggle">
        <p-toggleswitch formControlName="requiresSiteVisit" inputId="svcSiteVisit" data-testid="svc-site-visit"></p-toggleswitch>
        <label for="svcSiteVisit">Requires site visit</label>
        <i class="pi pi-info-circle svc-info" tooltipPosition="top"
           pTooltip="The service is performed at the customer's site rather than in the workshop."></i>
      </span>
    </div>
  </div>
</div>
  `,
  styles: [`
    .svc-info { font-size: .8rem; color: var(--sms-text-muted); margin-left: .25rem; cursor: help; }
    .svc-hint { color: var(--sms-text-muted); }
    .svc-error { color: var(--sms-danger); }
    .svc-toggles { display: flex; flex-wrap: wrap; gap: 1rem; padding-top: .25rem; }
    .svc-toggle { display: inline-flex; align-items: center; gap: .4rem; }
    .svc-toggle label { margin: 0; }
    .w-full { width: 100%; }
  `]
})
export class ServiceSettingsFieldsComponent {
  /** The product form group holding the controls from serviceSettingsControls(). */
  @Input({ required: true }) group!: FormGroup;

  readonly policyOptions = SERVICE_INVOICING_POLICY_OPTIONS;
  readonly billingOptions = SERVICE_BILLING_MODEL_OPTIONS;
  readonly categoryOptions = SERVICE_CATEGORY_OPTIONS;
}
