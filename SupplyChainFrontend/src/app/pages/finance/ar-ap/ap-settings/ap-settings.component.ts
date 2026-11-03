import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { AutoCompleteCompleteEvent, AutoCompleteModule } from 'primeng/autocomplete';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { DialogModule } from 'primeng/dialog';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';

import {
  AR_AP_PERMS, ArApService, CREDIT_CONTROL_MODES, EarlyPaymentTerm, EarlyPaymentTermSave, MATCHING_ROUTE, TERMS_READ, TOLERANCE_READ, ToleranceRule,
  ToleranceRuleSave
} from '../../../../services/ar-ap.service';
import { BusinessPartnerModel, BusinessPartnerService } from '../../../../services/business-partner.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { arApError, creditModeHelp, money, termProblem, termShort, toleranceProblem } from '../ar-ap.shared';

export function blankTolerance(): ToleranceRuleSave {
  return { name: '', vendorUuid: null, priceTolerancePercent: 2, priceToleranceAmount: 0, quantityTolerancePercent: 0, totalTolerancePercent: 0,
    totalToleranceAmount: 0, autoApproveWithin: false, isDefault: false, isActive: true };
}

export function blankTerm(): EarlyPaymentTermSave {
  return { code: '', name: '', discountPercent: 2, discountDays: 10, netDays: 30, isActive: true };
}

/**
 * A48 C9 / C15 — `/finance/ap/settings`: the 3-way match tolerance rules (org default + optional per vendor, D-7;
 * auto-approve within tolerance, BR-MT-005) and the early-payment terms ("2/10 Net 30", discount days < net days,
 * BR-EP-004). Read for AP viewers; create / edit need FINANCE_AR_AP_ADMIN.
 */
@Component({
  selector: 'app-ap-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, AutoCompleteModule, ButtonModule, CheckboxModule, DialogModule, InputNumberModule, InputTextModule,
    SelectModule, ToastModule, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Finance', 'AR & AP settings']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canEdit && canRules" label="New tolerance rule" icon="pi pi-plus" (onClick)="openRule(null)" data-testid="new-rule"></p-button>
    <p-button *ngIf="canEdit && canTerms" label="New payment term" icon="pi pi-plus" [text]="true" (onClick)="openTerm(null)" data-testid="new-term"></p-button>
    <span class="sf-end"><a *ngIf="canRules" class="p-button p-button-text" [routerLink]="matchingRoute"><i class="pi pi-check-square"></i>&nbsp;Invoice matching</a></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>AR &amp; AP settings</h1>
      <div class="sf-sub">Credit control, how far a supplier invoice may differ from its PO and goods receipt, and the early-payment discounts vendors offer</div></div></div>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

    <!-- §1.4 — the organization's credit control mode (D-5: Off by default) -->
    <section class="sf-card" data-testid="credit-mode">
      <div class="sf-ch"><h2>Credit control</h2></div>
      <div class="sf-fields">
        <div class="sf-fld"><label for="as-mode">Mode</label>
          <p-select inputId="as-mode" [options]="modes" [(ngModel)]="mode" (ngModelChange)="saveMode()" [disabled]="!canEdit || modeSaving" appendTo="body" data-testid="mode-select"></p-select></div>
        <div class="sf-fld" style="grid-column: span 2"><label>&nbsp;</label><span class="sf-mu sf-sm" data-testid="mode-help">{{ modeHelp }}</span></div>
      </div>
    </section>

    <section class="sf-card p0" data-testid="rules" *ngIf="canRules">
      <div class="sf-ch sf-pad"><h2>Tolerance rules <span class="sf-count">{{ rules.length }}</span></h2></div>
      <div class="gl-scroll">
        <table class="aa-table" style="min-width:52rem">
          <thead><tr><th>Name</th><th>Vendor</th><th class="gl-num">Price</th><th class="gl-num">Quantity</th><th class="gl-num">Total</th><th>Auto-approve</th><th>Status</th><th></th></tr></thead>
          <tbody>
            <tr *ngFor="let r of rules" [attr.data-testid]="'rule-' + r.uuid">
              <td>{{ r.name }}<span class="sf-pill in" *ngIf="r.isDefault" style="margin-left:.35rem">Default</span></td>
              <td>{{ r.vendorName || (r.vendorUuid ? 'A vendor' : 'Every vendor') }}</td>
              <td class="gl-num">{{ r.priceTolerancePercent }}% / {{ amount(r.priceToleranceAmount) }}</td>
              <td class="gl-num">{{ r.quantityTolerancePercent }}%</td>
              <td class="gl-num">{{ r.totalTolerancePercent }}% / {{ amount(r.totalToleranceAmount) }}</td>
              <td>{{ r.autoApproveWithin ? 'Yes' : 'No' }}</td>
              <td><span class="sf-pill" [ngClass]="r.isActive ? 'ok' : ''">{{ r.isActive ? 'Active' : 'Inactive' }}</span></td>
              <td><p-button *ngIf="canEdit" label="Edit" icon="pi pi-pencil" [text]="true" size="small" (onClick)="openRule(r)" [attr.data-testid]="'edit-rule-' + r.uuid"></p-button></td>
            </tr>
            <tr *ngIf="!rules.length"><td colspan="8" class="sf-empty">No tolerance rule: every difference is a mismatch.</td></tr>
          </tbody>
        </table>
      </div>
    </section>

    <section class="sf-card p0" data-testid="terms" *ngIf="canTerms">
      <div class="sf-ch sf-pad"><h2>Early-payment terms <span class="sf-count">{{ terms.length }}</span></h2></div>
      <div class="gl-scroll">
        <table class="aa-table" style="min-width:40rem">
          <thead><tr><th>Code</th><th>Name</th><th>Terms</th><th class="gl-num">Discount</th><th class="gl-num">Within</th><th class="gl-num">Net</th><th>Status</th><th></th></tr></thead>
          <tbody>
            <tr *ngFor="let t of terms" [attr.data-testid]="'term-' + t.code">
              <td class="sf-mono">{{ t.code }}</td><td>{{ t.name }}</td><td>{{ short(t) }}</td>
              <td class="gl-num">{{ t.discountPercent }}%</td><td class="gl-num">{{ t.discountDays }} days</td><td class="gl-num">{{ t.netDays }} days</td>
              <td><span class="sf-pill" [ngClass]="t.isActive ? 'ok' : ''">{{ t.isActive ? 'Active' : 'Inactive' }}</span></td>
              <td><p-button *ngIf="canEdit" label="Edit" icon="pi pi-pencil" [text]="true" size="small" (onClick)="openTerm(t)" [attr.data-testid]="'edit-term-' + t.code"></p-button></td>
            </tr>
            <tr *ngIf="!terms.length"><td colspan="8" class="sf-empty">No early-payment term yet.</td></tr>
          </tbody>
        </table>
      </div>
    </section>
  </div>
</sf-page>

<p-dialog [(visible)]="ruleOpen" [modal]="true" [header]="ruleUuid ? 'Edit tolerance rule' : 'New tolerance rule'" [style]="{ width: '36rem', maxWidth: '96vw' }" styleClass="sdlg">
  <div class="aa-dlg-body" *ngIf="rule as r">
    <div class="sf-fields">
      <div class="sf-fld"><label for="tr-name" class="sf-req">Name</label><input pInputText id="tr-name" [(ngModel)]="r.name" maxlength="100" data-testid="r-name" /></div>
      <div class="sf-fld"><label for="tr-vendor">Vendor</label>
        <p-autoComplete inputId="tr-vendor" [(ngModel)]="vendor" [suggestions]="vendorSuggestions" optionLabel="companyName" (completeMethod)="searchVendors($event)"
                        [minLength]="1" appendTo="body" placeholder="Every vendor" data-testid="r-vendor"></p-autoComplete></div>
      <div class="sf-fld"><label for="tr-pp">Price tolerance %</label><p-inputNumber inputId="tr-pp" [(ngModel)]="r.priceTolerancePercent" [min]="0" [max]="100" [maxFractionDigits]="2" data-testid="r-pp"></p-inputNumber></div>
      <div class="sf-fld"><label for="tr-pa">Price tolerance amount</label><p-inputNumber inputId="tr-pa" [(ngModel)]="r.priceToleranceAmount" [min]="0" [maxFractionDigits]="2"></p-inputNumber></div>
      <div class="sf-fld"><label for="tr-qp">Quantity tolerance %</label><p-inputNumber inputId="tr-qp" [(ngModel)]="r.quantityTolerancePercent" [min]="0" [max]="100" [maxFractionDigits]="2"></p-inputNumber></div>
      <div class="sf-fld"><label for="tr-tp">Total tolerance %</label><p-inputNumber inputId="tr-tp" [(ngModel)]="r.totalTolerancePercent" [min]="0" [max]="100" [maxFractionDigits]="2"></p-inputNumber></div>
      <div class="sf-fld"><label for="tr-ta">Total tolerance amount</label><p-inputNumber inputId="tr-ta" [(ngModel)]="r.totalToleranceAmount" [min]="0" [maxFractionDigits]="2"></p-inputNumber></div>
    </div>
    <div class="aa-actions">
      <label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="r.autoApproveWithin" data-testid="r-auto"></p-checkbox> Approve automatically within tolerance</label>
      <label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="r.isDefault"></p-checkbox> Default</label>
      <label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="r.isActive"></p-checkbox> Active</label>
    </div>
    <div class="gl-note er" *ngIf="formError" data-testid="rule-error"><i class="pi pi-exclamation-triangle"></i><div>{{ formError }}</div></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="ruleOpen = false"></p-button>
    <p-button label="Save" icon="pi pi-check" [disabled]="saving" [loading]="saving" (onClick)="saveRule()" data-testid="save-rule"></p-button>
  </ng-template>
</p-dialog>

<p-dialog [(visible)]="termOpen" [modal]="true" [header]="termUuid ? 'Edit payment term' : 'New payment term'" [style]="{ width: '32rem', maxWidth: '96vw' }" styleClass="sdlg">
  <div class="aa-dlg-body" *ngIf="term as t">
    <div class="sf-fields">
      <div class="sf-fld"><label for="ept-code" class="sf-req">Code</label><input pInputText id="ept-code" [(ngModel)]="t.code" maxlength="20" data-testid="t-code" /></div>
      <div class="sf-fld"><label for="ept-name" class="sf-req">Name</label><input pInputText id="ept-name" [(ngModel)]="t.name" maxlength="100" data-testid="t-name" /></div>
      <div class="sf-fld"><label for="ept-pct">Discount %</label><p-inputNumber inputId="ept-pct" [(ngModel)]="t.discountPercent" [min]="0" [max]="99.99" [maxFractionDigits]="2" data-testid="t-pct"></p-inputNumber></div>
      <div class="sf-fld"><label for="ept-dd">Discount within (days)</label><p-inputNumber inputId="ept-dd" [(ngModel)]="t.discountDays" [min]="0" [useGrouping]="false" data-testid="t-dd"></p-inputNumber></div>
      <div class="sf-fld"><label for="ept-nd">Net days</label><p-inputNumber inputId="ept-nd" [(ngModel)]="t.netDays" [min]="1" [useGrouping]="false" data-testid="t-nd"></p-inputNumber></div>
    </div>
    <label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="t.isActive"></p-checkbox> Active</label>
    <div class="sf-mu sf-sm">{{ short(t) }}</div>
    <div class="gl-note er" *ngIf="formError" data-testid="term-error"><i class="pi pi-exclamation-triangle"></i><div>{{ formError }}</div></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="termOpen = false"></p-button>
    <p-button label="Save" icon="pi pi-check" [disabled]="saving" [loading]="saving" (onClick)="saveTerm()" data-testid="save-term"></p-button>
  </ng-template>
</p-dialog>`
})
export class ApSettingsComponent implements OnInit {
  private readonly api = inject(ArApService);
  private readonly partners = inject(BusinessPartnerService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);

  readonly matchingRoute = MATCHING_ROUTE;

  rules: ToleranceRule[] = [];
  terms: EarlyPaymentTerm[] = [];
  loadError = '';

  ruleOpen = false;
  rule: ToleranceRuleSave | null = null;
  ruleUuid: string | null = null;
  vendor: BusinessPartnerModel | { uuid: string; companyName: string } | string | null = null;
  vendorSuggestions: BusinessPartnerModel[] = [];

  termOpen = false;
  term: EarlyPaymentTermSave | null = null;
  termUuid: string | null = null;

  formError = '';
  saving = false;

  readonly modes = CREDIT_CONTROL_MODES;
  mode = 'Off';
  modeSaving = false;

  get canEdit(): boolean { return this.auth.hasPermission(AR_AP_PERMS.ADMIN); }
  get canRules(): boolean { return this.auth.hasAnyPermission(...TOLERANCE_READ); }
  get canTerms(): boolean { return this.auth.hasAnyPermission(...TERMS_READ); }
  get modeHelp(): string { return creditModeHelp(this.mode); }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loadError = '';
    this.api.getSettings().subscribe({
      next: s => this.mode = s?.creditControlMode ?? 'Off',
      error: err => this.loadError = arApError(err, 'The credit control setting could not be loaded.')
    });
    if (this.canRules) this.api.getToleranceRules().subscribe({
      next: r => this.rules = [...r].sort((a, b) => Number(b.isDefault) - Number(a.isDefault) || a.name.localeCompare(b.name)),
      error: err => this.loadError = arApError(err, 'The tolerance rules could not be loaded.')
    });
    if (this.canTerms) this.api.getEarlyPaymentTerms().subscribe({
      next: t => this.terms = [...t].sort((a, b) => a.code.localeCompare(b.code)),
      error: err => this.loadError = arApError(err, 'The early-payment terms could not be loaded.')
    });
  }

  saveMode(): void {
    if (!this.canEdit) return;
    this.modeSaving = true;
    this.api.updateSettings({ creditControlMode: this.mode }).subscribe({
      next: s => { this.modeSaving = false; this.mode = s?.creditControlMode ?? this.mode; this.toast(`Credit control is now ${this.mode}.`); },
      error: err => {
        this.modeSaving = false;
        this.messages.add({ severity: 'error', summary: 'Error', detail: arApError(err, 'The mode could not be saved.'), life: 8000 });
        this.api.getSettings().pipe(catchError(() => of(null))).subscribe(s => { if (s) this.mode = s.creditControlMode; });
      }
    });
  }

  searchVendors(e: AutoCompleteCompleteEvent): void {
    this.partners.getPartners({ isVendor: true, active: true, search: e.query, pageSize: 20 }).pipe(catchError(() => of(null)))
      .subscribe(r => this.vendorSuggestions = r?.result?.data ?? []);
  }

  openRule(r: ToleranceRule | null): void {
    this.ruleUuid = r?.uuid ?? null;
    this.rule = r ? {
      name: r.name, vendorUuid: r.vendorUuid ?? null, priceTolerancePercent: r.priceTolerancePercent, priceToleranceAmount: r.priceToleranceAmount,
      quantityTolerancePercent: r.quantityTolerancePercent, totalTolerancePercent: r.totalTolerancePercent, totalToleranceAmount: r.totalToleranceAmount,
      autoApproveWithin: r.autoApproveWithin, isDefault: r.isDefault, isActive: r.isActive
    } : blankTolerance();
    this.vendor = r?.vendorUuid ? { uuid: r.vendorUuid, companyName: r.vendorName ?? '' } : null;
    this.formError = '';
    this.ruleOpen = true;
  }

  saveRule(): void {
    if (!this.rule) return;
    const vendorUuid = this.vendor && typeof this.vendor === 'object' ? this.vendor.uuid ?? null : null;
    const body: ToleranceRuleSave = { ...this.rule, name: this.rule.name.trim(), vendorUuid };
    this.formError = toleranceProblem(body) ?? '';
    if (this.formError) return;
    this.saving = true;
    const call = this.ruleUuid ? this.api.updateToleranceRule(this.ruleUuid, body) : this.api.createToleranceRule(body);
    call.subscribe({
      next: () => { this.saving = false; this.ruleOpen = false; this.toast(`${body.name} is saved.`); this.load(); },
      error: err => { this.saving = false; this.formError = arApError(err, 'The rule could not be saved.'); }
    });
  }

  openTerm(t: EarlyPaymentTerm | null): void {
    this.termUuid = t?.uuid ?? null;
    this.term = t ? { code: t.code, name: t.name, discountPercent: t.discountPercent, discountDays: t.discountDays, netDays: t.netDays, isActive: t.isActive } : blankTerm();
    this.formError = '';
    this.termOpen = true;
  }

  saveTerm(): void {
    if (!this.term) return;
    const body: EarlyPaymentTermSave = { ...this.term, code: this.term.code.trim().toUpperCase(), name: this.term.name.trim() };
    this.formError = termProblem(body) ?? '';
    if (this.formError) return;
    this.saving = true;
    const call = this.termUuid ? this.api.updateEarlyPaymentTerm(this.termUuid, body) : this.api.createEarlyPaymentTerm(body);
    call.subscribe({
      next: () => { this.saving = false; this.termOpen = false; this.toast(`${body.code} is saved.`); this.load(); },
      error: err => { this.saving = false; this.formError = arApError(err, 'The term could not be saved.'); }
    });
  }

  short(t: { discountPercent: number; discountDays: number; netDays: number }): string { return termShort(t); }
  amount(n: number | null | undefined): string { return money(n); }

  private toast(detail: string): void { this.messages.add({ severity: 'success', summary: 'Done', detail, life: 3000 }); }
}
