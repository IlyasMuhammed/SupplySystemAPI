import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { RadioButtonModule } from 'primeng/radiobutton';

import { CrmAccount, CrmContact, CrmService, LeadConversionResult, LeadDetail } from '../../../services/crm.service';
import { fromDateOnly, toDateOnly } from '../../../shared/date-only';
import { CrmLookupsService, Option } from '../shared/crm-lookups.service';

/**
 * A41 §15.4 / §8.3 — convert a qualified lead: create the account or link an existing one (possible duplicates first,
 * §8.4), create the contact or link one of that account's contacts (never another company's — BR-23), and the opportunity
 * it opens (topic, revenue, close date, owner).
 */
@Component({
  selector: 'app-lead-convert-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, CalendarModule, DialogModule, DropdownModule, InputNumberModule, InputTextModule, RadioButtonModule],
  template: `
<p-dialog [visible]="visible" (visibleChange)="close($event)" [modal]="true" [draggable]="false" [style]="{ width: '44rem' }"
          [breakpoints]="{ '760px': '96vw' }" [header]="'Convert lead ' + (lead?.leadNumber ?? '')" data-testid="convert-dialog">
  <ng-container *ngIf="visible && lead">
    <div class="sf-mu cv-sub">{{ lead.fullName }} — {{ lead.companyName }}</div>
    <div class="sf-pill er cv-error" *ngIf="error" role="alert" data-testid="convert-error">{{ error }}</div>

    <h3 class="cv-h">Account (customer)</h3>
    <div class="cv-radio"><p-radioButton name="acc" value="new" [(ngModel)]="accountMode" inputId="cv-acc-new" data-testid="account-new"></p-radioButton>
      <label for="cv-acc-new">Create a new account: <strong>{{ lead.companyName }}</strong></label></div>
    <div class="cv-radio"><p-radioButton name="acc" value="existing" [(ngModel)]="accountMode" inputId="cv-acc-ex" data-testid="account-existing"></p-radioButton>
      <label for="cv-acc-ex">Link to an existing account</label></div>
    <div class="cv-box" *ngIf="accountMode === 'existing' || matches.length">
      <div class="cv-warn" *ngIf="matches.length && !searched" data-testid="account-matches"><i class="pi pi-exclamation-triangle"></i> Possible matches found:</div>
      <div class="cv-search">
        <input pInputText [(ngModel)]="search" placeholder="Search accounts by name, code, phone or email" (keyup.enter)="runSearch()" data-testid="account-search" />
        <p-button icon="pi pi-search" [text]="true" (onClick)="runSearch()" ariaLabel="Search"></p-button>
      </div>
      <label class="cv-option" *ngFor="let a of matches" [class.on]="accountUuid === a.uuid" [attr.data-testid]="'account-' + a.code">
        <input type="radio" name="accpick" [value]="a.uuid" [(ngModel)]="accountUuid" (change)="pickAccount(a)" />
        <span><strong>{{ a.name }}</strong> <span class="sf-mono sf-mu">({{ a.code }})</span>
          <span class="sf-mu sf-sm" *ngIf="a.phone || a.email"><br>{{ a.phone }} {{ a.email }}</span>
          <span class="sf-pill in cv-tag" *ngIf="a.isCustomer">Customer</span><span class="sf-pill cv-tag" *ngIf="!a.isCustomer">Becomes a customer</span>
        </span>
      </label>
      <div class="sf-mu sf-sm" *ngIf="!matches.length">No account found.</div>
    </div>

    <h3 class="cv-h">Contact person</h3>
    <div class="cv-radio"><p-radioButton name="con" value="new" [(ngModel)]="contactMode" inputId="cv-con-new"></p-radioButton>
      <label for="cv-con-new">Create a new contact: <strong>{{ lead.fullName }}</strong><span *ngIf="lead.jobTitle" class="sf-mu"> — {{ lead.jobTitle }}</span></label></div>
    <div class="cv-radio"><p-radioButton name="con" value="existing" [(ngModel)]="contactMode" inputId="cv-con-ex"
                         [disabled]="accountMode !== 'existing' || !accountUuid" data-testid="contact-existing"></p-radioButton>
      <label for="cv-con-ex">Link to an existing contact of the chosen account</label></div>
    <div class="cv-box" *ngIf="contactMode === 'existing'">
      <label class="cv-option" *ngFor="let c of contacts" [class.on]="contactId === c.id" [attr.data-testid]="'contact-' + c.id">
        <input type="radio" name="conpick" [value]="c.id" [(ngModel)]="contactId" />
        <span><strong>{{ c.name }}</strong><span *ngIf="c.title" class="sf-mu"> — {{ c.title }}</span>
          <span class="sf-mu sf-sm" *ngIf="c.email || c.phone"><br>{{ c.email }} {{ c.phone }}</span></span>
      </label>
      <div class="sf-mu sf-sm" *ngIf="!contacts.length">This account has no contacts yet.</div>
    </div>

    <h3 class="cv-h">Opportunity</h3>
    <div class="sf-fields">
      <div class="sf-fld span-3"><label for="cv-topic">Topic</label>
        <input pInputText id="cv-topic" [(ngModel)]="topic" maxlength="200" data-testid="convert-topic" /></div>
      <div class="sf-fld"><label for="cv-rev">Revenue<span *ngIf="lead.currencyCode"> ({{ lead.currencyCode }})</span></label>
        <p-inputnumber inputId="cv-rev" [(ngModel)]="revenue" mode="decimal" [maxFractionDigits]="2" [min]="0" styleClass="w-full"></p-inputnumber></div>
      <div class="sf-fld"><label for="cv-close">Close by</label>
        <p-calendar inputId="cv-close" [(ngModel)]="closeDate" dateFormat="dd M yy" [showIcon]="true" appendTo="body" styleClass="w-full"></p-calendar></div>
      <div class="sf-fld" *ngIf="canAssign"><label for="cv-owner">Assign to</label>
        <p-dropdown inputId="cv-owner" [options]="userOptions" [(ngModel)]="ownerId" optionLabel="label" optionValue="value" [filter]="true"
                    filterBy="label" appendTo="body" styleClass="w-full"></p-dropdown></div>
    </div>
  </ng-container>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="close(false)"></p-button>
    <p-button label="Convert" icon="pi pi-sync" [loading]="saving" (onClick)="convert()" data-testid="convert-save"></p-button>
  </ng-template>
</p-dialog>`,
  styles: [`
    .cv-sub { margin-bottom: .5rem; }
    .cv-error { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .cv-h { font-size: .8rem; text-transform: uppercase; letter-spacing: .05em; color: var(--sms-text-muted); margin: 1rem 0 .4rem; }
    .cv-radio { display: flex; align-items: center; gap: .5rem; margin: .3rem 0; }
    .cv-box { border: 1px solid var(--sms-border); border-radius: var(--sms-radius-sm); padding: .5rem .6rem; margin: .35rem 0 .25rem 1.6rem; max-height: 16rem; overflow: auto; }
    .cv-warn { color: var(--sms-warn); font-size: .85rem; margin-bottom: .35rem; }
    .cv-search { display: flex; gap: .25rem; margin-bottom: .35rem; }
    .cv-search input { flex: 1; }
    .cv-option { display: flex; gap: .5rem; align-items: flex-start; padding: .35rem .4rem; border-radius: var(--sms-radius-sm); cursor: pointer; }
    .cv-option.on { background: var(--sms-primary-soft); }
    .cv-tag { margin-left: .35rem; }
    :host ::ng-deep .w-full { width: 100%; }
  `]
})
export class LeadConvertDialogComponent implements OnChanges {
  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();
  @Input() lead: LeadDetail | null = null;
  @Input() canAssign = false;
  @Output() converted = new EventEmitter<LeadConversionResult>();

  accountMode: 'new' | 'existing' = 'new';
  contactMode: 'new' | 'existing' = 'new';
  accountUuid: string | null = null;
  contactId: number | null = null;
  matches: CrmAccount[] = [];
  contacts: CrmContact[] = [];
  search = '';
  searched = false;
  topic = '';
  revenue: number | null = null;
  closeDate: Date | null = null;
  ownerId: number | null = null;
  userOptions: Option<number>[] = [];
  saving = false;
  error: string | null = null;

  constructor(private crm: CrmService, private lookups: CrmLookupsService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if ((changes['visible'] || changes['lead']) && this.visible && this.lead) this.reset();
  }

  private reset(): void {
    const l = this.lead!;
    this.error = null;
    this.accountMode = 'new';
    this.contactMode = 'new';
    this.accountUuid = null;
    this.contactId = null;
    this.contacts = [];
    this.search = '';
    this.searched = false;
    this.topic = l.topic;
    this.revenue = l.estimatedRevenue ?? null;
    this.closeDate = l.estimatedCloseDate ? fromDateOnly(l.estimatedCloseDate) : null;
    this.ownerId = l.assignedToUserId ?? null;
    this.crm.getAccountMatches(l.uuid).subscribe({ next: r => this.matches = r.result ?? [], error: () => this.matches = [] });
    if (this.canAssign) this.lookups.users().subscribe(u => this.userOptions = CrmLookupsService.userOptions(u));
  }

  runSearch(): void {
    if (!this.lead) return;
    this.accountMode = 'existing';
    this.searched = true;
    this.crm.getAccountMatches(this.lead.uuid, this.search.trim() || undefined).subscribe({ next: r => this.matches = r.result ?? [] });
  }

  pickAccount(a: CrmAccount): void {
    this.accountMode = 'existing';
    this.accountUuid = a.uuid;
    this.contactId = null;
    this.crm.getAccountContacts(a.uuid).subscribe({
      next: r => {
        this.contacts = r.result ?? [];
        // Offer the existing contact when one has the lead's email.
        const same = this.contacts.find(c => c.email && this.lead?.email && c.email.toLowerCase() === this.lead.email.toLowerCase());
        if (same) { this.contactMode = 'existing'; this.contactId = same.id; }
      },
      error: () => this.contacts = []
    });
  }

  close(visible: boolean): void {
    if (!visible) { this.visible = false; this.visibleChange.emit(false); }
  }

  convert(): void {
    if (!this.lead) return;
    this.error = null;
    if (this.accountMode === 'existing' && !this.accountUuid) { this.error = 'Choose the account to link.'; return; }
    if (this.contactMode === 'existing' && !this.contactId) { this.error = 'Choose the contact to link.'; return; }
    this.saving = true;
    this.crm.convertLead(this.lead.uuid, {
      existingAccountUuid: this.accountMode === 'existing' ? this.accountUuid : null,
      existingContactId: this.accountMode === 'existing' && this.contactMode === 'existing' ? this.contactId : null,
      topic: this.topic?.trim() || null,
      estimatedRevenue: this.revenue,
      estimatedCloseDate: this.closeDate ? toDateOnly(this.closeDate) : null,
      assignedToUserId: this.canAssign ? this.ownerId : null
    }).subscribe({
      next: r => { this.saving = false; this.converted.emit(r.result); this.close(false); },
      error: err => { this.saving = false; this.error = err?.error?.message || 'The lead could not be converted.'; }
    });
  }
}
