import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { AutoCompleteCompleteEvent, AutoCompleteModule } from 'primeng/autocomplete';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { DialogModule } from 'primeng/dialog';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';

import {
  AR_AP_PERMS, AR_ROUTE, ArApService, BatchStatementResult, StatementLogEntry, StatementQuery, StatementResult, StatementType
} from '../../../../services/ar-ap.service';
import { BusinessPartnerModel, BusinessPartnerService } from '../../../../services/business-partner.service';
import { saveDownload } from '../../../../services/import.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { StatementViewComponent } from '../statement-view/statement-view.component';
import { addDays, arApError, arApErrorAsync, labelOf, money, today } from '../ar-ap.shared';

export const STATEMENT_TYPES: { label: string; value: StatementType }[] = [
  { label: 'Activity (every transaction)', value: 'Activity' }, { label: 'Open items (unpaid only)', value: 'OpenItems' }
];

export function statementPeriodProblem(from: string, to: string): string | null {
  if (!from || !to) return 'Choose the period.';
  if (from > to) return 'The period must start on or before its end.';
  return null;
}

/**
 * A48 C4, tasks 552 / 553 — `/finance/ar/statements[?customer=]`: generate a customer's Activity or Open-items statement
 * on screen (per currency, D-13), download it as PDF (rendered on request, D-11), email it to the customer, and the
 * batch run for every customer over a minimum balance (BR-ST-005). Sending needs AR_STATEMENT_SEND.
 */
@Component({
  selector: 'app-customer-statement',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, AutoCompleteModule, ButtonModule, CheckboxModule, DialogModule, InputNumberModule, InputTextModule,
    SelectModule, TextareaModule, ToastModule, StatementViewComponent, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Finance', 'Receivables', 'Statements']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canView" label="Generate" icon="pi pi-play" [disabled]="!customerUuid || generating" [loading]="generating" (onClick)="generate()" data-testid="generate"></p-button>
    <p-button *ngIf="canView" label="Download PDF" icon="pi pi-file-pdf" [text]="true" [disabled]="!customerUuid || downloading" [loading]="downloading" (onClick)="downloadPdf()" data-testid="pdf"></p-button>
    <p-button *ngIf="canSend" label="Email" icon="pi pi-envelope" [text]="true" [disabled]="!customerUuid" (onClick)="openEmail()" data-testid="email"></p-button>
    <span class="sf-end"><a class="p-button p-button-text" [routerLink]="arRoute"><i class="pi pi-arrow-left"></i>&nbsp;Back to AR</a></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Customer statements</h1><div class="sf-sub">A statement of account for one customer, or a batch for every customer with a balance</div></div></div>

    <!-- Task 552 — the generation form -->
    <section class="sf-card">
      <div class="sf-fields">
        <div class="sf-fld"><label for="st-cust" class="sf-req">Customer</label>
          <p-autoComplete inputId="st-cust" [(ngModel)]="customer" [suggestions]="suggestions" optionLabel="companyName" (completeMethod)="search($event)"
                          (onSelect)="onCustomer()" (onClear)="onCustomer()" [minLength]="1" appendTo="body" placeholder="Search customers…" data-testid="customer"></p-autoComplete></div>
        <div class="sf-fld"><label for="st-type">Type</label>
          <p-select inputId="st-type" [options]="types" [(ngModel)]="type" optionLabel="label" optionValue="value" appendTo="body" data-testid="type"></p-select></div>
        <div class="sf-fld"><label for="st-from" class="sf-req">From</label><input pInputText id="st-from" type="date" [(ngModel)]="from" data-testid="from" /></div>
        <div class="sf-fld"><label for="st-to" class="sf-req">To</label><input pInputText id="st-to" type="date" [(ngModel)]="to" data-testid="to" /></div>
        <div class="sf-fld"><label for="st-ccy">Currency</label>
          <input pInputText id="st-ccy" [(ngModel)]="currencyCode" maxlength="3" placeholder="All currencies" data-testid="currency" /></div>
      </div>
    </section>
    <div class="gl-note wn" *ngIf="formError" data-testid="form-error"><i class="pi pi-info-circle"></i><div>{{ formError }}</div></div>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

    <ng-container *ngIf="statement">
      <div class="sf-lr-head"><div><h2 style="margin:0" data-testid="statement-title">{{ statement.partnerName }} — {{ label(statement.type) }}</h2>
        <div class="sf-sub">{{ statement.fromDate | date:'dd MMM yyyy' }} – {{ statement.toDate | date:'dd MMM yyyy' }}</div></div></div>
      <app-statement-view [statement]="statement"></app-statement-view>
    </ng-container>

    <!-- §5 statement_log — what was generated / sent for this customer -->
    <section class="sf-card p0" *ngIf="customerUuid && log.length" data-testid="log">
      <div class="sf-ch sf-pad"><h2>Recent statements</h2></div>
      <div class="gl-scroll"><table class="aa-table" style="min-width:40rem"><thead><tr><th>Generated</th><th>Type</th><th>Period</th><th class="gl-num">Closing</th><th>Email</th></tr></thead>
        <tbody><tr *ngFor="let l of log">
          <td>{{ l.generatedAt | date:'dd MMM yyyy, HH:mm' }}<span class="sf-mu sf-sm" *ngIf="l.generatedByName"> · {{ l.generatedByName }}</span></td>
          <td>{{ label(l.statementType) }}<span class="sf-mu sf-sm" *ngIf="l.format"> · {{ l.format }}</span></td>
          <td>{{ l.fromDate | date:'dd MMM' }} – {{ l.toDate | date:'dd MMM yyyy' }}</td>
          <td class="gl-num">{{ amount(l.closingBalance) }} <span class="sf-mu sf-sm">{{ l.currencyCode || '' }}</span></td>
          <td>{{ label(l.emailStatus) }}<span class="sf-mu sf-sm" *ngIf="l.emailedTo"> · {{ l.emailedTo }}</span><span class="aa-bad sf-sm" *ngIf="l.emailError"> {{ l.emailError }}</span></td>
        </tr></tbody></table></div>
    </section>

    <!-- BR-ST-005 — batch -->
    <section class="sf-card" *ngIf="canSend" data-testid="batch">
      <div class="sf-ch"><h2>Batch statements</h2><span class="sf-end sf-mu sf-sm">every customer with a balance, the period and type above</span></div>
      <div class="sf-fields">
        <div class="sf-fld"><label for="st-min">Minimum balance</label>
          <p-inputNumber inputId="st-min" [(ngModel)]="minimumBalance" mode="decimal" [minFractionDigits]="0" [maxFractionDigits]="2" [min]="0" data-testid="min-balance"></p-inputNumber></div>
        <div class="sf-fld"><label>&nbsp;</label><label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="batchEmail" data-testid="batch-email"></p-checkbox> Email each customer</label></div>
        <div class="sf-fld"><label>&nbsp;</label><p-button label="Run batch" icon="pi pi-send" [disabled]="batching" [loading]="batching" (onClick)="runBatch()" data-testid="run-batch"></p-button></div>
      </div>
      <div *ngIf="batch" class="gl-note ok" style="margin-top:.75rem" data-testid="batch-result"><i class="pi pi-check"></i>
        <div>{{ batch.considered }} considered · {{ batch.generated }} generated · {{ batch.emailed }} emailed · {{ batch.skipped }} skipped<ng-container
          *ngIf="batch.noEmail"> · {{ batch.noEmail }} without an email</ng-container><ng-container *ngIf="batch.failed"> · {{ batch.failed }} failed</ng-container></div></div>
      <div class="gl-scroll" *ngIf="batch?.items?.length">
        <table class="aa-table" style="min-width:32rem;margin-top:.5rem">
          <thead><tr><th>Customer</th><th>Result</th><th class="gl-num">Closing</th><th></th></tr></thead>
          <tbody><tr *ngFor="let i of batch!.items"><td>{{ i.partnerName || '' }}</td><td>{{ label(i.outcome) }}<span class="sf-mu sf-sm" *ngIf="i.emailedTo"> · {{ i.emailedTo }}</span></td>
            <td class="gl-num">{{ amount(i.closingBalanceBase) }}</td><td class="aa-bad sf-sm">{{ i.error || '' }}</td></tr></tbody>
        </table>
      </div>
    </section>
  </div>
</sf-page>

<p-dialog [(visible)]="emailOpen" [modal]="true" header="Email statement" [style]="{ width: '30rem', maxWidth: '96vw' }" styleClass="sdlg">
  <div class="aa-dlg-body">
    <div class="sf-fld"><label for="st-to-addr">To</label>
      <input pInputText id="st-to-addr" type="email" [(ngModel)]="emailTo" placeholder="The customer's email on file" data-testid="email-to" /></div>
    <div class="sf-fld"><label for="st-subj">Subject</label><input pInputText id="st-subj" [(ngModel)]="emailSubject" maxlength="200" placeholder="Default subject" data-testid="email-subject" /></div>
    <div class="sf-fld"><label for="st-msg">Message</label><textarea pTextarea id="st-msg" rows="3" [(ngModel)]="emailMessage" data-testid="email-message"></textarea></div>
    <div class="gl-note er" *ngIf="emailError" data-testid="email-error"><i class="pi pi-exclamation-triangle"></i><div>{{ emailError }}</div></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="emailOpen = false"></p-button>
    <p-button label="Send" icon="pi pi-send" [disabled]="sending" [loading]="sending" (onClick)="sendEmail()" data-testid="send"></p-button>
  </ng-template>
</p-dialog>`
})
export class CustomerStatementComponent implements OnInit {
  private readonly api = inject(ArApService);
  private readonly partners = inject(BusinessPartnerService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);
  private readonly route = inject(ActivatedRoute, { optional: true });

  readonly arRoute = AR_ROUTE;
  readonly types = STATEMENT_TYPES;

  customer: BusinessPartnerModel | string | null = null;
  suggestions: BusinessPartnerModel[] = [];
  /** A ?customer= link names the customer before the picker has a model. */
  private linkedUuid: string | null = null;
  type: StatementType = 'Activity';
  from = addDays(today(), -90);
  to = today();
  currencyCode = '';
  formError = '';
  loadError = '';
  statement: StatementResult | null = null;
  log: StatementLogEntry[] = [];
  generating = false;
  downloading = false;

  emailOpen = false;
  emailTo = '';
  emailSubject = '';
  emailMessage = '';
  emailError = '';
  sending = false;

  minimumBalance: number | null = 0;
  batchEmail = false;
  batching = false;
  batch: BatchStatementResult | null = null;

  /** §5 — view / PDF need AR_VIEW; email and batch AR_STATEMENT_SEND. */
  get canView(): boolean { return this.auth.hasPermission(AR_AP_PERMS.AR_VIEW); }
  get canSend(): boolean { return this.auth.hasPermission(AR_AP_PERMS.AR_STATEMENT_SEND); }
  get customerUuid(): string | null {
    return this.customer && typeof this.customer === 'object' ? this.customer.uuid ?? null : this.linkedUuid;
  }

  ngOnInit(): void {
    const linked = this.route?.snapshot?.queryParamMap?.get('customer');
    if (linked) {
      this.linkedUuid = linked;
      this.partners.getPartnerById(linked).pipe(catchError(() => of(null))).subscribe(r => { if (r?.result) this.customer = r.result; });
      if (this.canView) this.generate();
    }
  }

  search(e: AutoCompleteCompleteEvent): void {
    this.partners.getPartners({ isCustomer: true, active: true, search: e.query, pageSize: 20 }).pipe(catchError(() => of(null)))
      .subscribe(r => this.suggestions = r?.result?.data ?? []);
  }

  onCustomer(): void { this.linkedUuid = null; this.statement = null; this.loadError = ''; this.log = []; }

  private query(): StatementQuery | null {
    this.formError = (!this.customerUuid ? 'Choose the customer.' : statementPeriodProblem(this.from, this.to)) ?? '';
    const ccy = this.currencyCode.trim().toUpperCase();
    return this.formError ? null : { fromDate: this.from, toDate: this.to, type: this.type, currencyCode: ccy || null };
  }

  generate(): void {
    const q = this.query();
    if (!q) return;
    this.generating = true;
    this.loadError = '';
    this.api.getCustomerStatement(this.customerUuid!, q).subscribe({
      next: s => { this.generating = false; this.statement = s; this.loadLog(); },
      error: err => { this.generating = false; this.statement = null; this.loadError = arApError(err, 'The statement could not be generated.'); }
    });
  }

  loadLog(): void {
    if (!this.customerUuid) return;
    this.api.getStatementLog('Customer', this.customerUuid).pipe(catchError(() => of(null))).subscribe(r => this.log = r?.data ?? []);
  }

  downloadPdf(): void {
    const q = this.query();
    if (!q) return;
    this.downloading = true;
    this.api.downloadCustomerStatementPdf(this.customerUuid!, q).subscribe({
      next: res => { this.downloading = false; saveDownload(res, `statement-${this.to}.pdf`); },
      error: async err => { this.downloading = false; this.toast('error', await arApErrorAsync(err, 'The PDF could not be made.')); }
    });
  }

  openEmail(): void {
    if (!this.query()) return;
    this.emailTo = '';
    this.emailSubject = '';
    this.emailMessage = '';
    this.emailError = '';
    this.emailOpen = true;
  }

  sendEmail(): void {
    const q = this.query();
    if (!q) return;
    const to = this.emailTo.trim();
    // REV — one address (the server refuses lists with a 400).
    if (/[,;]/.test(to)) { this.emailError = 'Give one email address.'; return; }
    if (to && !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(to)) { this.emailError = 'Enter a valid email address, or leave it empty for the customer\'s own.'; return; }
    this.sending = true;
    this.api.emailCustomerStatement(this.customerUuid!, q, { emailTo: to || null, subject: this.emailSubject.trim() || null, message: this.emailMessage.trim() || null }).subscribe({
      next: r => { this.sending = false; this.emailOpen = false; this.toast('success', `The statement was sent${r?.emailedTo ? ' to ' + r.emailedTo : ''}.`); this.loadLog(); },
      error: err => { this.sending = false; this.emailError = arApError(err, 'The statement could not be sent.'); }
    });
  }

  runBatch(): void {
    const p = statementPeriodProblem(this.from, this.to);
    if (p) { this.formError = p; return; }
    this.batching = true;
    this.batch = null;
    this.api.batchStatements({ from: this.from, to: this.to, type: this.type, minimumBalance: this.minimumBalance ?? null, sendEmail: this.batchEmail }).subscribe({
      next: r => { this.batching = false; this.batch = r; },
      error: err => { this.batching = false; this.toast('error', arApError(err, 'The batch could not be run.')); }
    });
  }

  label(s: string | null | undefined): string { return labelOf(s); }
  amount(n: number | null | undefined): string { return money(n); }

  private toast(severity: 'success' | 'error', detail: string): void {
    this.messages.add({ severity, summary: severity === 'success' ? 'Done' : 'Error', detail, life: severity === 'error' ? 8000 : 3000 });
  }
}
