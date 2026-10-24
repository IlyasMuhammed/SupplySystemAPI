import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';

import { AccountingService, GL_ROUTE, GlDashboard } from '../../../../services/accounting.service';
import { FLOW } from '../../../../shared/flow';
import { glError } from '../gl.shared';

export const GL_LINKS = [
  { label: 'Chart of Accounts', icon: 'pi pi-sitemap', path: 'accounts' },
  // A47 D-20 — the journal engine.
  { label: 'Journal Entries', icon: 'pi pi-list', path: 'journal-entries' },
  { label: 'Trial Balance', icon: 'pi pi-table', path: 'trial-balance' },
  { label: 'Reconciliation', icon: 'pi pi-link', path: 'reconciliation' },
  { label: 'Period Close', icon: 'pi pi-lock', path: 'period-close' },
  { label: 'Journal Types', icon: 'pi pi-tags', path: 'journal-types' },
  { label: 'Fiscal Years', icon: 'pi pi-calendar', path: 'fiscal-years' },
  { label: 'Defaults', icon: 'pi pi-sliders-h', path: 'defaults' },
  { label: 'Opening Balances', icon: 'pi pi-book', path: 'opening-balances' },
  { label: 'Integrations', icon: 'pi pi-link', path: 'integrations' },
  { label: 'Setup Status', icon: 'pi pi-list-check', path: 'setup-status' }
];

/**
 * A42 (0641–0643) — `/gl`: totals per root type (sums of leaf current balances) and links to the GL pages. Opens only
 * once setup is Complete (route guard; the API's 403 FINANCE_SETUP_REQUIRED redirects too).
 */
@Component({
  selector: 'app-gl-dashboard',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, ...FLOW],
  styleUrls: ['../gl.scss'],
  template: `
<sf-page [crumbs]="['Finance', 'General Ledger']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="Chart of accounts" icon="pi pi-sitemap" [routerLink]="[route, 'accounts']"></p-button>
    <span class="sf-sep"></span>
    <p-button icon="pi pi-refresh" label="Refresh" [text]="true" (onClick)="load()" data-testid="refresh"></p-button>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>General ledger</h1>
      <div class="sf-sub">
        <ng-container *ngIf="data?.fiscalYear">{{ data!.fiscalYear!.name }} · {{ data!.fiscalYear!.startDate | date:'d MMM y' }} – {{ data!.fiscalYear!.endDate | date:'d MMM y' }} · </ng-container>
        <ng-container *ngIf="data">{{ data.accountCount | number }} accounts · <ng-container *ngIf="data.currencyCode">amounts in {{ data.currencyCode }}</ng-container></ng-container>
      </div></div></div>

    <div class="sf-card sf-empty" *ngIf="loading && !data"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

    <div class="sf-grid sf-tiles" *ngIf="data" data-testid="tiles">
      <div class="sf-tile" *ngFor="let t of tiles" [attr.data-testid]="'tile-' + t.key">
        <span class="l">{{ t.label }}</span><span class="v">{{ t.value | number:'1.2-2' }}</span><span class="s">{{ data.currencyCode || '' }}</span>
      </div>
    </div>

    <section class="sf-card">
      <div class="sf-ch"><h2>General ledger</h2></div>
      <nav class="gl-links" data-testid="links">
        <a *ngFor="let l of links" [routerLink]="[route, l.path]"><i class="pi" [ngClass]="l.icon"></i>{{ l.label }}</a>
      </nav>
    </section>
  </div>
</sf-page>`
})
export class GlDashboardComponent implements OnInit {
  private readonly api = inject(AccountingService);

  readonly route = GL_ROUTE;
  readonly links = GL_LINKS;
  data: GlDashboard | null = null;
  loading = false;
  loadError = '';

  get tiles() {
    const d = this.data;
    if (!d) return [];
    return [
      { key: 'assets', label: 'Total assets', value: d.totalAssets },
      { key: 'liabilities', label: 'Total liabilities', value: d.totalLiabilities },
      { key: 'equity', label: 'Total equity', value: d.totalEquity },
      { key: 'revenue', label: 'Revenue', value: d.totalRevenue },
      { key: 'expenses', label: 'Expenses', value: d.totalExpenses }
    ];
  }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.loadError = '';
    this.api.getDashboard().subscribe({
      next: r => { this.loading = false; this.data = r?.result ?? null; },
      error: err => { this.loading = false; this.loadError = glError(err, 'The GL dashboard could not be loaded.'); }
    });
  }
}
