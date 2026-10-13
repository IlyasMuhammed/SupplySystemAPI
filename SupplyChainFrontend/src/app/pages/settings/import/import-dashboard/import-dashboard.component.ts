import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import {
  FEATURE_OPENING_BALANCE_IMPORT, IMPORT_PERMS, IMPORT_ROUTE, ImportProfile, ImportService, OPENING_DATE_ROUTE, SessionListItem,
  importErrorMessage, importErrorText, saveDownload
} from '../../../../services/import.service';
import { ModuleService } from '../../../../services/module.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW, FlowTone } from '../../../../shared/flow';
import { OPENING_DATE_MESSAGE, byImportOrder, entityIcon, sessionStatusPill } from '../import.shared';

export interface EntityCardVm {
  profile: ImportProfile;
  icon: string;
  last: string;
  lastTone: FlowTone;
  /** Why Import is not possible (opening date not set), else null. */
  blocked: string | null;
}

export interface RecentRow extends SessionListItem {
  statusLabel: string;
  statusTone: FlowTone;
}

/**
 * A40 — `/portal/pages/settings/import`: one card per importable entity in import order, the opening balances in their own
 * section (only with IMPORT_OPENING_BALANCE and FEATURE_OPENING_BALANCE_IMPORT), the migration workbook, and recent imports.
 * View models are built once per load, never in getters.
 */
@Component({
  selector: 'app-import-dashboard',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, TableModule, ToastModule, TooltipModule, ...FLOW],
  templateUrl: './import-dashboard.component.html',
  styleUrls: ['../import.scss'],
  providers: [MessageService]
})
export class ImportDashboardComponent implements OnInit {
  private readonly api = inject(ImportService);
  private readonly auth = inject(AuthService);
  private readonly modules = inject(ModuleService);
  private readonly messages = inject(MessageService);

  readonly importRoute = IMPORT_ROUTE;
  readonly openingDateRoute = OPENING_DATE_ROUTE;

  cards: EntityCardVm[] = [];
  openingCards: EntityCardVm[] = [];
  recent: RecentRow[] = [];
  isLoading = true;
  loadFailed = false;
  recentLoading = false;
  downloading = false;

  canUpload = false;
  openingAllowed = false;
  openingDate: string | null = null;

  ngOnInit(): void {
    this.canUpload = this.auth.hasPermission(IMPORT_PERMS.UPLOAD);
    this.openingAllowed = this.modules.isFeatureEnabled(FEATURE_OPENING_BALANCE_IMPORT)
      && this.auth.hasPermission(IMPORT_PERMS.OPENING_BALANCE);
    this.load();
  }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.api.getProfiles().subscribe({
      next: res => {
        this.isLoading = false;
        const profiles = [...(res?.result ?? [])].sort(byImportOrder);
        this.cards = profiles.filter(p => !p.isOpeningBalance).map(p => this.cardVm(p));
        const opening = this.openingAllowed ? profiles.filter(p => p.isOpeningBalance) : [];
        this.openingCards = opening.map(p => this.cardVm(p));
        if (opening.length) this.loadOpeningDate();
      },
      error: err => {
        this.isLoading = false;
        this.loadFailed = true;
        this.toast(importErrorText(err, 'Could not load the import profiles.'));
      }
    });
    this.loadRecent();
  }

  loadRecent(): void {
    if (!this.canUpload) { this.recent = []; return; }
    this.recentLoading = true;
    this.api.getSessions({ page: 1, pageSize: 10 }).subscribe({
      next: res => {
        this.recentLoading = false;
        this.recent = (res?.result?.data ?? []).map(s => {
          const pill = sessionStatusPill(s.status);
          return { ...s, statusLabel: pill.label, statusTone: pill.tone };
        });
      },
      error: () => { this.recentLoading = false; this.recent = []; }
    });
  }

  private loadOpeningDate(): void {
    this.api.getOpeningBalanceDate().subscribe({
      next: res => {
        this.openingDate = res?.result?.date ?? null;
        this.openingCards = this.openingCards.map(c => ({ ...c, blocked: this.openingDate ? null : OPENING_DATE_MESSAGE }));
      },
      // Unknown: let them try — the server refuses the import itself while the date is missing.
      error: () => { this.openingCards = this.openingCards.map(c => ({ ...c, blocked: null })); }
    });
  }

  private cardVm(p: ImportProfile): EntityCardVm {
    const done = (p.completedSessions ?? 0) > 0;
    const when = p.lastCompletedAt ? new Date(p.lastCompletedAt).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }) : '';
    const rows = p.lastImportedRows != null ? ` · ${p.lastImportedRows} rows` : '';
    return {
      profile: p,
      icon: entityIcon(p.entityType),
      last: done ? ['Last import', when].filter(Boolean).join(' ') + rows : 'Not imported yet',
      lastTone: done ? 'ok' : '',
      blocked: p.isOpeningBalance ? OPENING_DATE_MESSAGE : null
    };
  }

  downloadWorkbook(): void {
    if (this.downloading) return;
    this.downloading = true;
    this.api.downloadMigrationWorkbook().subscribe({
      next: res => { this.downloading = false; saveDownload(res, 'data-migration-workbook.xlsx'); },
      error: async err => {
        this.downloading = false;
        this.toast(await importErrorMessage(err, 'The migration workbook could not be downloaded.'));
      }
    });
  }

  private toast(detail: string): void {
    this.messages.add({ severity: 'error', summary: 'Error', detail, life: 8000 });
  }

  trackCard = (_: number, c: EntityCardVm) => c.profile.entityType;
}
