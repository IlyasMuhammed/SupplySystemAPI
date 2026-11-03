import { Component, Input, OnChanges, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { catchError, of } from 'rxjs';

import {
  AP_PAYMENT_METHODS, AR_AP_PERMS, AR_PAYMENT_METHODS, ArApService, CREDIT_READ, CREDIT_ROUTE, DunningProcedure, EarlyPaymentTerm, PAYMENT_PRIORITIES,
  PROFILE_READ_CUSTOMER, PROFILE_READ_VENDOR, PartnerFinancialProfile, PartnerFinancialProfileSave, PartnerRole, VENDOR_STATEMENT_READ, VENDOR_STATEMENT_ROUTE
} from '../../../../services/ar-ap.service';
import { AuthService } from '../../../service/auth.service';
import { arApError, labelOf, money, riskTone } from '../ar-ap.shared';

/** Every code that reads some profile (the detail pages show the section to either side). */
export const PROFILE_READ = [...new Set([...PROFILE_READ_CUSTOMER, ...PROFILE_READ_VENDOR])];

/** §1.1 — the codes that read a profile of this role. */
export function profileReadCodes(role: PartnerRole): string[] { return role === 'Customer' ? PROFILE_READ_CUSTOMER : PROFILE_READ_VENDOR; }

interface ProfileDraft {
  dunningProcedureUuid: string | null;
  blockSalesAtDunning: boolean;
  excludeFromDunning: boolean;
  defaultPaymentMethod: string | null;
  paymentPriority: string;
  preferredPaymentMethod: string | null;
  withholdingTaxApplicable: boolean;
  earlyPaymentTermUuid: string | null;
  notes: string;
}

/** §1.2 body: only what changed; a cleared procedure / term goes as the clear flag. */
export function profileChanges(p: PartnerFinancialProfile, d: ProfileDraft, role: PartnerRole): PartnerFinancialProfileSave {
  const body: PartnerFinancialProfileSave = { rowVersion: p.rowVersion ?? null };
  if (role === 'Customer') {
    if ((p.dunningProcedureUuid ?? null) !== d.dunningProcedureUuid) {
      if (d.dunningProcedureUuid) body.dunningProcedureUuid = d.dunningProcedureUuid; else body.clearDunningProcedure = true;
    }
    if (!!p.blockSalesAtDunning !== d.blockSalesAtDunning) body.blockSalesAtDunning = d.blockSalesAtDunning;
    if (!!p.excludeFromDunning !== d.excludeFromDunning) body.excludeFromDunning = d.excludeFromDunning;
    // The server ignores null (= unchanged) and clears on '' (PartnerFinancialProfileService).
    if ((p.defaultPaymentMethod ?? null) !== d.defaultPaymentMethod) body.defaultPaymentMethod = d.defaultPaymentMethod ?? '';
  } else {
    if ((p.paymentPriority ?? 'Normal') !== d.paymentPriority) body.paymentPriority = d.paymentPriority;
    if ((p.preferredPaymentMethod ?? null) !== d.preferredPaymentMethod) body.preferredPaymentMethod = d.preferredPaymentMethod ?? '';
    if (!!p.withholdingTaxApplicable !== d.withholdingTaxApplicable) body.withholdingTaxApplicable = d.withholdingTaxApplicable;
    if ((p.earlyPaymentTermUuid ?? null) !== d.earlyPaymentTermUuid) {
      if (d.earlyPaymentTermUuid) body.earlyPaymentTermUuid = d.earlyPaymentTermUuid; else body.clearEarlyPaymentTerm = true;
    }
  }
  const notes = d.notes.trim();
  if ((p.notes ?? '') !== notes) body.notes = notes;
  return body;
}

/**
 * A48 C1 / D-4 / contract §1 — the partner's financial profile as a section of the customer / vendor detail page (no
 * sf-page: a sub-component). Customer: hold, temporary and effective limit, risk tier, dunning procedure / level /
 * exclusion, default payment method, with a link to credit control. Vendor: payment priority, preferred method,
 * withholding flag, early-payment term, with a link to the vendor statement. The permanent limit and payment terms
 * stay on the partner master (shown, not edited). Editing needs FINANCE_AR_AP_ADMIN; hold / temporary limit / tier
 * change on the credit control page (CREDIT_MANAGE, audited).
 */
@Component({
  selector: 'app-partner-financial-profile',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, CheckboxModule, InputTextModule, SelectModule],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<div class="sf-empty" *ngIf="!canRead" data-testid="profile-no-access">Viewing the {{ role === 'Customer' ? 'receivables' : 'payables' }} profile needs {{ role === 'Customer' ? 'AR_VIEW' : 'AP_VIEW' }}.</div>
<div class="sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
<div class="gl-note er" *ngIf="error" data-testid="profile-error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
<ng-container *ngIf="!loading && p">
  <ng-container *ngIf="!editing">
    <div class="sf-kv" *ngIf="role === 'Customer'" data-testid="profile-customer">
      <span>Credit limit</span><span data-testid="pf-limit">{{ p.creditLimit ? amount(p.creditLimit) : 'No limit' }} <small class="sf-mu">(customer master)</small></span>
      <span>Temporary limit</span><span>{{ p.temporaryLimitActive && p.temporaryCreditLimit ? amount(p.temporaryCreditLimit) + ' until ' + (p.temporaryCreditExpiry | date:'dd MMM yyyy') : '—' }}</span>
      <span>Effective limit</span><span data-testid="pf-effective">{{ p.effectiveCreditLimit ? amount(p.effectiveCreditLimit) : 'No limit' }}</span>
      <span>Credit hold</span><span data-testid="pf-hold"><span class="sf-pill" [ngClass]="p.creditHold ? 'er' : 'ok'">{{ p.creditHold ? 'On hold' : 'No' }}</span>
        <small class="sf-mu" *ngIf="p.creditHold && p.creditHoldReason"> {{ p.creditHoldReason }}</small></span>
      <span>Risk tier</span><span><span class="sf-pill" [ngClass]="tone(p.riskTier)" data-testid="pf-tier">{{ p.riskTier }}</span></span>
      <span>Dunning</span><span data-testid="pf-dunning">{{ p.excludeFromDunning ? 'Excluded' : (p.dunningProcedureCode || 'Organization default') }} · level {{ p.currentDunningLevel || 0 }}
        <ng-container *ngIf="p.lastDunningDate"> · last {{ p.lastDunningDate | date:'dd MMM yyyy' }}</ng-container></span>
      <span>Block sales at dunning</span><span>{{ p.blockSalesAtDunning ? 'Yes' : 'No' }}</span>
      <span>Default payment method</span><span>{{ label(p.defaultPaymentMethod) || '—' }}</span>
      <span>Payment terms</span><span>{{ p.paymentTermsDays !== null && p.paymentTermsDays !== undefined ? p.paymentTermsDays + ' days' : '—' }}</span>
      <span>Last payment</span><span>{{ p.lastPaymentDate ? (p.lastPaymentDate | date:'dd MMM yyyy') : '—' }}<ng-container *ngIf="p.averageDaysToPay"> · pays in {{ p.averageDaysToPay }} days on average</ng-container></span>
      <ng-container *ngIf="p.notes"><span>Notes</span><span>{{ p.notes }}</span></ng-container>
    </div>
    <div class="sf-kv" *ngIf="role === 'Vendor'" data-testid="profile-vendor">
      <span>Payment priority</span><span data-testid="pf-priority">{{ p.paymentPriority || 'Normal' }}</span>
      <span>Preferred payment method</span><span>{{ label(p.preferredPaymentMethod) || '—' }}</span>
      <span>Early-payment term</span><span>{{ p.earlyPaymentTermCode || '—' }}</span>
      <span>Withholding tax</span><span>{{ p.withholdingTaxApplicable ? 'Applies' : 'No' }}</span>
      <span>Payment terms</span><span>{{ p.paymentTermsDays !== null && p.paymentTermsDays !== undefined ? p.paymentTermsDays + ' days' : '—' }}</span>
      <span>Last payment</span><span>{{ p.lastPaymentDate ? (p.lastPaymentDate | date:'dd MMM yyyy') : '—' }}</span>
      <ng-container *ngIf="p.notes"><span>Notes</span><span>{{ p.notes }}</span></ng-container>
    </div>
    <div class="aa-actions" style="margin-top:.75rem">
      <p-button *ngIf="canEdit" label="Edit preferences" icon="pi pi-pencil" [text]="true" size="small" (onClick)="edit()" data-testid="pf-edit"></p-button>
      <a *ngIf="role === 'Customer' && canCredit" class="p-button p-button-text p-button-sm" [routerLink]="[creditRoute, partnerUuid]" data-testid="pf-credit-link">
        <i class="pi pi-shield"></i>&nbsp;Credit control</a>
      <a *ngIf="role === 'Vendor' && canVendorStatement" class="p-button p-button-text p-button-sm" [routerLink]="[vendorRoute, partnerUuid]" data-testid="pf-vendor-link">
        <i class="pi pi-file"></i>&nbsp;Statement &amp; reconciliation</a>
    </div>
  </ng-container>

  <ng-container *ngIf="editing && draft as d">
    <div class="sf-fields" data-testid="pf-form">
      <ng-container *ngIf="role === 'Customer'">
        <div class="sf-fld"><label for="pf-proc">Dunning procedure</label>
          <p-select inputId="pf-proc" [options]="procedureOptions" [(ngModel)]="d.dunningProcedureUuid" optionLabel="label" optionValue="value" appendTo="body" data-testid="pf-proc"></p-select></div>
        <div class="sf-fld"><label for="pf-dpm">Default payment method</label>
          <p-select inputId="pf-dpm" [options]="arMethods" [(ngModel)]="d.defaultPaymentMethod" optionLabel="label" optionValue="value" appendTo="body"></p-select></div>
        <div class="sf-fld"><label>&nbsp;</label><div class="aa-actions">
          <label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="d.blockSalesAtDunning"></p-checkbox> Block sales at dunning</label>
          <label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="d.excludeFromDunning" data-testid="pf-exclude"></p-checkbox> Exclude from dunning</label></div></div>
      </ng-container>
      <ng-container *ngIf="role === 'Vendor'">
        <div class="sf-fld"><label for="pf-prio">Payment priority</label>
          <p-select inputId="pf-prio" [options]="priorities" [(ngModel)]="d.paymentPriority" appendTo="body" data-testid="pf-prio"></p-select></div>
        <div class="sf-fld"><label for="pf-ppm">Preferred payment method</label>
          <p-select inputId="pf-ppm" [options]="apMethods" [(ngModel)]="d.preferredPaymentMethod" optionLabel="label" optionValue="value" appendTo="body"></p-select></div>
        <div class="sf-fld"><label for="pf-term">Early-payment term</label>
          <p-select inputId="pf-term" [options]="termOptions" [(ngModel)]="d.earlyPaymentTermUuid" optionLabel="label" optionValue="value" appendTo="body" data-testid="pf-term"></p-select></div>
        <div class="sf-fld"><label>&nbsp;</label><label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="d.withholdingTaxApplicable"></p-checkbox> Withholding tax applies</label></div>
      </ng-container>
    </div>
    <div class="sf-fld" style="margin-top:.5rem"><label for="pf-notes">Notes</label><input pInputText id="pf-notes" [(ngModel)]="d.notes" maxlength="1000" data-testid="pf-notes" /></div>
    <div class="gl-note er" *ngIf="saveError" style="margin-top:.5rem" data-testid="pf-save-error"><i class="pi pi-exclamation-triangle"></i><div>{{ saveError }}</div></div>
    <div class="aa-actions" style="margin-top:.75rem">
      <p-button label="Cancel" [text]="true" severity="secondary" size="small" (onClick)="editing = false"></p-button>
      <p-button label="Save" icon="pi pi-check" size="small" [disabled]="saving" [loading]="saving" (onClick)="save()" data-testid="pf-save"></p-button>
    </div>
  </ng-container>
</ng-container>`
})
export class PartnerFinancialProfileComponent implements OnChanges {
  private readonly api = inject(ArApService);
  private readonly auth = inject(AuthService);

  @Input() partnerUuid = '';
  @Input() role: PartnerRole = 'Customer';

  readonly creditRoute = CREDIT_ROUTE;
  readonly vendorRoute = VENDOR_STATEMENT_ROUTE;
  readonly priorities = PAYMENT_PRIORITIES;
  readonly arMethods = [{ label: 'None', value: null as string | null }, ...AR_PAYMENT_METHODS.map(m => ({ label: labelOf(m), value: m as string | null }))];
  readonly apMethods = [{ label: 'None', value: null as string | null }, ...AP_PAYMENT_METHODS.map(m => ({ label: labelOf(m), value: m as string | null }))];

  p: PartnerFinancialProfile | null = null;
  loading = false;
  error = '';
  editing = false;
  draft: ProfileDraft | null = null;
  procedureOptions: { label: string; value: string | null }[] = [{ label: 'Organization default', value: null }];
  termOptions: { label: string; value: string | null }[] = [{ label: 'None', value: null }];
  saving = false;
  saveError = '';

  get canRead(): boolean { return this.auth.hasAnyPermission(...profileReadCodes(this.role)); }
  get canEdit(): boolean { return this.auth.hasPermission(AR_AP_PERMS.ADMIN); }
  get canCredit(): boolean { return this.auth.hasAnyPermission(...CREDIT_READ); }
  get canVendorStatement(): boolean { return this.auth.hasAnyPermission(...VENDOR_STATEMENT_READ); }

  ngOnChanges(): void { if (this.partnerUuid && this.canRead) this.load(); }

  load(): void {
    this.loading = true;
    this.error = '';
    this.api.getProfile(this.partnerUuid, this.role).subscribe({
      next: p => { this.loading = false; this.p = p; },
      error: err => { this.loading = false; this.p = null; this.error = arApError(err, 'The financial profile could not be loaded.'); }
    });
  }

  edit(): void {
    const p = this.p;
    if (!p) return;
    this.draft = {
      dunningProcedureUuid: p.dunningProcedureUuid ?? null, blockSalesAtDunning: !!p.blockSalesAtDunning, excludeFromDunning: !!p.excludeFromDunning,
      defaultPaymentMethod: p.defaultPaymentMethod ?? null, paymentPriority: p.paymentPriority ?? 'Normal', preferredPaymentMethod: p.preferredPaymentMethod ?? null,
      withholdingTaxApplicable: !!p.withholdingTaxApplicable, earlyPaymentTermUuid: p.earlyPaymentTermUuid ?? null, notes: p.notes ?? ''
    };
    this.saveError = '';
    this.editing = true;
    if (this.role === 'Customer') {
      this.api.getProcedures().pipe(catchError(() => of([] as DunningProcedure[]))).subscribe(list => {
        this.procedureOptions = [{ label: 'Organization default', value: null },
          ...list.filter(x => x.isActive || x.uuid === p.dunningProcedureUuid).map(x => ({ label: `${x.code} — ${x.name}`, value: x.uuid as string | null }))];
      });
    } else {
      this.api.getEarlyPaymentTerms().pipe(catchError(() => of([] as EarlyPaymentTerm[]))).subscribe(list => {
        this.termOptions = [{ label: 'None', value: null },
          ...list.filter(x => x.isActive || x.uuid === p.earlyPaymentTermUuid).map(x => ({ label: `${x.code} — ${x.name}`, value: x.uuid as string | null }))];
      });
    }
  }

  save(): void {
    if (!this.draft || !this.p) return;
    this.saving = true;
    this.saveError = '';
    this.api.updateProfile(this.partnerUuid, this.role, profileChanges(this.p, this.draft, this.role)).subscribe({
      next: p => { this.saving = false; this.editing = false; if (p) this.p = p; else this.load(); },
      error: err => { this.saving = false; this.saveError = arApError(err, 'The profile could not be saved.'); }
    });
  }

  tone(t: string | null | undefined): string { return riskTone(t); }
  label(s: string | null | undefined): string { return labelOf(s); }
  amount(n: number | null | undefined): string { return money(n); }
}
