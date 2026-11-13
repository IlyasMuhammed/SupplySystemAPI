import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { FLOW } from '../../../shared/flow';
import {
  KeptDemoRecord, OnboardingDemoService, SampleDataDeleteResult, SampleDataPlanModel, demoEntityLabel
} from '../../../services/onboarding-demo.service';
import { DemoRefsStore } from '../../../shared/components/demo-badge/demo-refs.store';

interface CountRow { key: string; label: string; count: number; }

/** Display order of the plan's keys; unknown keys follow. */
const ORDER = ['customers', 'vendors', 'products', 'openingStock', 'saleOrders', 'purchaseOrders'];

export function countRows(counts: Record<string, number> | null | undefined): CountRow[] {
  const entries = Object.entries(counts ?? {});
  const rank = (k: string) => { const i = ORDER.indexOf(k); return i < 0 ? ORDER.length : i; };
  return entries
    .sort(([a], [b]) => rank(a) - rank(b))
    .map(([key, count]) => ({ key, label: demoEntityLabel(key), count: Number(count) || 0 }));
}

/**
 * A50 tasks 309–311 (D-27): load a small set of demo customers, suppliers, products and orders, all tagged "Demo",
 * and remove them in one click. Records real data now uses are kept and listed. SAMPLE_DATA_MANAGE.
 */
@Component({
  selector: 'app-sample-data-manager',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, DialogModule, ToastModule, ...FLOW],
  providers: [MessageService],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Settings', 'Organization', 'Sample data']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="Load sample data" icon="pi pi-download" [disabled]="!canLoad || working" (onClick)="loadVisible = true" data-testid="load"></p-button>
    <p-button label="Delete sample data" icon="pi pi-trash" severity="danger" [text]="true" [disabled]="!plan?.hasDemo || working"
              (onClick)="deleteVisible = true" data-testid="delete"></p-button>
    <span class="sf-end">
      <p-button label="Refresh" icon="pi pi-refresh" [text]="true" [loading]="loading" (onClick)="load()"></p-button>
    </span>
  </div>
  <div class="sf-body sdm">
    <div class="sf-lr-head">
      <div>
        <h1>Sample data</h1>
        <div class="sf-sub">Try the system with demo customers, suppliers, products and orders. Each is marked "Demo" and can be removed in one click.</div>
      </div>
    </div>

    @if (loading && !plan) {
      <div class="sf-card sf-empty" data-testid="sdm-loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
    } @else if (error) {
      <div class="sf-card sf-empty sdm-err" data-testid="sdm-error">
        <i class="pi pi-exclamation-circle"></i> {{ error }}
        <div><p-button label="Try again" icon="pi pi-refresh" [text]="true" (onClick)="load()"></p-button></div>
      </div>
    } @else if (plan) {
      <section class="sf-card" data-testid="sdm-status">
        <div class="sf-ch">
          <h2>Status</h2>
          @if (plan.hasDemo) { <span class="sf-pill vi" data-testid="sdm-loaded">Sample data loaded</span> }
          @else { <span class="sf-pill" data-testid="sdm-none">No sample data</span> }
        </div>
        @if (plan.hasDemo && plan.loadedAt) { <p class="sdm-mu">Loaded {{ plan.loadedAt | date:'d MMM yyyy, HH:mm' }}.</p> }
        @if (plan.reason && !canLoad) { <p class="sdm-mu" data-testid="sdm-reason"><i class="pi pi-info-circle"></i> {{ plan.reason }}</p> }
        @for (n of plan.notes ?? []; track n) { <p class="sdm-note"><i class="pi pi-exclamation-triangle"></i> {{ n }}</p> }
      </section>

      <div class="sf-row">
        <section class="sf-card" data-testid="sdm-plan">
          <div class="sf-ch"><h2>What a load creates</h2></div>
          @if (!planRows.length || planTotal === 0) {
            <div class="sf-empty sdm-empty-sm">Nothing to load: no module that provides sample data is switched on.</div>
          } @else {
            <ul class="sdm-list">
              @for (r of planRows; track r.key) {
                <li [attr.data-testid]="'plan-' + r.key"><span>{{ r.label }}</span><b>{{ r.count | number }}</b></li>
              }
            </ul>
          }
        </section>
        <section class="sf-card" data-testid="sdm-current">
          <div class="sf-ch"><h2>Demo records now</h2></div>
          @if (!currentTotal) {
            <div class="sf-empty sdm-empty-sm">None.</div>
          } @else {
            <ul class="sdm-list">
              @for (r of currentRows; track r.key) {
                @if (r.count) { <li [attr.data-testid]="'current-' + r.key"><span>{{ r.label }}</span><b>{{ r.count | number }}</b></li> }
              }
            </ul>
          }
        </section>
      </div>
    }

    @if (deleteResult) {
      <section class="sf-card" data-testid="sdm-result">
        <div class="sf-ch"><h2>Last clean-up</h2></div>
        <p class="sdm-mu">Removed {{ deleteTotal | number }} demo record{{ deleteTotal === 1 ? '' : 's' }}.</p>
        @if (deleteResult.kept.length) {
          <div class="sdm-kept" data-testid="sdm-kept">
            <p class="sdm-kept-h"><i class="pi pi-lock"></i> Kept {{ deleteResult.kept.length }} because real data uses them</p>
            <ul class="sdm-kept-list">
              @for (k of deleteResult.kept; track k.entityType + k.entityRef) {
                <li [attr.data-testid]="'kept-' + k.entityRef">
                  <span class="sf-pill">{{ entityLabel(k.entityType) }}</span>
                  <b>{{ k.name || k.entityRef }}</b>
                  @if (k.reason) { <span class="sdm-mu">— {{ k.reason }}</span> }
                </li>
              }
            </ul>
            <p class="sdm-mu">They still show the Demo badge. Remove what uses them, then delete sample data again.</p>
          </div>
        }
        @for (n of deleteResult.notes ?? []; track n) { <p class="sdm-mu" data-testid="sdm-delete-note"><i class="pi pi-info-circle"></i> {{ n }}</p> }
      </section>
    }
  </div>
</sf-page>

<p-dialog [(visible)]="loadVisible" [modal]="true" header="Load sample data?" styleClass="sdlg"
          [style]="{ width: '460px' }" [breakpoints]="{ '640px': '94vw' }" data-testid="load-dialog">
  <p class="sdm-dlg-p">These demo records will be created in your organization:</p>
  <ul class="sdm-list">
    @for (r of planRows; track r.key) {
      @if (r.count) { <li><span>{{ r.label }}</span><b>{{ r.count | number }}</b></li> }
    }
  </ul>
  <p class="sdm-mu">Every record's name ends in "(Demo)". You can delete them all later from this page.</p>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" [disabled]="working" (onClick)="loadVisible = false"></p-button>
    <p-button label="Load sample data" icon="pi pi-download" [loading]="working" (onClick)="confirmLoad()" data-testid="load-confirm"></p-button>
  </ng-template>
</p-dialog>

<p-dialog [(visible)]="deleteVisible" [modal]="true" header="Delete sample data?" styleClass="sdlg"
          [style]="{ width: '460px' }" [breakpoints]="{ '640px': '94vw' }" data-testid="delete-dialog">
  <div class="sdm-warn"><i class="pi pi-exclamation-triangle"></i>
    <span>All demo records are deleted, newest first. This cannot be undone. A demo record that your real data now uses
    (for example a demo customer on a real invoice) is kept, and listed afterwards.</span></div>
  @if (currentTotal) {
    <ul class="sdm-list">
      @for (r of currentRows; track r.key) {
        @if (r.count) { <li><span>{{ r.label }}</span><b>{{ r.count | number }}</b></li> }
      }
    </ul>
  }
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" [disabled]="working" (onClick)="deleteVisible = false"></p-button>
    <p-button label="Delete sample data" icon="pi pi-trash" severity="danger" [loading]="working" (onClick)="confirmDelete()" data-testid="delete-confirm"></p-button>
  </ng-template>
</p-dialog>
  `,
  styles: [`
    .sdm { max-width: 980px; }
    .sdm .sf-card { display: flex; flex-direction: column; gap: .5rem; }
    .sdm-mu { margin: 0; font-size: .84rem; color: var(--sms-text-muted); }
    .sdm-note { margin: 0; font-size: .82rem; color: var(--sms-warn); }
    .sdm-err { color: var(--sms-danger); }
    .sdm-empty-sm { padding: 1rem; }
    .sdm-list { list-style: none; margin: .25rem 0; padding: 0; }
    .sdm-list li { display: flex; justify-content: space-between; gap: 1rem; padding: .45rem .25rem; border-bottom: 1px solid var(--sms-border-soft); font-size: .875rem; color: var(--sms-text); }
    .sdm-list li:last-child { border-bottom: none; }
    .sdm-list b { font-variant-numeric: tabular-nums; }
    .sdm-kept { border: 1px solid color-mix(in srgb, var(--sms-warn) 35%, transparent); background: var(--sms-warn-soft); border-radius: .6rem; padding: .75rem .9rem; display: flex; flex-direction: column; gap: .4rem; }
    .sdm-kept-h { margin: 0; font-weight: 700; color: var(--sms-warn); font-size: .86rem; }
    .sdm-kept-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: .3rem; font-size: .84rem; color: var(--sms-text); }
    .sdm-kept-list li { display: flex; flex-wrap: wrap; gap: .4rem; align-items: baseline; }
    .sdm-warn { display: flex; gap: .6rem; align-items: flex-start; padding: .7rem .8rem; border-radius: .5rem; background: var(--sms-danger-soft); color: var(--sms-danger); font-size: .84rem; line-height: 1.5; margin-bottom: .6rem; }
    .sdm-dlg-p { margin: 0 0 .4rem; font-size: .875rem; color: var(--sms-text); }
  `]
})
export class SampleDataManagerComponent implements OnInit {
  plan: SampleDataPlanModel | null = null;
  loading = false;
  error: string | null = null;
  working = false;
  loadVisible = false;
  deleteVisible = false;
  deleteResult: SampleDataDeleteResult | null = null;

  constructor(
    private api: OnboardingDemoService,
    private refs: DemoRefsStore,
    private messages: MessageService
  ) {}

  ngOnInit(): void { this.load(); }

  entityLabel(t: string): string { return demoEntityLabel(t).replace(/s$/, ''); }

  get planRows(): CountRow[] { return countRows(this.plan?.counts); }
  get currentRows(): CountRow[] { return countRows(this.plan?.current); }
  get planTotal(): number { return this.planRows.reduce((n, r) => n + r.count, 0); }
  get currentTotal(): number { return this.currentRows.filter(r => r.key !== 'openingStock').reduce((n, r) => n + r.count, 0); }
  get deleteTotal(): number {
    const r = this.deleteResult;
    if (!r) return 0;
    return r.total ?? Object.values(r.deleted ?? {}).reduce((n, v) => n + (Number(v) || 0), 0);
  }

  get canLoad(): boolean {
    const p = this.plan;
    if (!p) return false;
    return (p.canLoad ?? !p.hasDemo) && this.planTotal > 0;
  }

  load(): void {
    this.loading = true;
    this.error = null;
    this.api.getSampleData().subscribe({
      next: res => { this.loading = false; this.plan = res?.result ?? null; if (!this.plan) this.error = 'Sample data is not available.'; },
      error: err => { this.loading = false; this.error = err?.error?.message || 'Sample data could not be loaded.'; }
    });
  }

  confirmLoad(): void {
    if (this.working) return;
    this.working = true;
    this.api.loadSampleData().subscribe({
      next: res => {
        this.working = false;
        this.loadVisible = false;
        this.deleteResult = null;
        this.refs.invalidate();
        const total = res?.result?.total ?? countRows(res?.result?.created).reduce((n, r) => n + r.count, 0);
        this.messages.add({ severity: 'success', summary: 'Sample data loaded', detail: `${total} demo records were created.` });
        (res?.result?.notes ?? []).forEach(n => this.messages.add({ severity: 'warn', summary: 'Note', detail: n, life: 8000 }));
        this.load();
      },
      error: err => {
        this.working = false;
        this.loadVisible = false;
        const detail = err?.status === 409
          ? (err?.error?.message || 'Sample data is already loaded. Remove it first.')
          : (err?.error?.message || 'Sample data could not be loaded.');
        this.messages.add({ severity: 'error', summary: 'Not loaded', detail, life: 8000 });
        this.load();
      }
    });
  }

  confirmDelete(): void {
    if (this.working) return;
    this.working = true;
    this.api.deleteSampleData().subscribe({
      next: res => {
        this.working = false;
        this.deleteVisible = false;
        this.refs.invalidate();
        this.deleteResult = {
          deleted: res?.result?.deleted ?? {}, total: res?.result?.total, kept: res?.result?.kept ?? [], notes: res?.result?.notes ?? []
        };
        const kept: KeptDemoRecord[] = this.deleteResult.kept;
        this.messages.add(kept.length
          ? { severity: 'warn', summary: 'Partly removed', detail: `${this.deleteTotal} removed; ${kept.length} kept because real data uses them.` }
          : { severity: 'success', summary: 'Sample data removed', detail: `${this.deleteTotal} demo records were deleted.` });
        this.load();
      },
      error: err => {
        this.working = false;
        this.deleteVisible = false;
        this.messages.add({ severity: 'error', summary: 'Not removed', detail: err?.error?.message || 'Sample data could not be removed.' });
        this.load();
      }
    });
  }
}
