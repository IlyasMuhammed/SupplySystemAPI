import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { AutoCompleteCompleteEvent, AutoCompleteModule } from 'primeng/autocomplete';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';

import {
  AP_ROUTE, AR_AP_PERMS, ArApService, DEBIT_MEMOS_ROUTE, DEBIT_MEMO_REASONS, DebitMemo, PartnerOpenItem
} from '../../../../services/ar-ap.service';
import { BusinessPartnerModel, BusinessPartnerService } from '../../../../services/business-partner.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { ApplyRow, applicationProblem, arApError, labelOf, money, round2, today } from '../ar-ap.shared';

export function debitMemoProblem(reason: string, amount: number | null, date: string, on = today()): string | null {
  if (!reason) return 'Choose the reason.';
  if (!(Number(amount) > 0)) return 'The amount must be greater than zero.';
  if (date && date > on) return 'The date cannot be in the future.';
  return null;
}

/**
 * A48 C14 / contract §9 — `/finance/ap/debit-memos[?vendor=]`: raise an AP debit memo for a vendor (a price difference,
 * short supply, rebate…), optionally against one of their invoices (deducted at once when that invoice still owes at
 * least the memo, else carried forward), and apply a carried-forward note to one or more open invoices of the vendor,
 * with its applications listed. Notes from §9 `vendors/{uuid}/debit-memos` (open only, or all) and `debit-memo/{uuid}`
 * (FE-8); the invoices from §5 open items. Every read takes AP_VIEW or AP_DEBIT_MEMO, so a memo-only user can work the
 * page; creating and applying need AP_DEBIT_MEMO.
 */
@Component({
  selector: 'app-debit-memos',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, AutoCompleteModule, ButtonModule, DialogModule, InputNumberModule, InputTextModule, SelectModule,
    ToastModule, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Finance', 'Payables', 'Debit memos']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canWrite" label="New debit memo" icon="pi pi-plus" [disabled]="!vendorUuid" (onClick)="openNew()" data-testid="new"></p-button>
    <span class="sf-end"><a class="p-button p-button-text" [routerLink]="apRoute"><i class="pi pi-arrow-left"></i>&nbsp;AP dashboard</a></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Debit memos</h1><div class="sf-sub">Claim back from a vendor and set the claim against their open invoices</div></div></div>
    <section class="sf-card">
      <div class="sf-fields"><div class="sf-fld"><label for="dm-vendor" class="sf-req">Vendor</label>
        <p-autoComplete inputId="dm-vendor" [(ngModel)]="vendor" [suggestions]="suggestions" optionLabel="companyName" (completeMethod)="search($event)"
                        (onSelect)="choose($event.value)" [minLength]="1" appendTo="body" placeholder="Search vendors…" data-testid="vendor"></p-autoComplete></div></div>
    </section>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

    <section class="sf-card p0" *ngIf="vendorUuid" data-testid="debits">
      <div class="sf-ch sf-pad"><h2>{{ openOnly ? 'Open debit notes' : 'All debit notes' }} <span class="sf-count">{{ debitsTotal }}</span></h2>
        <span class="sf-end"><button type="button" class="sf-chip" [class.on]="openOnly" (click)="setOpenOnly(true)" data-testid="chip-open">Open</button>
          <button type="button" class="sf-chip" [class.on]="!openOnly" (click)="setOpenOnly(false)" data-testid="chip-all">All</button></span></div>
      <div class="gl-scroll">
        <table class="aa-table" style="min-width:44rem">
          <thead><tr><th>Note</th><th>Issued</th><th>Reason</th><th>Status</th><th class="gl-num">Amount</th><th class="gl-num">Remaining</th><th></th></tr></thead>
          <tbody>
            <tr *ngFor="let d of debits" class="aa-click" [class.rc-sel]="selected?.uuid === d.uuid" (click)="select(d)" [attr.data-testid]="'debit-' + d.uuid">
              <td class="sf-mono">{{ d.debitNoteNumber }}<span class="sf-mu sf-sm"> · {{ d.origin === 'MEMO' ? 'memo' : 'return' }}</span></td>
              <td>{{ d.issuedAt ? (d.issuedAt | date:'dd MMM yyyy') : '' }}</td>
              <td>{{ label(d.debitReason) }}</td>
              <td class="sf-sm">{{ label(d.applicationStatus) }}</td>
              <td class="gl-num">{{ amount(d.amount) }}</td>
              <td class="gl-num"><b>{{ amount(d.remainingAmount) }}</b></td>
              <td><p-button label="Open" [text]="true" size="small" (onClick)="select(d); $event.stopPropagation()"></p-button></td>
            </tr>
            <tr *ngIf="!debits.length"><td colspan="7" class="sf-empty" data-testid="no-debits">{{ openOnly ? 'No carried-forward debit note for this vendor.' : 'No debit note for this vendor.' }}</td></tr>
          </tbody>
        </table>
      </div>
    </section>

    <div class="gl-note" *ngIf="created as m" data-testid="created"><i class="pi pi-check"></i>
      <div><span class="sf-mono">{{ m.debitNoteNumber }}</span> — {{ amount(m.amount) }} · {{ label(m.debitReason) }} · {{ label(m.applicationStatus) }}
        <ng-container *ngIf="m.originalInvoiceNumber"> against {{ m.originalInvoiceNumber }}</ng-container>; {{ amount(m.remainingAmount) }} left to apply.</div></div>

    <ng-container *ngIf="selected as s">
      <section class="sf-card" data-testid="memo">
        <div class="sf-ch"><h2><span class="sf-mono">{{ s.debitNoteNumber }}</span> — {{ amount(s.remainingAmount) }} left</h2></div>
        <div class="sf-kv">
          <span>Reason</span><span>{{ label(s.debitReason) }}<ng-container *ngIf="s.debitReasonDetail"> · {{ s.debitReasonDetail }}</ng-container></span>
          <span>Against</span><span>{{ s.originalInvoiceNumber || '—' }}</span>
          <span>Amount</span><span>{{ amount(s.amount) }}</span>
          <span>Status</span><span>{{ label(s.status) }} · {{ label(s.applicationStatus) }}</span>
        </div>
      </section>

      <section class="sf-card p0" *ngIf="canWrite && s.remainingAmount > 0" data-testid="apply">
        <div class="sf-ch sf-pad"><h2>Apply to open invoices</h2><span class="sf-end sf-mu sf-sm" data-testid="apply-currency">{{ s.currencyCode ? s.currencyCode + ' invoices only' : 'invoices in the note’s currency only' }}</span></div>
        <div class="gl-scroll">
          <table class="aa-table" style="min-width:36rem">
            <thead><tr><th>Invoice</th><th>Due</th><th class="gl-num">Open</th><th class="gl-num">Apply</th></tr></thead>
            <tbody>
              <tr *ngFor="let r of rows" [attr.data-testid]="'row-' + r.invoiceNumber">
                <td class="sf-mono">{{ r.invoiceNumber }}</td><td>{{ dueOf(r.invoiceUuid) | date:'dd MMM yyyy' }}</td>
                <td class="gl-num">{{ amount(r.open) }} <span class="sf-mu sf-sm">{{ currencyOf(r.invoiceUuid) }}</span></td>
                <td class="gl-num"><p-inputNumber [(ngModel)]="r.amount" mode="decimal" [minFractionDigits]="2" [maxFractionDigits]="2" [min]="0" [ariaLabel]="'Apply to ' + r.invoiceNumber"></p-inputNumber></td>
              </tr>
              <tr *ngIf="!rows.length"><td colspan="4" class="sf-empty" data-testid="no-invoices">No open invoice for this vendor.</td></tr>
            </tbody>
          </table>
        </div>
        <div class="gl-note wn" *ngIf="applyError" style="margin: .5rem 1rem" data-testid="apply-error"><i class="pi pi-info-circle"></i><div>{{ applyError }}</div></div>
        <div class="aa-actions sf-pad"><p-button label="Apply" icon="pi pi-check" [disabled]="applying || !rows.length" [loading]="applying" (onClick)="apply()" data-testid="apply-btn"></p-button></div>
      </section>

      <section class="sf-card p0" data-testid="applications">
        <div class="sf-ch sf-pad"><h2>Applications <span class="sf-count">{{ s.applications.length }}</span></h2></div>
        <div class="gl-scroll"><table class="aa-table" style="min-width:32rem"><tbody>
          <tr *ngFor="let a of s.applications"><td>{{ a.appliedAt | date:'dd MMM yyyy' }}</td><td class="sf-mono">{{ a.invoiceNumber }}</td>
            <td class="gl-num">{{ amount(a.amount) }}</td><td class="sf-mu sf-sm">{{ a.appliedByName || '' }}</td></tr>
          <tr *ngIf="!s.applications.length"><td colspan="4" class="sf-empty">Not applied to any invoice through A48 yet.</td></tr>
        </tbody></table></div>
      </section>
    </ng-container>
  </div>
</sf-page>

<p-dialog [(visible)]="newOpen" [modal]="true" header="New debit memo" [style]="{ width: '34rem', maxWidth: '98vw' }" styleClass="sdlg">
  <div class="aa-dlg-body">
    <div class="sf-fields">
      <div class="sf-fld"><label for="dm-orig">Against invoice</label>
        <p-select inputId="dm-orig" [options]="originalOptions" [(ngModel)]="originalUuid" optionLabel="label" optionValue="value" appendTo="body" data-testid="original"></p-select></div>
      <div class="sf-fld"><label for="dm-date">Date</label><input pInputText id="dm-date" type="date" [(ngModel)]="date" [max]="todayIso" data-testid="date" /></div>
      <div class="sf-fld"><label for="dm-reason" class="sf-req">Reason</label>
        <p-select inputId="dm-reason" [options]="reasons" [(ngModel)]="reason" optionLabel="label" optionValue="value" appendTo="body" data-testid="reason"></p-select></div>
      <div class="sf-fld"><label for="dm-amt" class="sf-req">Amount</label>
        <p-inputNumber inputId="dm-amt" [(ngModel)]="memoAmount" mode="decimal" [minFractionDigits]="2" [maxFractionDigits]="2" [min]="0" data-testid="amount"></p-inputNumber></div>
    </div>
    <div class="sf-fld"><label for="dm-detail">Detail</label><input pInputText id="dm-detail" [(ngModel)]="reasonDetail" maxlength="500" data-testid="detail" /></div>
    <div class="sf-fld"><label for="dm-notes">Notes</label><input pInputText id="dm-notes" [(ngModel)]="notes" maxlength="1000" /></div>
    <div class="sf-mu sf-sm" *ngIf="originalUuid">Deducted from the invoice at once when it still owes at least this amount; otherwise carried forward to apply later.</div>
    <div class="gl-note er" *ngIf="formError" data-testid="form-error"><i class="pi pi-exclamation-triangle"></i><div>{{ formError }}</div></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="newOpen = false"></p-button>
    <p-button label="Create" icon="pi pi-check" [disabled]="saving" [loading]="saving" (onClick)="create()" data-testid="create"></p-button>
  </ng-template>
</p-dialog>`,
  styles: [`tr.rc-sel td { background: var(--sms-primary-soft); }`]
})
export class DebitMemosComponent implements OnInit {
  private readonly api = inject(ArApService);
  private readonly partners = inject(BusinessPartnerService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute, { optional: true });

  readonly apRoute = AP_ROUTE;
  readonly todayIso = today();
  readonly reasons = DEBIT_MEMO_REASONS.map(r => ({ label: labelOf(r), value: r as string }));

  vendor: BusinessPartnerModel | string | null = null;
  suggestions: BusinessPartnerModel[] = [];
  vendorUuid: string | null = null;
  debits: DebitMemo[] = [];
  debitsTotal = 0;
  openOnly = true;
  openInvoices: PartnerOpenItem[] = [];
  loadError = '';
  selected: DebitMemo | null = null;
  rows: ApplyRow[] = [];
  applyError = '';
  applying = false;
  created: DebitMemo | null = null;

  newOpen = false;
  originalUuid: string | null = null;
  date = today();
  reason = 'PRICE_DIFFERENCE';
  reasonDetail = '';
  memoAmount: number | null = null;
  notes = '';
  formError = '';
  saving = false;

  get canWrite(): boolean { return this.auth.hasPermission(AR_AP_PERMS.AP_DEBIT_MEMO); }
  get originalOptions(): { label: string; value: string | null }[] {
    return [{ label: 'None (carry forward)', value: null },
      ...this.openInvoices.map(i => ({ label: `${i.documentNumber} · ${money(i.openAmount)} ${i.currencyCode} open`, value: i.documentUuid as string | null }))];
  }

  ngOnInit(): void {
    const linked = this.route?.snapshot?.queryParamMap?.get('vendor');
    if (linked) {
      this.partners.getPartnerById(linked).pipe(catchError(() => of(null))).subscribe(r => { if (r?.result) this.vendor = r.result; });
      this.setVendor(linked);
    }
  }

  search(e: AutoCompleteCompleteEvent): void {
    this.partners.getPartners({ isVendor: true, active: true, search: e.query, pageSize: 20 }).pipe(catchError(() => of(null)))
      .subscribe(r => this.suggestions = r?.result?.data ?? []);
  }

  choose(p: BusinessPartnerModel): void {
    if (!p?.uuid) return;
    this.router.navigate([DEBIT_MEMOS_ROUTE], { queryParams: { vendor: p.uuid }, replaceUrl: true });
    this.setVendor(p.uuid);
  }

  setVendor(uuid: string): void {
    this.vendorUuid = uuid;
    this.selected = null;
    this.created = null;
    this.load();
  }

  /** FE-8 — the vendor's debit notes (§9 `vendors/{uuid}/debit-memos`) and its open invoices (§5 open items, AP_DEBIT_MEMO admitted). */
  load(keep: string | null = null): void {
    if (!this.vendorUuid) return;
    const vendor = this.vendorUuid;
    this.loadError = '';
    this.api.getVendorDebitMemos(vendor, this.openOnly).subscribe({
      next: p => {
        this.debits = p.data;
        this.debitsTotal = p.totalRecords;
        if (keep) this.open(keep);
      },
      error: err => { this.debits = []; this.debitsTotal = 0; this.loadError = arApError(err, 'The vendor\'s debit notes could not be loaded.'); }
    });
    this.api.getVendorOpenItems(vendor, { includeCredits: false }).pipe(catchError(() => of(null))).subscribe(p => {
      this.openInvoices = (p?.data ?? []).filter(i => i.documentType === 'SUPPLIER_INVOICE' && i.openAmount > 0);
      this.buildRows();
    });
  }

  setOpenOnly(on: boolean): void { if (this.openOnly !== on) { this.openOnly = on; this.load(); } }

  select(d: DebitMemo): void { this.open(d.uuid, d); }

  private openSeq = 0;

  /** The note's detail (§9 GET, FE-8): remaining amount and applications; only the latest request's answer is shown. */
  private open(uuid: string, shown: DebitMemo | null = null): void {
    const seq = ++this.openSeq;
    if (shown) this.selected = shown;
    this.applyError = '';
    this.buildRows();
    this.api.getDebitMemo(uuid).subscribe({
      next: m => { if (seq === this.openSeq) { this.selected = m; this.buildRows(); } },
      error: err => { if (seq === this.openSeq) this.toast(arApError(err, 'The debit note could not be loaded.'), 'error'); }
    });
  }

  /** REV (§9) — only invoices in the note's currency (when the note names it; the server refuses others with a 400). */
  private buildRows(): void {
    const ccy = this.selected?.currencyCode;
    this.rows = !this.selected ? [] : this.openInvoices.filter(i => !ccy || i.currencyCode === ccy)
      .sort((a, b) => a.dueDate.localeCompare(b.dueDate))
      .map(i => ({ invoiceUuid: i.documentUuid, invoiceNumber: i.documentNumber, open: i.openAmount, amount: null }));
  }

  dueOf(uuid: string): string | null { return this.openInvoices.find(i => i.documentUuid === uuid)?.dueDate ?? null; }
  currencyOf(uuid: string): string { return this.openInvoices.find(i => i.documentUuid === uuid)?.currencyCode ?? ''; }

  apply(): void {
    const s = this.selected;
    if (!s) return;
    this.applyError = applicationProblem(this.rows, s.remainingAmount, s.debitNoteNumber) ?? '';
    if (this.applyError) return;
    const applications = this.rows.filter(r => r.amount && r.amount > 0).map(r => ({ invoiceUuid: r.invoiceUuid, amount: round2(r.amount!) }));
    this.applying = true;
    this.api.applyDebitMemo(s.uuid, applications).subscribe({
      next: m => {
        this.applying = false;
        this.selected = { ...m, applications: m.applications ?? [] };
        this.toast(`${m.debitNoteNumber} applied; ${money(m.remainingAmount)} left.`);
        this.load();
      },
      error: err => { this.applying = false; this.applyError = arApError(err, 'The debit memo could not be applied.'); }
    });
  }

  openNew(): void {
    this.originalUuid = null;
    this.date = today();
    this.reason = 'PRICE_DIFFERENCE';
    this.reasonDetail = '';
    this.memoAmount = null;
    this.notes = '';
    this.formError = '';
    this.newOpen = true;
  }

  create(): void {
    if (!this.vendorUuid) return;
    this.formError = debitMemoProblem(this.reason, this.memoAmount, this.date) ?? '';
    if (this.formError) return;
    this.saving = true;
    this.api.createDebitMemo({
      vendorUuid: this.vendorUuid, originalInvoiceUuid: this.originalUuid, date: this.date, reason: this.reason,
      reasonDetail: this.reasonDetail.trim() || null, amount: round2(Number(this.memoAmount)), notes: this.notes.trim() || null
    }).subscribe({
      next: m => { this.saving = false; this.newOpen = false; this.created = m; this.toast(`${m.debitNoteNumber} is created.`); this.load(m.remainingAmount > 0 ? m.uuid : null); },
      error: err => { this.saving = false; this.formError = arApError(err, 'The debit memo could not be created.'); }
    });
  }

  label(s: string | null | undefined): string { return labelOf(s); }
  amount(n: number | null | undefined): string { return money(n); }

  private toast(detail: string, severity: 'success' | 'error' = 'success'): void {
    this.messages.add({ severity, summary: severity === 'success' ? 'Done' : 'Error', detail, life: severity === 'error' ? 8000 : 4000 });
  }
}
