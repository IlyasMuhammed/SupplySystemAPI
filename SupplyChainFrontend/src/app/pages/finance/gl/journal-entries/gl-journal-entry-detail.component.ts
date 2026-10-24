import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DatePickerModule } from 'primeng/datepicker';
import { DialogModule } from 'primeng/dialog';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Subscription } from 'rxjs';

import {
  GL_JOURNAL_PERMS, GL_JOURNAL_ROUTE, GL_RECONCILIATION_ROUTE, GlJournalService, JournalEntryDetail
} from '../../../../services/gl-journal.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { toIsoDate } from '../../../finance-setup/finance-setup.shared';
import { glError, money } from '../gl.shared';
import { MAX_REVERSAL_REASON, baseTotals, entrySourceRoute, journalStatusTone, kindLabel, sourceLabel } from '../journal.shared';

/**
 * A47 C6 / C7 (tasks 260–263) — `/gl/journal-entries/:uuid`: one journal entry with its lines (base amounts, the
 * transaction-currency amount, partner, reconciliation). Read-only once Posted / Reversed. Draft: Edit, Post, Delete;
 * Posted: Reverse with a reason and an optional date (a gateway journal is reversed in the ledger only — its source
 * document is untouched). Links to the reversal / reversed entry and, softly, to the source document.
 */
@Component({
  selector: 'app-gl-journal-entry-detail',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, ConfirmDialogModule, DatePickerModule, DialogModule, TextareaModule, ToastModule, ...FLOW],
  providers: [MessageService, ConfirmationService],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast></p-toast>
<p-confirmDialog key="gl-je" [style]="{ width: '30rem', maxWidth: '96vw' }"></p-confirmDialog>
<sf-page [crumbs]="['Finance', 'General Ledger', 'Journal Entries', e?.entryNumber || '…']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands" *ngIf="e">
    <p-button *ngIf="canPost" label="Post" icon="pi pi-check" [loading]="busy === 'post'" [disabled]="!!busy" (onClick)="confirmPost()" data-testid="post"></p-button>
    <p-button *ngIf="canEdit" label="Edit" icon="pi pi-pencil" [text]="!canPost" [outlined]="canPost" [routerLink]="[route, e.uuid, 'edit']" data-testid="edit"></p-button>
    <p-button *ngIf="canReverse" label="Reverse" icon="pi pi-replay" [disabled]="!!busy" (onClick)="openReverse()" data-testid="reverse"></p-button>
    <span class="sf-sep" *ngIf="canDelete"></span>
    <p-button *ngIf="canDelete" label="Delete" icon="pi pi-trash" [text]="true" severity="danger" [disabled]="!!busy" (onClick)="confirmDelete()" data-testid="delete"></p-button>
    <span class="sf-end"><p-button icon="pi pi-list" label="All entries" [text]="true" [routerLink]="[route]"></p-button></span>
  </div>

  <div class="sf-body" *ngIf="loading || loadError">
    <div class="sf-card sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>
  </div>

  <ng-container *ngIf="e && !loading">
    <header class="sf-hdr">
      <div class="sf-t"><div>
        <div class="sf-kicker">{{ e.journalType?.name || 'Journal entry' }} · {{ source }}</div>
        <h1><span class="sf-mono">{{ e.entryNumber }}</span> <span class="sf-pill" [ngClass]="tone" data-testid="status">{{ e.status }}</span></h1>
        <div class="sf-meta">{{ e.entryDate | date:'dd MMM yyyy' }}<ng-container *ngIf="e.fiscalPeriod"> · {{ e.fiscalPeriod.periodName }}</ng-container>
          <ng-container *ngIf="e.reference"> · {{ e.reference }}</ng-container></div>
      </div></div>
      <div class="sf-facts">
        <div class="sf-kpi"><span class="l">Debit</span><span class="v">{{ amount(e.totalDebit) }}</span><span class="s">{{ baseLabel }}</span></div>
        <div class="sf-kpi"><span class="l">Credit</span><span class="v">{{ amount(e.totalCredit) }}</span><span class="s">{{ baseLabel }}</span></div>
        <div class="sf-kpi"><span class="l">Lines</span><span class="v">{{ e.lines.length }}</span></div>
      </div>
    </header>

    <div class="sf-body">
      <div class="gl-note" *ngIf="e.reversesEntry" data-testid="reverses">
        <i class="pi pi-replay"></i><div>Reversal of <a class="sf-mono" [routerLink]="[route, e.reversesEntry.uuid]">{{ e.reversesEntry.entryNumber }}</a><ng-container *ngIf="e.reversalReason">: {{ e.reversalReason }}</ng-container></div>
      </div>
      <div class="gl-note er" *ngIf="e.reversedByEntry" data-testid="reversed-by">
        <i class="pi pi-replay"></i><div>Reversed by <a class="sf-mono" [routerLink]="[route, e.reversedByEntry.uuid]">{{ e.reversedByEntry.entryNumber }}</a><ng-container *ngIf="e.reversalReason && !e.reversesEntry">: {{ e.reversalReason }}</ng-container></div>
      </div>
      <div class="gl-note wn" *ngIf="e.status === 'Draft' && !e.isManual" data-testid="gateway-draft">
        <i class="pi pi-info-circle"></i><div>Created as a draft by the posting gateway (journals are not posted automatically). Review it and post it.</div>
      </div>

      <section class="sf-card p0">
        <div class="sf-ch sf-pad"><h2>Lines</h2></div>
        <div class="gl-scroll">
          <table class="je-view" data-testid="lines">
            <thead><tr><th style="width:2.5rem">#</th><th>Account</th><th>Description</th><th>Partner</th>
              <th class="gl-num">Debit</th><th class="gl-num">Credit</th><th class="gl-num" *ngIf="foreign">{{ e.currencyCode }}</th><th>Reconciled</th></tr></thead>
            <tbody>
              <tr *ngFor="let l of e.lines" [attr.data-testid]="'line-' + l.lineNumber">
                <td class="sf-mu">{{ l.lineNumber }}</td>
                <td><span class="sf-mono">{{ l.account.accountCode }}</span> {{ l.account.accountName }}</td>
                <td>{{ l.description || '' }}</td>
                <td>{{ l.partnerName || '' }}<span class="sf-mu sf-sm" *ngIf="l.partnerType && l.partnerName"> · {{ l.partnerType }}</span></td>
                <td class="gl-num">{{ l.debit ? amount(l.debit) : '' }}</td>
                <td class="gl-num">{{ l.credit ? amount(l.credit) : '' }}</td>
                <td class="gl-num" *ngIf="foreign">{{ l.amountCurrency != null ? amount(l.amountCurrency) : '' }}</td>
                <td><ng-container *ngIf="l.reconcileGroupNumber; else noRec">
                    <span class="sf-pill" [ngClass]="l.reconciled ? 'ok' : 'wn'">{{ l.reconciled ? 'Full' : 'Partial' }}</span>
                    <a class="sf-mono sf-sm" [routerLink]="reconRoute" [queryParams]="{ accountUuid: l.account.uuid, groupUuid: l.reconcileGroupUuid }"
                       [attr.data-testid]="'group-' + l.lineNumber"> {{ l.reconcileGroupNumber }}</a></ng-container>
                  <ng-template #noRec><span class="sf-mu">—</span></ng-template></td>
              </tr>
            </tbody>
            <tfoot>
              <tr class="sf-tot">
                <td colspan="4">Total <span class="je-bal" [class.bad]="!totals.balanced">{{ totals.balanced ? 'balanced' : 'not balanced' }}</span></td>
                <td class="gl-num" data-testid="total-debit">{{ amount(totals.debit) }}</td>
                <td class="gl-num" data-testid="total-credit">{{ amount(totals.credit) }}</td>
                <td *ngIf="foreign"></td><td></td>
              </tr>
            </tfoot>
          </table>
        </div>
      </section>

      <section class="sf-card">
        <div class="sf-ch"><h2>Details</h2></div>
        <div class="sf-kv">
          <span>Journal type</span><span>{{ e.journalType?.code }} — {{ e.journalType?.name }} <span class="sf-mu">({{ kind(e.journalType?.type) }})</span></span>
          <span>Description</span><span>{{ e.description || '—' }}</span>
          <span>Source</span><span data-testid="source">
            <a *ngIf="sourceRoute as r; else plainSource" [routerLink]="r" data-testid="source-link">{{ source }}</a>
            <ng-template #plainSource>{{ source }}</ng-template>
            <span class="sf-mu sf-sm" *ngIf="e.sourceModule"> · {{ e.sourceModule }}</span></span>
          <span *ngIf="e.documentDate">Document date</span><span *ngIf="e.documentDate">{{ e.documentDate | date:'dd MMM yyyy' }}</span>
          <span>Currency</span><span>{{ e.currencyCode || '—' }}<ng-container *ngIf="foreign && e.exchangeRate"> · rate {{ e.exchangeRate }}</ng-container></span>
          <span *ngIf="e.fiscalYear">Fiscal year</span><span *ngIf="e.fiscalYear">{{ e.fiscalYear.fiscalYearName }}</span>
          <span>Created</span><span>{{ e.createdAt | date:'dd MMM yyyy, HH:mm' }}<ng-container *ngIf="e.createdByName"> by {{ e.createdByName }}</ng-container></span>
          <span *ngIf="e.postedAt">Posted</span><span *ngIf="e.postedAt">{{ e.postedAt | date:'dd MMM yyyy, HH:mm' }}<ng-container *ngIf="e.postedByName"> by {{ e.postedByName }}</ng-container></span>
          <span *ngIf="e.isOpeningEntry || e.isClosingEntry">Adjustment</span><span *ngIf="e.isOpeningEntry || e.isClosingEntry">{{ e.isOpeningEntry ? 'Opening entry' : 'Closing entry' }}</span>
        </div>
      </section>
    </div>
  </ng-container>
</sf-page>

<p-dialog [(visible)]="reverseVisible" [modal]="true" [draggable]="false" [style]="{ width: '32rem' }" [breakpoints]="{ '640px': '96vw' }"
          [header]="'Reverse ' + (e?.entryNumber || '')">
  <div class="gl-note wn" *ngIf="e && !e.isManual" style="margin-bottom:.75rem" data-testid="gateway-warning">
    <i class="pi pi-exclamation-triangle"></i>
    <div>This journal was posted from {{ source.toLowerCase() }}. Reversing it changes the ledger only — the source document stays as it is.</div>
  </div>
  <div class="sf-fields">
    <div class="sf-fld" style="grid-column: 1 / -1">
      <label for="rv-reason" class="sf-req">Reason</label>
      <textarea pTextarea id="rv-reason" rows="3" [(ngModel)]="reverseReason" [maxlength]="maxReason" data-testid="rv-reason"></textarea>
      <span class="gl-sub">{{ reverseReason.length }}/{{ maxReason }}</span>
    </div>
    <div class="sf-fld">
      <label for="rv-date">Reversal date</label>
      <p-datepicker inputId="rv-date" [(ngModel)]="reverseDate" dateFormat="dd M yy" [showIcon]="true" [showClear]="true" appendTo="body"
                    placeholder="Same as the entry" data-testid="rv-date"></p-datepicker>
      <span class="gl-sub">Empty: dated {{ e?.entryDate | date:'dd MMM yyyy' }}, like the entry. The date's period must be open.</span>
    </div>
  </div>
  <div class="sf-sm" style="color: var(--sms-danger); margin-top:.5rem" *ngIf="reverseError" data-testid="rv-error">{{ reverseError }}</div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" (onClick)="reverseVisible = false"></p-button>
    <p-button label="Reverse entry" icon="pi pi-replay" severity="danger" [loading]="busy === 'reverse'" (onClick)="reverse()" data-testid="rv-confirm"></p-button>
  </ng-template>
</p-dialog>`,
  styles: [`
    .je-view { width: 100%; min-width: 52rem; border-collapse: collapse; font-size: .875rem; }
    .je-view th, .je-view td { padding: .4rem .75rem; border-top: 1px solid var(--sms-border-soft); text-align: left; }
    .je-view th.gl-num, .je-view td.gl-num { text-align: right; }
    .je-view tfoot td { font-weight: 600; }
    .je-bal { font-weight: 400; font-size: .75rem; color: var(--sms-ok); margin-left: .375rem; }
    .je-bal.bad { color: var(--sms-danger); }
  `]
})
export class GlJournalEntryDetailComponent implements OnInit, OnDestroy {
  private readonly api = inject(GlJournalService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService);
  private readonly router = inject(Router);
  private readonly activated = inject(ActivatedRoute);
  private sub: Subscription | null = null;

  readonly route = GL_JOURNAL_ROUTE;
  readonly reconRoute = GL_RECONCILIATION_ROUTE;
  readonly maxReason = MAX_REVERSAL_REASON;
  e: JournalEntryDetail | null = null;
  loading = false;
  loadError = '';
  busy: 'post' | 'reverse' | 'delete' | null = null;

  reverseVisible = false;
  reverseReason = '';
  reverseDate: Date | null = null;
  reverseError = '';

  private has(code: string): boolean { return this.auth.hasPermission(code); }
  /** The server's flags win when it sent them; else state + permission. */
  get canPost(): boolean { return !!this.e && this.e.status === 'Draft' && (this.e.canPost ?? this.has(GL_JOURNAL_PERMS.POST)); }
  get canEdit(): boolean { return !!this.e && this.e.status === 'Draft' && (this.e.canEdit ?? (this.has(GL_JOURNAL_PERMS.EDIT) && this.e.isManual !== false)); }
  get canDelete(): boolean { return !!this.e && this.e.status === 'Draft' && (this.e.canDelete ?? (this.has(GL_JOURNAL_PERMS.DELETE) && this.e.isManual !== false)); }
  get canReverse(): boolean {
    // §3 / C-3 — a reversal, the opening balance entry and the year-end closing entry cannot be reversed.
    return !!this.e && this.e.status === 'Posted' && !this.e.reversedByEntry && !this.e.reversesEntry
      && this.e.entryType !== 'OPENING_BALANCE' && this.e.entryType !== 'YEAR_END_CLOSE'
      && (this.e.canReverse ?? this.has(GL_JOURNAL_PERMS.REVERSE));
  }
  get tone(): string { return journalStatusTone(this.e?.status); }
  get source(): string { return this.e ? sourceLabel(this.e) : ''; }
  get sourceRoute(): string[] | null { return this.e ? entrySourceRoute(this.e) : null; }
  /** REV-18 — exact equality of the 2-dp base totals, like the server (REV-2). */
  get totals() { return baseTotals(this.e?.lines ?? []); }
  /** Lines carry a transaction-currency amount different from base. */
  get foreign(): boolean { return !!this.e?.lines.some(l => l.amountCurrency != null && !!l.currencyCode && Math.abs(Math.abs(l.amountCurrency) - (l.debit || l.credit)) >= 0.005); }
  get baseLabel(): string { return this.foreign ? 'base currency' : (this.e?.currencyCode ?? ''); }

  ngOnInit(): void {
    this.sub = this.activated.paramMap.subscribe(p => {
      const uuid = p.get('uuid');
      if (uuid) this.load(uuid);
    });
  }

  ngOnDestroy(): void { this.sub?.unsubscribe(); }

  load(uuid: string): void {
    this.loading = true;
    this.loadError = '';
    this.api.getEntry(uuid).subscribe({
      next: e => { this.loading = false; this.e = e; },
      error: err => { this.loading = false; this.e = null; this.loadError = glError(err, 'The journal entry could not be loaded.'); }
    });
  }

  amount(n: number): string { return money(n); }
  kind(k: string | null | undefined): string { return kindLabel(k); }

  confirmPost(): void {
    const e = this.e!;
    this.confirm.confirm({
      key: 'gl-je', header: 'Post entry', icon: 'pi pi-check', acceptLabel: 'Post', rejectLabel: 'Cancel',
      message: `Post ${e.entryNumber}? The balances move now and the entry can no longer be changed — only reversed.`,
      accept: () => this.post()
    });
  }

  post(): void {
    const e = this.e!;
    this.busy = 'post';
    this.api.postEntry(e.uuid).subscribe({
      next: r => {
        this.busy = null;
        this.toast('success', `${r?.entryNumber ?? e.entryNumber} is posted.`);
        if (r?.uuid && r.lines) this.e = { ...r, lines: r.lines ?? [] }; else this.load(e.uuid);
      },
      error: err => { this.busy = null; this.toast('error', glError(err, 'The entry could not be posted.')); }
    });
  }

  openReverse(): void {
    this.reverseReason = '';
    this.reverseDate = null;
    this.reverseError = '';
    this.reverseVisible = true;
  }

  reverse(): void {
    const e = this.e!;
    const reason = this.reverseReason.trim();
    if (!reason) { this.reverseError = 'Give the reason for the reversal.'; return; }
    if (reason.length > MAX_REVERSAL_REASON) { this.reverseError = `The reason can be at most ${MAX_REVERSAL_REASON} characters.`; return; }
    this.busy = 'reverse';
    this.reverseError = '';
    this.api.reverseEntry(e.uuid, { reason, reversalDate: this.reverseDate ? toIsoDate(this.reverseDate) : null }).subscribe({
      next: r => {
        this.busy = null;
        this.reverseVisible = false;
        const by = r?.reversal?.entryNumber;
        const groups = r?.reconcileGroupNumbers?.length ? ` Reconciled in ${r.reconcileGroupNumbers.join(', ')}.` : '';
        this.toast('success', `${e.entryNumber} is reversed${by ? ' by ' + by : ''}.${groups}`);
        if (r?.warning) this.messages.add({ severity: 'warn', summary: 'Note', detail: r.warning, life: 10000 });
        this.load(e.uuid);
      },
      error: err => { this.busy = null; this.reverseError = glError(err, 'The entry could not be reversed.'); }
    });
  }

  confirmDelete(): void {
    const e = this.e!;
    this.confirm.confirm({
      key: 'gl-je', header: 'Delete draft', icon: 'pi pi-trash', acceptLabel: 'Delete', rejectLabel: 'Cancel', acceptButtonStyleClass: 'p-button-danger',
      message: `Delete the draft ${e.entryNumber}? This cannot be undone.`,
      accept: () => {
        this.busy = 'delete';
        this.api.deleteEntry(e.uuid).subscribe({
          next: () => { this.busy = null; this.toast('success', `${e.entryNumber} is deleted.`); this.router.navigate([GL_JOURNAL_ROUTE]); },
          error: err => { this.busy = null; this.toast('error', glError(err, 'The draft could not be deleted.')); }
        });
      }
    });
  }

  private toast(severity: 'success' | 'error', detail: string): void {
    this.messages.add({ severity, summary: severity === 'success' ? 'Done' : 'Error', detail, life: severity === 'error' ? 8000 : 3000 });
  }
}
