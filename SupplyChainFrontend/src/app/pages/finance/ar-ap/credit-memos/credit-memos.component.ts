import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
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
  AR_AP_PERMS, AR_ROUTE, ArApService, CREDIT_MEMOS_ROUTE, CreditMemo, PartnerOpenItem
} from '../../../../services/ar-ap.service';
import { BusinessPartnerModel, BusinessPartnerService } from '../../../../services/business-partner.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { ApplyRow, applicationProblem, arApError, creditMemoProblem, labelOf, money, round2, today } from '../ar-ap.shared';

interface MemoLineDraft { description: string; amount: number | null; }

/**
 * A48 C7 / contract §4 — `/finance/ar/credit-memos[?customer=]`: a customer's open credits (credit memos and A38
 * credit notes with something left to apply), a new credit memo (against an invoice or standalone, amount lines, applied
 * to the original at once by default — D-AR-7), and applying a credit to any open invoice of the same customer and
 * currency, partly or in pieces, with its applications listed. Creating and applying need AR_CREDIT_MEMO.
 */
@Component({
  selector: 'app-credit-memos',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, AutoCompleteModule, ButtonModule, CheckboxModule, DialogModule, InputNumberModule, InputTextModule,
    SelectModule, ToastModule, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Finance', 'Receivables', 'Credit memos']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canWrite" label="New credit memo" icon="pi pi-plus" [disabled]="!customerUuid" (onClick)="openNew()" data-testid="new"></p-button>
    <span class="sf-end"><a class="p-button p-button-text" [routerLink]="arRoute"><i class="pi pi-arrow-left"></i>&nbsp;Back to AR</a></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Credit memos</h1><div class="sf-sub">Credit a customer and apply the credit to their open invoices</div></div></div>
    <section class="sf-card">
      <div class="sf-fields"><div class="sf-fld"><label for="cm-cust" class="sf-req">Customer</label>
        <p-autoComplete inputId="cm-cust" [(ngModel)]="customer" [suggestions]="suggestions" optionLabel="companyName" (completeMethod)="search($event)"
                        (onSelect)="choose($event.value)" [minLength]="1" appendTo="body" placeholder="Search customers…" data-testid="customer"></p-autoComplete></div></div>
    </section>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

    <section class="sf-card p0" *ngIf="customerUuid" data-testid="credits">
      <div class="sf-ch sf-pad"><h2>Open credits <span class="sf-count">{{ credits.length }}</span></h2></div>
      <div class="gl-scroll">
        <table class="aa-table" style="min-width:46rem">
          <thead><tr><th>Credit</th><th>Date</th><th>Against</th><th class="gl-num">Total</th><th class="gl-num">Applied</th><th class="gl-num">Remaining</th><th></th></tr></thead>
          <tbody>
            <tr *ngFor="let c of credits" class="aa-click" [class.rc-sel]="selected?.uuid === c.uuid" (click)="select(c)" [attr.data-testid]="'credit-' + c.uuid">
              <td class="sf-mono">{{ c.creditNoteNumber }}<span class="sf-mu sf-sm"> · {{ label(c.sourceType) }}</span></td>
              <td>{{ c.date | date:'dd MMM yyyy' }}</td>
              <td class="sf-mono">{{ c.originalInvoiceNumber || '—' }}</td>
              <td class="gl-num">{{ amount(c.total) }} <span class="sf-mu sf-sm">{{ c.currencyCode }}</span></td>
              <td class="gl-num">{{ amount(c.applied) }}</td>
              <td class="gl-num"><b>{{ amount(c.remaining) }}</b></td>
              <td><p-button label="Open" [text]="true" size="small" (onClick)="select(c); $event.stopPropagation()"></p-button></td>
            </tr>
            <tr *ngIf="!credits.length"><td colspan="7" class="sf-empty" data-testid="no-credits">Nothing left to apply for this customer.</td></tr>
          </tbody>
        </table>
      </div>
    </section>

    <ng-container *ngIf="selected as s">
      <section class="sf-card" data-testid="memo">
        <div class="sf-ch"><h2><span class="sf-mono">{{ s.creditNoteNumber }}</span> — {{ amount(s.remaining) }} {{ s.currencyCode }} left</h2></div>
        <div class="sf-kv">
          <span>Customer</span><span>{{ s.customerName }}</span>
          <span>Against</span><span>{{ s.originalInvoiceNumber || 'standalone' }}</span>
          <span>Total</span><span>{{ amount(s.subtotal) }} + tax {{ amount(s.taxAmount) }} = {{ amount(s.total) }}</span>
          <span>Applied / refunded</span><span>{{ amount(s.applied) }} / {{ amount(s.refunded) }}</span>
          <ng-container *ngIf="s.notes"><span>Reason</span><span>{{ s.notes }}</span></ng-container>
        </div>
        <div class="gl-scroll" *ngIf="s.lines.length"><table class="aa-table" style="min-width:30rem;margin-top:.5rem"><tbody>
          <tr *ngFor="let l of s.lines"><td>{{ l.lineNo }}</td><td>{{ l.description }}</td><td class="sf-mu sf-sm">{{ l.taxCode || '' }}</td><td class="gl-num">{{ amount(l.amount) }}</td></tr>
        </tbody></table></div>
      </section>

      <section class="sf-card p0" *ngIf="canWrite && s.remaining > 0" data-testid="apply">
        <div class="sf-ch sf-pad"><h2>Apply to open invoices</h2><span class="sf-end sf-mu sf-sm">{{ s.currencyCode }} invoices of {{ s.customerName }}</span></div>
        <div class="gl-scroll">
          <table class="aa-table" style="min-width:36rem">
            <thead><tr><th>Invoice</th><th>Due</th><th class="gl-num">Open</th><th class="gl-num">Apply</th></tr></thead>
            <tbody>
              <tr *ngFor="let r of rows" [attr.data-testid]="'row-' + r.invoiceNumber">
                <td class="sf-mono">{{ r.invoiceNumber }}</td><td>{{ dueOf(r.invoiceUuid) | date:'dd MMM yyyy' }}</td>
                <td class="gl-num">{{ amount(r.open) }}</td>
                <td class="gl-num"><p-inputNumber [(ngModel)]="r.amount" mode="decimal" [minFractionDigits]="2" [maxFractionDigits]="2" [min]="0" [ariaLabel]="'Apply to ' + r.invoiceNumber"></p-inputNumber></td>
              </tr>
              <tr *ngIf="!rows.length"><td colspan="4" class="sf-empty" data-testid="no-invoices">No open invoice in {{ s.currencyCode }} for this customer.</td></tr>
            </tbody>
          </table>
        </div>
        <div class="gl-note wn" *ngIf="applyError" style="margin: .5rem 1rem" data-testid="apply-error"><i class="pi pi-info-circle"></i><div>{{ applyError }}</div></div>
        <div class="aa-actions sf-pad">
          <p-button label="Fill oldest first" [text]="true" size="small" [disabled]="!rows.length" (onClick)="fillOldest()" data-testid="fill"></p-button>
          <p-button label="Apply" icon="pi pi-check" [disabled]="applying || !rows.length" [loading]="applying" (onClick)="apply()" data-testid="apply-btn"></p-button>
        </div>
      </section>

      <section class="sf-card p0" data-testid="applications">
        <div class="sf-ch sf-pad"><h2>Applications <span class="sf-count">{{ s.applications.length }}</span></h2></div>
        <div class="gl-scroll"><table class="aa-table" style="min-width:32rem"><tbody>
          <tr *ngFor="let a of s.applications" [class.aa-off]="a.reversedAt"><td>{{ a.appliedAt | date:'dd MMM yyyy' }}</td><td class="sf-mono">{{ a.invoiceNumber }}</td>
            <td class="gl-num">{{ amount(a.amount) }}</td><td class="sf-mu sf-sm">{{ a.appliedByName || '' }}<ng-container *ngIf="a.reversedAt"> · reversed</ng-container></td></tr>
          <tr *ngIf="!s.applications.length"><td colspan="4" class="sf-empty">Not applied yet.</td></tr>
        </tbody></table></div>
      </section>
    </ng-container>
  </div>
</sf-page>

<p-dialog [(visible)]="newOpen" [modal]="true" header="New credit memo" [style]="{ width: '40rem', maxWidth: '98vw' }" styleClass="sdlg">
  <div class="aa-dlg-body">
    <div class="sf-fields">
      <div class="sf-fld"><label for="cm-orig">Against invoice</label>
        <p-select inputId="cm-orig" [options]="originalOptions" [(ngModel)]="originalUuid" optionLabel="label" optionValue="value" appendTo="body" data-testid="original"></p-select></div>
      <div class="sf-fld"><label for="cm-date">Date</label><input pInputText id="cm-date" type="date" [(ngModel)]="date" [max]="todayIso" data-testid="date" /></div>
      <div class="sf-fld" *ngIf="!originalUuid"><label for="cm-ccy">Currency</label>
        <input pInputText id="cm-ccy" [(ngModel)]="currencyCode" maxlength="3" placeholder="Sale base" data-testid="currency" /></div>
      <div class="sf-fld" *ngIf="originalUuid"><label>&nbsp;</label>
        <label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="applyToOriginal" data-testid="apply-original"></p-checkbox> Apply to the invoice at once</label></div>
    </div>
    <div class="sf-fld"><label for="cm-reason" class="sf-req">Reason</label><input pInputText id="cm-reason" [(ngModel)]="reason" maxlength="500" data-testid="reason" /></div>
    <div class="sf-ch"><h2>Lines</h2><span class="sf-end"><p-button label="Add line" icon="pi pi-plus" [text]="true" size="small" (onClick)="addLine()" data-testid="add-line"></p-button></span></div>
    <div class="aa-memo-line" *ngFor="let l of lines; let i = index" [attr.data-testid]="'line-' + i">
      <input pInputText [(ngModel)]="l.description" maxlength="500" placeholder="Description" [attr.aria-label]="'Description ' + (i + 1)" />
      <p-inputNumber [(ngModel)]="l.amount" mode="decimal" [minFractionDigits]="2" [maxFractionDigits]="2" [min]="0" placeholder="Amount (tax incl.)" [ariaLabel]="'Amount ' + (i + 1)"></p-inputNumber>
      <p-button icon="pi pi-trash" [text]="true" size="small" severity="danger" [disabled]="lines.length === 1" (onClick)="removeLine(i)" ariaLabel="Remove line"></p-button>
    </div>
    <div class="sf-mu sf-sm">Total {{ amount(draftTotal) }}</div>
    <div class="gl-note er" *ngIf="formError" data-testid="form-error"><i class="pi pi-exclamation-triangle"></i><div>{{ formError }}</div></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="newOpen = false"></p-button>
    <p-button label="Create" icon="pi pi-check" [disabled]="saving" [loading]="saving" (onClick)="create()" data-testid="create"></p-button>
  </ng-template>
</p-dialog>`,
  styles: [`
    .aa-memo-line { display: grid; grid-template-columns: 1fr 11rem auto; gap: .5rem; align-items: center; }
    tr.rc-sel td { background: var(--sms-primary-soft); }
    @media (max-width: 640px) { .aa-memo-line { grid-template-columns: 1fr; } }
  `]
})
export class CreditMemosComponent implements OnInit {
  private readonly api = inject(ArApService);
  private readonly partners = inject(BusinessPartnerService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute, { optional: true });

  readonly arRoute = AR_ROUTE;
  readonly todayIso = today();

  customer: BusinessPartnerModel | { uuid: string; companyName: string } | string | null = null;
  suggestions: BusinessPartnerModel[] = [];
  customerUuid: string | null = null;
  credits: CreditMemo[] = [];
  openInvoices: PartnerOpenItem[] = [];
  loadError = '';
  selected: CreditMemo | null = null;
  rows: ApplyRow[] = [];
  applyError = '';
  applying = false;

  newOpen = false;
  originalUuid: string | null = null;
  date = today();
  currencyCode = '';
  applyToOriginal = true;
  reason = '';
  lines: MemoLineDraft[] = [{ description: '', amount: null }];
  formError = '';
  saving = false;

  get canWrite(): boolean { return this.auth.hasPermission(AR_AP_PERMS.AR_CREDIT_MEMO); }
  get draftTotal(): number { return round2(this.lines.reduce((s, l) => s + Number(l.amount || 0), 0)); }
  get originalOptions(): { label: string; value: string | null }[] {
    return [{ label: 'None (standalone credit)', value: null },
      ...this.openInvoices.map(i => ({ label: `${i.documentNumber} · ${money(i.openAmount)} ${i.currencyCode} open`, value: i.documentUuid as string | null }))];
  }

  ngOnInit(): void {
    const linked = this.route?.snapshot?.queryParamMap?.get('customer');
    if (linked) {
      this.partners.getPartnerById(linked).pipe(catchError(() => of(null))).subscribe(r => { if (r?.result) this.customer = r.result; });
      this.setCustomer(linked);
    }
  }

  search(e: AutoCompleteCompleteEvent): void {
    this.partners.getPartners({ isCustomer: true, active: true, search: e.query, pageSize: 20 }).pipe(catchError(() => of(null)))
      .subscribe(r => this.suggestions = r?.result?.data ?? []);
  }

  choose(p: BusinessPartnerModel): void {
    if (!p?.uuid) return;
    this.router.navigate([CREDIT_MEMOS_ROUTE], { queryParams: { customer: p.uuid }, replaceUrl: true });
    this.setCustomer(p.uuid);
  }

  setCustomer(uuid: string): void {
    this.customerUuid = uuid;
    this.selected = null;
    this.load();
  }

  load(keep: string | null = null): void {
    if (!this.customerUuid) return;
    const uuid = this.customerUuid;
    this.loadError = '';
    this.api.getOpenCredits(uuid).subscribe({
      next: list => {
        this.credits = list ?? [];
        const again = keep ? this.credits.find(c => c.uuid === keep) : null;
        if (again) this.select(again);
      },
      error: err => { this.credits = []; this.loadError = arApError(err, 'The open credits could not be loaded.'); }
    });
    // Open debit items only: the invoices a credit can go against.
    this.api.getCustomerOpenItems(uuid, { includeCredits: false }).pipe(catchError(() => of(null)))
      .subscribe(p => { this.openInvoices = (p?.data ?? []).filter(i => i.documentType === 'SALES_INVOICE' && i.openAmount > 0); this.buildRows(); });
  }

  select(c: CreditMemo): void {
    this.selected = { ...c, applications: c.applications ?? [], lines: c.lines ?? [] };
    this.applyError = '';
    this.buildRows();
    if (!c.applications?.length) {
      this.api.getCreditApplications(c.uuid).pipe(catchError(() => of(null))).subscribe(a => {
        if (a && this.selected?.uuid === c.uuid) this.selected = { ...this.selected, applications: a };
      });
    }
  }

  private buildRows(): void {
    const s = this.selected;
    this.rows = !s ? [] : this.openInvoices.filter(i => i.currencyCode === s.currencyCode && i.openAmount > 0)
      .sort((a, b) => a.dueDate.localeCompare(b.dueDate))
      .map(i => ({ invoiceUuid: i.documentUuid, invoiceNumber: i.documentNumber, open: i.openAmount, amount: null }));
  }

  dueOf(uuid: string): string | null { return this.openInvoices.find(i => i.documentUuid === uuid)?.dueDate ?? null; }

  /** Oldest due first until the credit is used up. */
  fillOldest(): void {
    let left = this.selected?.remaining ?? 0;
    for (const r of this.rows) {
      const take = round2(Math.min(left, r.open));
      r.amount = take > 0 ? take : null;
      left = round2(left - take);
    }
  }

  apply(): void {
    const s = this.selected;
    if (!s) return;
    this.applyError = applicationProblem(this.rows, s.remaining, s.creditNoteNumber) ?? '';
    if (this.applyError) return;
    const applications = this.rows.filter(r => r.amount && r.amount > 0).map(r => ({ invoiceUuid: r.invoiceUuid, amount: round2(r.amount!) }));
    this.applying = true;
    this.api.applyCreditMemo(s.uuid, applications).subscribe({
      next: m => { this.applying = false; this.toast(`${m.creditNoteNumber}: ${money(m.applied - s.applied)} applied.`); this.selected = m; this.load(m.uuid); },
      error: err => { this.applying = false; this.applyError = arApError(err, 'The credit could not be applied.'); }
    });
  }

  openNew(): void {
    this.originalUuid = null;
    this.date = today();
    this.currencyCode = '';
    this.applyToOriginal = true;
    this.reason = '';
    this.lines = [{ description: '', amount: null }];
    this.formError = '';
    this.newOpen = true;
  }

  addLine(): void { this.lines = [...this.lines, { description: '', amount: null }]; }
  removeLine(i: number): void { if (this.lines.length > 1) this.lines = this.lines.filter((_, j) => j !== i); }

  create(): void {
    if (!this.customerUuid) return;
    this.formError = creditMemoProblem(this.reason, this.lines) ?? '';
    if (!this.formError && this.date > this.todayIso) this.formError = 'The date cannot be in the future.';
    if (this.formError) return;
    this.saving = true;
    this.api.createCreditMemo({
      customerUuid: this.customerUuid, originalInvoiceUuid: this.originalUuid, date: this.date,
      currencyCode: this.originalUuid ? null : (this.currencyCode.trim().toUpperCase() || null), reason: this.reason.trim(),
      lines: this.lines.map(l => ({ description: l.description.trim(), amount: round2(Number(l.amount)) })),
      applyToOriginal: this.originalUuid ? this.applyToOriginal : null
    }).subscribe({
      next: m => { this.saving = false; this.newOpen = false; this.toast(`${m.creditNoteNumber} is created for ${money(m.total)} ${m.currencyCode}.`); this.selected = m; this.load(m.uuid); },
      error: err => { this.saving = false; this.formError = arApError(err, 'The credit memo could not be created.'); }
    });
  }

  label(s: string | null | undefined): string { return labelOf(s); }
  amount(n: number | null | undefined): string { return money(n); }

  private toast(detail: string): void { this.messages.add({ severity: 'success', summary: 'Done', detail, life: 4000 }); }
}
