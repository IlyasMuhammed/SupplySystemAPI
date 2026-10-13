import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { CheckboxModule } from 'primeng/checkbox';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';

import { CRM_ROUTE, CrmService, LeadDetail, LeadDuplicateResult, LeadUpsert, crmLabel } from '../../../services/crm.service';
import { fromDateOnly, toDateOnly } from '../../../shared/date-only';
import { CrmLookupsService, Option } from '../shared/crm-lookups.service';

/**
 * A41 §15.1 [+ New Lead] / edit — a lead's contact, company, address, source, BANT qualification, rating and deal estimate.
 * "Individual (no company)" leaves the company blank so the server uses the person's name (BR-03). While creating, the
 * email / mobile / company are checked for duplicates (advisory, BR-09). Assignment and team are set here only on create
 * (later through Assign).
 */
@Component({
  selector: 'app-lead-form-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, CalendarModule, CheckboxModule, DialogModule, DropdownModule,
    InputNumberModule, InputTextModule, TextareaModule],
  template: `
<p-dialog [visible]="visible" (visibleChange)="close($event)" [modal]="true" [draggable]="false" [style]="{ width: '60rem' }"
          [breakpoints]="{ '900px': '96vw' }" [header]="lead ? 'Edit lead ' + lead.leadNumber : 'New lead'" data-testid="lead-dialog">
  <ng-container *ngIf="visible">
    <div class="sf-pill er lf-error" *ngIf="error" role="alert" data-testid="lead-error">{{ error }}</div>

    <div class="lf-dups" *ngIf="duplicates && (duplicates.leads.length || duplicates.accounts.length)" data-testid="lead-duplicates">
      <i class="pi pi-exclamation-triangle"></i>
      <div>
        <strong>Possible duplicates</strong>
        <div *ngFor="let d of duplicates.leads">
          <a [routerLink]="[route, 'leads', d.lead.uuid]" target="_blank" class="sf-mono">{{ d.lead.leadNumber }}</a>
          {{ d.lead.fullName }} — {{ d.lead.companyName }} <span class="sf-mu">({{ matched(d.matchedOn) }}, {{ label(d.lead.status) }})</span>
        </div>
        <div *ngFor="let a of duplicates.accounts"><span class="sf-mono">{{ a.code }}</span> {{ a.name }} <span class="sf-mu">(existing {{ a.isCustomer ? 'customer' : 'partner' }})</span></div>
      </div>
    </div>

    <h3 class="lf-h">Topic</h3>
    <div class="sf-fields">
      <div class="sf-fld span-3">
        <label for="lf-topic" class="sf-req">Topic</label>
        <input pInputText id="lf-topic" [(ngModel)]="f.topic" maxlength="200" placeholder="e.g. AC + ovens inquiry from Facebook" data-testid="lead-topic" />
      </div>
      <div class="sf-fld span-3">
        <label for="lf-desc">Description</label>
        <textarea pTextarea id="lf-desc" [(ngModel)]="f.description" rows="2" maxlength="4000"></textarea>
      </div>
    </div>

    <h3 class="lf-h">Contact</h3>
    <div class="sf-fields">
      <div class="sf-fld">
        <label for="lf-sal">Salutation</label>
        <p-dropdown inputId="lf-sal" [options]="salutations" [(ngModel)]="f.salutation" [editable]="true" [showClear]="true" appendTo="body" styleClass="w-full"></p-dropdown>
      </div>
      <div class="sf-fld">
        <label for="lf-first" class="sf-req">First name</label>
        <input pInputText id="lf-first" [(ngModel)]="f.firstName" maxlength="100" data-testid="lead-first" />
      </div>
      <div class="sf-fld">
        <label for="lf-last" class="sf-req">Last name</label>
        <input pInputText id="lf-last" [(ngModel)]="f.lastName" maxlength="100" data-testid="lead-last" />
      </div>
      <div class="sf-fld">
        <label for="lf-company" [class.sf-req]="!individual">Company</label>
        <input pInputText id="lf-company" [(ngModel)]="f.companyName" maxlength="200" [disabled]="individual" (blur)="checkDuplicates()"
               [placeholder]="individual ? 'The person\\'s name is used' : ''" data-testid="lead-company" />
        <div class="lf-check"><p-checkbox [(ngModel)]="individual" [binary]="true" inputId="lf-ind" (onChange)="onIndividual()" data-testid="lead-individual"></p-checkbox>
          <label for="lf-ind">Individual (no company)</label></div>
      </div>
      <div class="sf-fld">
        <label for="lf-title">Job title</label>
        <input pInputText id="lf-title" [(ngModel)]="f.jobTitle" maxlength="100" />
      </div>
      <div class="sf-fld">
        <label for="lf-email">Email</label>
        <input pInputText id="lf-email" type="email" [(ngModel)]="f.email" maxlength="200" (blur)="checkDuplicates()" data-testid="lead-email" />
      </div>
      <div class="sf-fld">
        <label for="lf-phone">Phone</label>
        <input pInputText id="lf-phone" [(ngModel)]="f.phone" maxlength="30" />
      </div>
      <div class="sf-fld">
        <label for="lf-mobile">Mobile</label>
        <input pInputText id="lf-mobile" [(ngModel)]="f.mobilePhone" maxlength="30" (blur)="checkDuplicates()" data-testid="lead-mobile" />
      </div>
      <div class="sf-fld">
        <label for="lf-web">Website</label>
        <input pInputText id="lf-web" [(ngModel)]="f.website" maxlength="500" />
      </div>
    </div>

    <h3 class="lf-h">Address</h3>
    <div class="sf-fields">
      <div class="sf-fld span-2"><label for="lf-a1">Address</label>
        <input pInputText id="lf-a1" [(ngModel)]="f.addressLine1" maxlength="200" placeholder="Line 1" />
        <input pInputText [(ngModel)]="f.addressLine2" maxlength="200" placeholder="Line 2" class="lf-mt" /></div>
      <div class="sf-fld"><label for="lf-city">City</label><input pInputText id="lf-city" [(ngModel)]="f.city" maxlength="100" /></div>
      <div class="sf-fld"><label for="lf-state">State / province</label><input pInputText id="lf-state" [(ngModel)]="f.stateProvince" maxlength="100" /></div>
      <div class="sf-fld"><label for="lf-postal">Postal code</label><input pInputText id="lf-postal" [(ngModel)]="f.postalCode" maxlength="20" /></div>
      <div class="sf-fld"><label for="lf-country">Country</label><input pInputText id="lf-country" [(ngModel)]="f.country" maxlength="100" /></div>
    </div>

    <h3 class="lf-h">Source</h3>
    <div class="sf-fields">
      <div class="sf-fld">
        <label for="lf-source">Lead source</label>
        <p-dropdown inputId="lf-source" [options]="sourceOptions" [(ngModel)]="f.leadSourceUuid" optionLabel="label" optionValue="value"
                    [showClear]="true" [filter]="true" filterBy="label" appendTo="body" styleClass="w-full" data-testid="lead-source"></p-dropdown>
      </div>
      <div class="sf-fld" *ngIf="campaignOptions.length || f.campaignUuid">
        <label for="lf-campaign">Campaign</label>
        <p-dropdown inputId="lf-campaign" [options]="campaignOptions" [(ngModel)]="f.campaignUuid" optionLabel="label" optionValue="value"
                    [showClear]="true" [filter]="true" filterBy="label" appendTo="body" styleClass="w-full" data-testid="lead-campaign"></p-dropdown>
      </div>
      <div class="sf-fld"><label for="lf-medium">Medium</label>
        <input pInputText id="lf-medium" [(ngModel)]="f.sourceMedium" maxlength="50" placeholder="social, email, cpc…" /></div>
      <div class="sf-fld"><label for="lf-ref">Reference</label>
        <input pInputText id="lf-ref" [(ngModel)]="f.sourceReference" maxlength="200" placeholder="Ad id, form id, referrer…" /></div>
    </div>

    <h3 class="lf-h">Qualification (BANT)</h3>
    <div class="sf-fields">
      <div class="sf-fld">
        <label for="lf-budget">Budget</label>
        <p-inputnumber inputId="lf-budget" [(ngModel)]="f.budgetAmount" mode="decimal" [minFractionDigits]="0" [maxFractionDigits]="2" [min]="0" styleClass="w-full"></p-inputnumber>
      </div>
      <div class="sf-fld">
        <label for="lf-budget-cur">Budget currency</label>
        <p-dropdown inputId="lf-budget-cur" [options]="currencyOptions" [(ngModel)]="f.budgetCurrencyCode" [showClear]="true" appendTo="body" styleClass="w-full"></p-dropdown>
      </div>
      <div class="sf-fld">
        <label for="lf-tf">Purchase timeframe</label>
        <p-dropdown inputId="lf-tf" [options]="timeframeOptions" [(ngModel)]="f.purchaseTimeframe" optionLabel="label" optionValue="value" [showClear]="true" appendTo="body" styleClass="w-full"></p-dropdown>
      </div>
      <div class="sf-fld span-3 lf-checks">
        <span class="lf-check"><p-checkbox [(ngModel)]="f.budgetConfirmed" [binary]="true" inputId="lf-b"></p-checkbox><label for="lf-b">Budget confirmed</label></span>
        <span class="lf-check"><p-checkbox [(ngModel)]="f.authorityConfirmed" [binary]="true" inputId="lf-a"></p-checkbox><label for="lf-a">Decision maker identified</label></span>
        <span class="lf-check"><p-checkbox [(ngModel)]="f.needIdentified" [binary]="true" inputId="lf-n"></p-checkbox><label for="lf-n">Need identified</label></span>
      </div>
    </div>

    <h3 class="lf-h">Rating &amp; deal estimate</h3>
    <div class="sf-fields">
      <div class="sf-fld"><label for="lf-rating">Rating</label>
        <p-dropdown inputId="lf-rating" [options]="ratingOptions" [(ngModel)]="f.rating" optionLabel="label" optionValue="value" appendTo="body" styleClass="w-full" data-testid="lead-rating"></p-dropdown></div>
      <div class="sf-fld"><label for="lf-priority">Priority</label>
        <p-dropdown inputId="lf-priority" [options]="priorityOptions" [(ngModel)]="f.priority" optionLabel="label" optionValue="value" appendTo="body" styleClass="w-full"></p-dropdown></div>
      <div class="sf-fld"><label for="lf-rev">Estimated revenue</label>
        <p-inputnumber inputId="lf-rev" [(ngModel)]="f.estimatedRevenue" mode="decimal" [minFractionDigits]="0" [maxFractionDigits]="2" [min]="0" styleClass="w-full" data-testid="lead-revenue"></p-inputnumber></div>
      <div class="sf-fld"><label for="lf-cur">Currency</label>
        <p-dropdown inputId="lf-cur" [options]="currencyOptions" [(ngModel)]="f.currencyCode" [showClear]="true" appendTo="body" styleClass="w-full" data-testid="lead-currency"></p-dropdown></div>
      <div class="sf-fld"><label for="lf-close">Expected close</label>
        <p-calendar inputId="lf-close" [(ngModel)]="closeDate" dateFormat="dd M yy" [showIcon]="true" [showButtonBar]="true" appendTo="body" styleClass="w-full"></p-calendar></div>
    </div>

    <ng-container *ngIf="!lead">
      <h3 class="lf-h">Ownership</h3>
      <div class="sf-fields">
        <div class="sf-fld" *ngIf="canAssign">
          <label for="lf-owner">Assign to</label>
          <p-dropdown inputId="lf-owner" [options]="userOptions" [(ngModel)]="f.assignedToUserId" optionLabel="label" optionValue="value"
                      [filter]="true" filterBy="label" [showClear]="true" placeholder="Me" appendTo="body" styleClass="w-full" data-testid="lead-owner"></p-dropdown>
        </div>
        <div class="sf-fld">
          <label for="lf-team">Sales team</label>
          <p-dropdown inputId="lf-team" [options]="teamOptions" [(ngModel)]="f.salesTeamUuid" optionLabel="label" optionValue="value"
                      [showClear]="true" placeholder="No team" appendTo="body" styleClass="w-full"></p-dropdown>
        </div>
      </div>
    </ng-container>
  </ng-container>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="close(false)"></p-button>
    <p-button [label]="lead ? 'Save' : 'Create lead'" icon="pi pi-check" [loading]="saving" (onClick)="save()" data-testid="lead-save"></p-button>
  </ng-template>
</p-dialog>`,
  styles: [`
    .lf-error { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .lf-h { font-size: .8rem; text-transform: uppercase; letter-spacing: .05em; color: var(--sms-text-muted); margin: 1rem 0 .4rem; }
    .lf-mt { margin-top: .4rem; }
    .lf-check { display: inline-flex; align-items: center; gap: .4rem; margin-right: 1.25rem; margin-top: .35rem; }
    .lf-checks { flex-direction: row !important; flex-wrap: wrap; }
    .lf-dups { display: flex; gap: .6rem; padding: .6rem .75rem; background: var(--sms-warn-soft); border-radius: var(--sms-radius-sm); margin-bottom: .5rem; }
    .lf-dups .pi { color: var(--sms-warn); margin-top: .2rem; }
    :host ::ng-deep .w-full { width: 100%; }
  `]
})
export class LeadFormDialogComponent implements OnChanges {
  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();
  /** null = a new lead. */
  @Input() lead: LeadDetail | null = null;
  /** CRM_ASSIGN_LEAD — may create the lead for someone else. */
  @Input() canAssign = false;
  @Output() saved = new EventEmitter<LeadDetail>();

  readonly route = CRM_ROUTE;
  readonly salutations = ['Mr', 'Mrs', 'Ms', 'Dr', 'Eng', 'Prof'];
  label = crmLabel;

  f: LeadUpsert = {};
  individual = false;
  closeDate: Date | null = null;
  saving = false;
  error: string | null = null;
  duplicates: LeadDuplicateResult | null = null;

  sourceOptions: Option[] = [];
  campaignOptions: Option[] = [];
  teamOptions: Option[] = [];
  userOptions: Option<number>[] = [];
  currencyOptions: string[] = [];
  ratingOptions: Option[] = [];
  priorityOptions: Option[] = [];
  timeframeOptions: Option[] = [];

  private lastCheck = '';

  constructor(private crm: CrmService, private lookups: CrmLookupsService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if ((changes['visible'] || changes['lead']) && this.visible) this.reset();
  }

  private reset(): void {
    this.error = null;
    this.duplicates = null;
    this.lastCheck = '';
    const l = this.lead;
    this.f = l ? {
      topic: l.topic, description: l.description, salutation: l.salutation, firstName: l.firstName, lastName: l.lastName,
      jobTitle: l.jobTitle, email: l.email, phone: l.phone, mobilePhone: l.mobilePhone, companyName: l.companyName, website: l.website,
      addressLine1: l.addressLine1, addressLine2: l.addressLine2, city: l.city, stateProvince: l.stateProvince, postalCode: l.postalCode,
      country: l.country, leadSourceUuid: l.leadSourceUuid, campaignUuid: l.campaignUuid, sourceMedium: l.sourceMedium,
      sourceReference: l.sourceReference, externalSourceSystem: l.externalSourceSystem, externalSourceId: l.externalSourceId,
      budgetAmount: l.budgetAmount, budgetCurrencyCode: l.budgetCurrencyCode, budgetConfirmed: l.budgetConfirmed,
      authorityConfirmed: l.authorityConfirmed, needIdentified: l.needIdentified, purchaseTimeframe: l.purchaseTimeframe,
      rating: l.rating, priority: l.priority, estimatedRevenue: l.estimatedRevenue, currencyCode: l.currencyCode
    } : { rating: 'WARM', priority: 'NORMAL', budgetConfirmed: false, authorityConfirmed: false, needIdentified: false };
    this.individual = !!l && l.companyName === l.fullName;
    this.closeDate = l?.estimatedCloseDate ? fromDateOnly(l.estimatedCloseDate) : null;

    this.lookups.meta().subscribe(m => {
      this.ratingOptions = CrmLookupsService.options(m?.ratings);
      this.priorityOptions = CrmLookupsService.options(m?.priorities);
      this.timeframeOptions = CrmLookupsService.options(m?.purchaseTimeframes);
    });
    this.lookups.sources().subscribe(s => this.sourceOptions = s.map(x => ({ value: x.uuid, label: x.name })));
    this.lookups.campaigns().subscribe(c => this.campaignOptions = c.map(x => ({ value: x.uuid, label: `${x.name} (${x.code})` })));
    this.lookups.teams().subscribe(t => this.teamOptions = t.map(x => ({ value: x.uuid, label: x.name })));
    this.lookups.users().subscribe(u => this.userOptions = CrmLookupsService.userOptions(u));
    this.lookups.currencies().subscribe(c => {
      this.currencyOptions = c.codes;
      if (!this.lead && !this.f.currencyCode) this.f.currencyCode = c.base;
    });
  }

  onIndividual(): void { if (this.individual) this.f.companyName = null; }

  matched(on: string[]): string { return on.map(o => o.toLowerCase()).join(' + ') + ' match'; }

  /** BR-09 — advisory only, while creating. */
  checkDuplicates(): void {
    if (this.lead) return;
    const key = [this.f.email, this.f.mobilePhone, this.individual ? '' : this.f.companyName].map(v => (v ?? '').trim().toLowerCase()).join('|');
    if (key === this.lastCheck || key === '||') return;
    this.lastCheck = key;
    this.crm.checkLeadDuplicates({ email: this.f.email, phone: this.f.mobilePhone || this.f.phone, companyName: this.individual ? null : this.f.companyName })
      .subscribe({ next: r => this.duplicates = r.result, error: () => this.duplicates = null });
  }

  close(visible: boolean): void {
    if (!visible) { this.visible = false; this.visibleChange.emit(false); }
  }

  save(): void {
    this.error = null;
    if (!this.f.topic?.trim() || !this.f.firstName?.trim() || !this.f.lastName?.trim()) {
      this.error = 'Topic, first name and last name are required.'; return;
    }
    if (!this.individual && !this.f.companyName?.trim()) { this.error = 'Give the company, or tick "Individual (no company)".'; return; }
    const req: LeadUpsert = {
      ...this.f,
      companyName: this.individual ? null : this.f.companyName,
      estimatedCloseDate: this.closeDate ? toDateOnly(this.closeDate) : null
    };
    this.saving = true;
    (this.lead ? this.crm.updateLead(this.lead.uuid, req) : this.crm.createLead(req)).subscribe({
      next: r => { this.saving = false; this.saved.emit(r.result); this.close(false); },
      error: err => { this.saving = false; this.error = err?.error?.message || 'The lead could not be saved.'; }
    });
  }
}
