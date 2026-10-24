import { Component, Input, OnChanges, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';

import { AccountLedger, GL_JOURNAL_ROUTE, GlAccountRef, GlJournalService, LedgerLine } from '../../../../services/gl-journal.service';
import { glError, money } from '../gl.shared';
import { journalStatusTone, parens } from '../journal.shared';

/**
 * A47 C9 (task 353) — one account's ledger: every posted (and reversed) line in date order with the running balance
 * (debit − credit, the opening included), paged on the server. Embedded in the trial balance's drill-down drawer.
 */
@Component({
  selector: 'app-gl-account-ledger',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, TableModule],
  styleUrls: ['../gl.scss'],
  template: `
<div class="gl-note er" *ngIf="error" data-testid="ledger-error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
<div class="lg-head" *ngIf="ledger" data-testid="ledger-head">
  <div><span class="l">Opening</span><span class="v" data-testid="ledger-opening">{{ signed(ledger.openingBalance) }}</span></div>
  <div *ngIf="ledger.totalDebit != null"><span class="l">Debits</span><span class="v">{{ amount(ledger.totalDebit!) }}</span></div>
  <div *ngIf="ledger.totalCredit != null"><span class="l">Credits</span><span class="v">{{ amount(ledger.totalCredit!) }}</span></div>
  <div><span class="l">Closing</span><span class="v" data-testid="ledger-closing">{{ signed(ledger.closingBalance) }}</span></div>
</div>
<div class="gl-scroll">
  <p-table [value]="rows" [lazy]="true" (onLazyLoad)="onLazyLoad($event)" [loading]="loading" [paginator]="true" [rows]="pageSize"
           [totalRecords]="total" [first]="(page - 1) * pageSize" [rowsPerPageOptions]="[50, 100, 200]" dataKey="lineUuid"
           styleClass="p-datatable-sm" [tableStyle]="{ 'min-width': '40rem' }" data-testid="ledger">
    <ng-template pTemplate="header">
      <tr><th>Date</th><th>Entry</th><th>Description</th><th class="gl-num">Debit</th><th class="gl-num">Credit</th><th class="gl-num">Balance</th></tr>
    </ng-template>
    <ng-template pTemplate="body" let-l>
      <tr [attr.data-testid]="'lline-' + l.lineUuid">
        <td class="lg-nowrap">{{ l.entryDate | date:'dd MMM yyyy' }}</td>
        <td><a class="sf-mono" [routerLink]="[jeRoute, l.entry.uuid]">{{ l.entry.entryNumber }}</a>
          <span class="sf-pill lg-pill" *ngIf="l.status && l.status !== 'Posted'" [ngClass]="tone(l.status)">{{ l.status }}</span></td>
        <td>{{ l.description || l.reference || '' }}<span class="sf-mu sf-sm" *ngIf="l.partnerName"> · {{ l.partnerName }}</span></td>
        <td class="gl-num">{{ l.debit ? amount(l.debit) : '' }}</td>
        <td class="gl-num">{{ l.credit ? amount(l.credit) : '' }}</td>
        <td class="gl-num">{{ signed(l.runningBalance) }}</td>
      </tr>
    </ng-template>
    <ng-template pTemplate="emptymessage">
      <tr><td colspan="6" class="sf-empty" data-testid="ledger-empty">No posted line on this account in the period.</td></tr>
    </ng-template>
  </p-table>
</div>`,
  styles: [`
    .lg-head { display: grid; grid-template-columns: repeat(auto-fit, minmax(8rem, 1fr)); gap: .5rem 1rem; margin-bottom: .75rem; }
    .lg-head .l { display: block; font-size: .75rem; color: var(--sms-text-muted); }
    .lg-head .v { font-weight: 600; font-variant-numeric: tabular-nums; }
    .lg-nowrap { white-space: nowrap; }
    .lg-pill { margin-left: .35rem; }
  `]
})
export class GlAccountLedgerComponent implements OnChanges {
  private readonly api = inject(GlJournalService);

  @Input() account: GlAccountRef | null = null;
  @Input() fromDate: string | null = null;
  @Input() toDate: string | null = null;
  @Input() includeClosingEntries = false;

  readonly jeRoute = GL_JOURNAL_ROUTE;
  ledger: AccountLedger | null = null;
  rows: LedgerLine[] = [];
  total = 0;
  page = 1;
  pageSize = 50;
  loading = false;
  error = '';
  private ready = false;

  ngOnChanges(): void {
    this.ledger = null;
    this.rows = [];
    this.total = 0;
    // The table's first lazy load reads page 1; later input changes reload here.
    if (this.ready) this.load(1);
  }

  load(page = 1): void {
    if (!this.account) return;
    const uuid = this.account.uuid;
    this.loading = true;
    this.error = '';
    this.api.getAccountLedger(uuid, {
      fromDate: this.fromDate, toDate: this.toDate, includeClosingEntries: this.includeClosingEntries, page, pageSize: this.pageSize
    }).subscribe({
      next: l => {
        if (this.account?.uuid !== uuid) return;
        this.loading = false;
        this.ledger = l;
        this.rows = l.page.data;
        this.total = l.page.totalRecords;
        this.page = page;
      },
      error: err => { this.loading = false; this.rows = []; this.total = 0; this.error = glError(err, 'The account ledger could not be loaded.'); }
    });
  }

  onLazyLoad(e: TableLazyLoadEvent): void {
    this.ready = true;
    this.pageSize = e.rows ?? this.pageSize;
    this.load(Math.floor((e.first ?? 0) / this.pageSize) + 1);
  }

  amount(n: number): string { return money(n); }
  signed(n: number | null | undefined): string { return parens(n); }
  tone(s: string): string { return journalStatusTone(s); }
}
