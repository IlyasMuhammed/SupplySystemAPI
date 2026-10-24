import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { catchError, of } from 'rxjs';

import { AccountingService, FiscalYear } from '../../../../services/accounting.service';
import {
  GL_JOURNAL_PERMS, GL_JOURNAL_ROUTE, GlJournalService, JOURNAL_STATUSES, JournalEntryListItem, JournalType
} from '../../../../services/gl-journal.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { glError, money } from '../gl.shared';
import { journalStatusTone, sourceLabel } from '../journal.shared';

/**
 * A47 C6 (tasks 251–255) — `/gl/journal-entries`: every journal entry (manual, gateway A44 journals with their JE-
 * numbers, opening and closing entries), filtered by type, period, status, date range and text, paged on the server;
 * status pills Draft (blue) / Posted (green) / Reversed (red). "+ Manual Entry" for GL_JOURNAL_CREATE.
 */
@Component({
  selector: 'app-gl-journal-entries',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, InputTextModule, SelectModule, TableModule, TooltipModule, ...FLOW],
  styleUrls: ['../gl.scss'],
  template: `
<sf-page [crumbs]="['Finance', 'General Ledger', 'Journal Entries']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canCreate" label="Manual Entry" icon="pi pi-plus" [routerLink]="[route, 'new']" data-testid="new-entry"></p-button>
    <p-button icon="pi pi-refresh" label="Refresh" [text]="true" (onClick)="load(page)" data-testid="refresh"></p-button>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head">
      <div><h1>Journal entries <span class="sf-count">{{ total }}</span></h1>
        <div class="sf-sub">Every entry in the general ledger — entered by hand or posted from invoices, payments and bills</div></div>
      <div class="sf-filters">
        <button type="button" class="sf-chip" [class.on]="!status" (click)="setStatus(null)" data-testid="chip-all">All</button>
        <button type="button" class="sf-chip" *ngFor="let s of statuses" [class.on]="status === s" (click)="setStatus(s)"
                [attr.data-testid]="'chip-' + s">{{ s }}</button>
      </div>
    </div>

    <div class="sf-filters je-filters">
      <p-select [options]="typeOptions" [(ngModel)]="typeUuid" (ngModelChange)="reload()" optionLabel="label" optionValue="value"
                appendTo="body" data-testid="type-filter"></p-select>
      <p-select [options]="periodOptions" [(ngModel)]="periodUuid" (ngModelChange)="reload()" optionLabel="label" optionValue="value"
                [filter]="true" filterBy="label" appendTo="body" data-testid="period-filter"></p-select>
      <p-select [options]="statusOptions" [(ngModel)]="status" (ngModelChange)="reload()" optionLabel="label" optionValue="value"
                appendTo="body" data-testid="status-filter"></p-select>
      <input pInputText type="date" [(ngModel)]="from" (change)="reload()" aria-label="From" data-testid="from" />
      <input pInputText type="date" [(ngModel)]="to" (change)="reload()" aria-label="To" data-testid="to" />
      <input pInputText class="sf-search" [(ngModel)]="search" (ngModelChange)="onSearch()" placeholder="Number, reference, description…" data-testid="search" />
      <p-button icon="pi pi-filter-slash" [text]="true" severity="secondary" pTooltip="Clear filters" (onClick)="clearFilters()" data-testid="clear"></p-button>
    </div>

    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i>
      <div>{{ loadError }} <a href="" (click)="$event.preventDefault(); load(page)">Try again</a></div></div>

    <section class="sf-card p0">
      <div class="gl-scroll">
        <p-table [value]="rows" [lazy]="true" (onLazyLoad)="onLazyLoad($event)" [loading]="loading" [paginator]="true" [rows]="pageSize"
                 [totalRecords]="total" [first]="(page - 1) * pageSize" [rowsPerPageOptions]="[25, 50, 100]" dataKey="uuid"
                 styleClass="p-datatable-sm" [tableStyle]="{ 'min-width': '56rem' }" data-testid="entries">
          <ng-template pTemplate="header">
            <tr><th>Entry #</th><th>Type</th><th>Date</th><th>Description</th><th class="gl-num">Amount</th><th>Status</th><th style="width:5rem"></th></tr>
          </ng-template>
          <ng-template pTemplate="body" let-e>
            <tr [attr.data-testid]="'row-' + e.entryNumber">
              <td><a class="sf-mono" [routerLink]="[route, e.uuid]">{{ e.entryNumber }}</a>
                <div class="sf-mu sf-sm">{{ source(e) }}</div></td>
              <td>{{ e.journalType?.name || e.journalType?.code || '—' }}</td>
              <td class="je-nowrap">{{ e.entryDate | date:'dd MMM yyyy' }}</td>
              <td class="je-desc">{{ e.description || e.reference || '' }}
                <span class="sf-mu sf-sm" *ngIf="e.isReversal"> · reversal</span></td>
              <td class="gl-num">{{ amount(e.totalDebit) }} <span class="sf-mu sf-sm">{{ e.currencyCode || '' }}</span></td>
              <td><span class="sf-pill" [ngClass]="tone(e.status)" [attr.data-testid]="'status-' + e.entryNumber">{{ e.status }}</span></td>
              <td>
                <p-button *ngIf="e.status === 'Draft' && canEdit && e.isManual !== false; else viewBtn" label="Edit" [text]="true" size="small"
                          [routerLink]="[route, e.uuid, 'edit']" [attr.data-testid]="'edit-' + e.entryNumber"></p-button>
                <ng-template #viewBtn><p-button label="View" [text]="true" size="small" [routerLink]="[route, e.uuid]"
                          [attr.data-testid]="'view-' + e.entryNumber"></p-button></ng-template>
              </td>
            </tr>
          </ng-template>
          <ng-template pTemplate="emptymessage">
            <tr><td colspan="7" class="sf-empty" data-testid="empty">
              {{ hasFilters ? 'No journal entry matches the filters.' : 'No journal entry yet. Posted invoices, payments and bills appear here, and you can enter one by hand.' }}</td></tr>
          </ng-template>
        </p-table>
      </div>
    </section>
  </div>
</sf-page>`,
  styles: [`
    .je-filters { flex-wrap: wrap; }
    .je-filters input[type=date] { width: 10rem; }
    .je-nowrap { white-space: nowrap; }
    .je-desc { max-width: 24rem; }
  `]
})
export class GlJournalEntriesComponent implements OnInit, OnDestroy {
  private readonly api = inject(GlJournalService);
  private readonly accounting = inject(AccountingService);
  private readonly auth = inject(AuthService);
  private readonly activated = inject(ActivatedRoute, { optional: true });

  readonly route = GL_JOURNAL_ROUTE;
  readonly statuses = JOURNAL_STATUSES;
  readonly statusOptions = [{ label: 'Every status', value: null as string | null }, ...JOURNAL_STATUSES.map(s => ({ label: s, value: s as string | null }))];
  typeOptions: { label: string; value: string | null }[] = [{ label: 'Every type', value: null }];
  periodOptions: { label: string; value: string | null }[] = [{ label: 'Every period', value: null }];

  rows: JournalEntryListItem[] = [];
  total = 0;
  page = 1;
  pageSize = 25;
  loading = false;
  loadError = '';

  typeUuid: string | null = null;
  periodUuid: string | null = null;
  status: string | null = null;
  from = '';
  to = '';
  search = '';
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  get canCreate(): boolean { return this.auth.hasPermission(GL_JOURNAL_PERMS.CREATE); }
  get canEdit(): boolean { return this.auth.hasPermission(GL_JOURNAL_PERMS.EDIT); }
  get hasFilters(): boolean { return !!(this.typeUuid || this.periodUuid || this.status || this.from || this.to || this.search.trim()); }

  ngOnInit(): void {
    // ?status=Draft (e.g. from the period-close drafts warning) / ?periodUuid=…
    const qp = this.activated?.snapshot?.queryParamMap;
    if (qp?.get('status')) this.status = qp.get('status');
    if (qp?.get('periodUuid')) this.periodUuid = qp.get('periodUuid');
    this.api.getJournalTypes().pipe(catchError(() => of([] as JournalType[]))).subscribe(list => {
      this.typeOptions = [{ label: 'Every type', value: null },
        ...[...list].sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0)).map(t => ({ label: `${t.code} — ${t.name}`, value: t.uuid as string | null }))];
    });
    this.accounting.getFiscalYears().pipe(catchError(() => of(null))).subscribe(r => {
      const years: FiscalYear[] = [...(r?.result ?? [])].sort((a, b) => b.startDate.localeCompare(a.startDate));
      this.periodOptions = [{ label: 'Every period', value: null },
        ...years.flatMap(y => [...(y.periods ?? [])].sort((a, b) => a.periodNumber - b.periodNumber)
          .map(p => ({ label: `${p.periodName} · ${y.fiscalYearName}`, value: p.uuid as string | null })))];
    });
    // The table's first lazy load fetches page 1.
  }

  ngOnDestroy(): void { if (this.searchTimer) clearTimeout(this.searchTimer); }

  /** REV-18 — only the latest request's answer is shown (filters change faster than the server answers). */
  private requestSeq = 0;

  load(page = 1): void {
    const seq = ++this.requestSeq;
    this.loading = true;
    this.loadError = '';
    this.api.getEntries({
      journalTypeUuid: this.typeUuid, fiscalPeriodUuid: this.periodUuid, status: this.status, fromDate: this.from || null,
      toDate: this.to || null, search: this.search, page, pageSize: this.pageSize
    }).subscribe({
      next: r => {
        if (seq !== this.requestSeq) return;
        this.loading = false; this.rows = r.data; this.total = r.totalRecords; this.page = page;
      },
      error: err => {
        if (seq !== this.requestSeq) return;
        this.loading = false; this.rows = []; this.total = 0; this.loadError = glError(err, 'The journal entries could not be loaded.');
      }
    });
  }

  onLazyLoad(e: TableLazyLoadEvent): void {
    this.pageSize = e.rows ?? this.pageSize;
    this.load(Math.floor((e.first ?? 0) / this.pageSize) + 1);
  }

  reload(): void { this.load(1); }

  setStatus(s: string | null): void {
    this.status = s;
    this.reload();
  }

  onSearch(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.reload(), 400);
  }

  clearFilters(): void {
    this.typeUuid = null;
    this.periodUuid = null;
    this.status = null;
    this.from = '';
    this.to = '';
    this.search = '';
    this.reload();
  }

  tone(s: string): string { return journalStatusTone(s); }
  amount(n: number): string { return money(n); }
  source(e: JournalEntryListItem): string { return sourceLabel(e); }
}
