import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import {
  FEATURE_OPENING_BALANCE_IMPORT, IMPORT_PERMS, IMPORT_ROUTE, ImportProfile, ImportService, OPENING_DATE_ROUTE,
  importErrorMessage, importErrorText, saveDownload
} from '../../../../services/import.service';
import { ModuleService } from '../../../../services/module.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { OPENING_DATE_MESSAGE, byImportOrder, entityIcon } from '../import.shared';

export type ChecklistState = 'done' | 'next' | 'todo';

export interface ChecklistItem {
  profile: ImportProfile;
  icon: string;
  state: ChecklistState;
  /** ✓ / ◉ / ○ */
  symbol: string;
  detail: string;
  blocked: string | null;
}

const SYMBOLS: Record<ChecklistState, string> = { done: '✓', next: '◉', todo: '○' };

/**
 * A40 — `/portal/pages/settings/import/migration`: the go-live checklist. Every entity in import order: ✓ imported (counts,
 * View → its last completed session), ◉ the next one to do, ○ not started; opening balances in their own section (with
 * IMPORT_OPENING_BALANCE + the feature). Items are built once per load.
 */
@Component({
  selector: 'app-import-migration',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, ToastModule, ...FLOW],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Settings', 'Data import', 'Migration wizard']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canUpload" label="Download migration workbook" icon="pi pi-download" [loading]="downloading"
              (onClick)="downloadWorkbook()" data-testid="download-workbook"></p-button>
    <span class="sf-sep"></span>
    <p-button label="Data import" icon="pi pi-arrow-left" [text]="true" [routerLink]="importRoute"></p-button>
    <p-button label="Refresh" icon="pi pi-refresh" [text]="true" (onClick)="load()"></p-button>
  </div>

  <div class="sf-body">
    <div class="sf-lr-head">
      <div>
        <h1>Migration wizard <span class="sf-count" data-testid="done-count">{{ doneCount }} / {{ totalCount }}</span></h1>
        <div class="sf-sub">Moving from another system: import each list in this order, then the opening balances. The workbook holds one sheet per list.</div>
      </div>
    </div>

    <div class="sf-card sf-empty" *ngIf="isLoading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>

    <section class="sf-card" *ngIf="!isLoading" data-testid="master-list">
      <div class="sf-ch"><h2>Master data</h2></div>
      <ul class="imp-check">
        <li *ngFor="let i of items; trackBy: track" [ngClass]="i.state" [attr.data-testid]="'step-' + i.profile.entityType">
          <span class="imp-state" [attr.data-testid]="'state-' + i.profile.entityType">{{ i.symbol }}</span>
          <span class="imp-order">{{ i.profile.importOrder }}</span>
          <i [class]="i.icon"></i>
          <div class="imp-grow"><strong>{{ i.profile.name }}</strong><div class="sf-mu sf-sm">{{ i.detail }}</div></div>
          <p-button *ngIf="i.state === 'done'" label="View" [text]="true" size="small" (onClick)="view(i)"
                    [attr.data-testid]="'view-' + i.profile.entityType"></p-button>
          <p-button *ngIf="canUpload" [label]="i.state === 'done' ? 'Import again' : 'Import'" size="small" [text]="i.state !== 'next'"
                    [routerLink]="[importRoute, i.profile.entityType]" [attr.data-testid]="'import-' + i.profile.entityType"></p-button>
        </li>
      </ul>
    </section>

    <section class="sf-card" *ngIf="!isLoading && openingItems.length" data-testid="opening-list">
      <div class="sf-ch"><div><h2>Opening balances</h2><div class="sf-mu sf-sm">After all master data — stock and open balances at the opening balance date.</div></div></div>
      <ul class="imp-check">
        <li *ngFor="let i of openingItems; trackBy: track" [ngClass]="i.state" [attr.data-testid]="'step-' + i.profile.entityType">
          <span class="imp-state" [attr.data-testid]="'state-' + i.profile.entityType">{{ i.symbol }}</span>
          <span class="imp-order">{{ i.profile.importOrder }}</span>
          <i [class]="i.icon"></i>
          <div class="imp-grow"><strong>{{ i.profile.name }}</strong><div class="sf-mu sf-sm">{{ i.detail }}</div>
            <div class="imp-blocked sf-sm" *ngIf="i.blocked" [attr.data-testid]="'blocked-' + i.profile.entityType">
              {{ i.blocked }} <a [routerLink]="openingDateRoute">Set it now</a></div>
          </div>
          <p-button *ngIf="i.state === 'done'" label="View" [text]="true" size="small" (onClick)="view(i)"
                    [attr.data-testid]="'view-' + i.profile.entityType"></p-button>
          <p-button *ngIf="canUpload" label="Import" size="small" [text]="i.state !== 'next'" [disabled]="!!i.blocked"
                    [routerLink]="i.blocked ? null : [importRoute, i.profile.entityType]" [attr.data-testid]="'import-' + i.profile.entityType"></p-button>
        </li>
      </ul>
    </section>
  </div>
</sf-page>
  `,
  styleUrls: ['../import.scss'],
  providers: [MessageService]
})
export class ImportMigrationComponent implements OnInit {
  private readonly api = inject(ImportService);
  private readonly auth = inject(AuthService);
  private readonly modules = inject(ModuleService);
  private readonly router = inject(Router);
  private readonly messages = inject(MessageService);

  readonly importRoute = IMPORT_ROUTE;
  readonly openingDateRoute = OPENING_DATE_ROUTE;

  items: ChecklistItem[] = [];
  openingItems: ChecklistItem[] = [];
  doneCount = 0;
  totalCount = 0;
  isLoading = true;
  downloading = false;
  canUpload = false;
  openingAllowed = false;
  private openingDateSet: boolean | null = null;

  ngOnInit(): void {
    this.canUpload = this.auth.hasPermission(IMPORT_PERMS.UPLOAD);
    this.openingAllowed = this.modules.isFeatureEnabled(FEATURE_OPENING_BALANCE_IMPORT)
      && this.auth.hasPermission(IMPORT_PERMS.OPENING_BALANCE);
    this.load();
  }

  load(): void {
    this.isLoading = true;
    this.api.getProfiles().subscribe({
      next: res => {
        this.isLoading = false;
        const all = [...(res?.result ?? [])].sort(byImportOrder);
        const master = all.filter(p => !p.isOpeningBalance);
        const opening = this.openingAllowed ? all.filter(p => p.isOpeningBalance) : [];
        this.build(master, opening);
        if (opening.length && this.openingDateSet === null) {
          this.api.getOpeningBalanceDate().subscribe({
            next: r => { this.openingDateSet = !!r?.result?.date; this.build(master, opening); },
            error: () => { this.openingDateSet = true; this.build(master, opening); }
          });
        }
      },
      error: err => {
        this.isLoading = false;
        this.messages.add({ severity: 'error', summary: 'Error', detail: importErrorText(err, 'Could not load the import profiles.'), life: 8000 });
      }
    });
  }

  /** ◉ is the first entity not imported yet — across master data first, then the opening balances. */
  private build(master: ImportProfile[], opening: ImportProfile[]): void {
    let nextGiven = false;
    const item = (p: ImportProfile): ChecklistItem => {
      const done = (p.completedSessions ?? 0) > 0;
      const state: ChecklistState = done ? 'done' : nextGiven ? 'todo' : 'next';
      if (state === 'next') nextGiven = true;
      const when = p.lastCompletedAt ? new Date(p.lastCompletedAt).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }) : '';
      const detail = done
        ? `${p.lastImportedRows ?? 0} rows imported${when ? ' on ' + when : ''} · ${p.completedSessions} import${p.completedSessions === 1 ? '' : 's'}`
        : (p.description || 'Not started');
      return {
        profile: p, icon: entityIcon(p.entityType), state, symbol: SYMBOLS[state], detail,
        blocked: p.isOpeningBalance && this.openingDateSet === false ? OPENING_DATE_MESSAGE : null
      };
    };
    this.items = master.map(item);
    this.openingItems = opening.map(item);
    const all = [...this.items, ...this.openingItems];
    this.totalCount = all.length;
    this.doneCount = all.filter(i => i.state === 'done').length;
  }

  /** View: the newest completed session of that entity. */
  view(i: ChecklistItem): void {
    this.api.getSessions({ entityType: i.profile.entityType, page: 1, pageSize: 20 }).subscribe({
      next: res => {
        const s = (res?.result?.data ?? []).find(x => x.status === 'COMPLETED' || x.status === 'COMPLETED_WITH_ERRORS');
        if (s) this.router.navigate([IMPORT_ROUTE, 'sessions', s.uuid]);
        else this.messages.add({ severity: 'info', summary: 'Not found', detail: 'No completed import of this list is visible to you.', life: 5000 });
      },
      error: err => this.messages.add({ severity: 'error', summary: 'Error', detail: importErrorText(err, 'Could not load the imports.'), life: 8000 })
    });
  }

  downloadWorkbook(): void {
    if (this.downloading) return;
    this.downloading = true;
    this.api.downloadMigrationWorkbook().subscribe({
      next: res => { this.downloading = false; saveDownload(res, 'data-migration-workbook.xlsx'); },
      error: async err => {
        this.downloading = false;
        this.messages.add({ severity: 'error', summary: 'Error', detail: await importErrorMessage(err, 'The migration workbook could not be downloaded.'), life: 8000 });
      }
    });
  }

  track = (_: number, i: ChecklistItem) => i.profile.entityType;
}
