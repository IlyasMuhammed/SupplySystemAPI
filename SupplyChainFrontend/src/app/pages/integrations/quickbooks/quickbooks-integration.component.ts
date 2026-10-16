import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TabsModule } from 'primeng/tabs';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { forkJoin, of } from 'rxjs';
import { catchError, skip } from 'rxjs/operators';

import {
  ConnectionStatusModel, IntegrationSettingsModel, PreflightResultModel, QuickBooksIntegrationService, SyncSummaryModel,
  qboErrorMessage
} from '../../../services/quickbooks-integration.service';
import { AuthService } from '../../service/auth.service';
import {
  A44_TABS, CONNECTION_STATUS_LABEL, PERM_MANAGE, PERM_SYNC, QBO_TAB_KEYS, QboTabKey, SetupStep, currentStep, needsReconnect,
  setupComplete, setupSteps, unlockedTabs
} from './quickbooks.shared';
import { LogsTabComponent, SyncItemFocus } from './logs-tab/logs-tab.component';
import { SyncControlTabComponent } from './sync-control-tab/sync-control-tab.component';
import { DefaultsTabComponent } from './defaults-tab/defaults-tab.component';
import { AccountsTabComponent } from './accounts-tab/accounts-tab.component';
import { CoverageTabComponent } from './coverage-tab/coverage-tab.component';
import { ConnectionTabComponent } from './connection-tab/connection-tab.component';
import { PreflightTabComponent } from './preflight-tab/preflight-tab.component';
import { MappingsTabComponent } from './mappings-tab/mappings-tab.component';
import { InitialSyncTabComponent } from './initial-sync-tab/initial-sync-tab.component';
import { MatchTabComponent } from './match-tab/match-tab.component';
import { SyncTabComponent } from './sync-tab/sync-tab.component';
import { ApiClientsTabComponent } from './api-clients-tab/api-clients-tab.component';
import { FLOW } from '../../../shared/flow';

interface TabDef {
  key: QboTabKey;
  label: string;
  icon: string;
}

const TABS: TabDef[] = [
  { key: 'connection',   label: 'Connection',     icon: 'pi pi-link' },
  { key: 'preflight',    label: 'Preflight',      icon: 'pi pi-verified' },
  { key: 'mappings',     label: 'Mappings',       icon: 'pi pi-arrow-right-arrow-left' },
  // A44 D-30
  { key: 'accounts',     label: 'Accounts',       icon: 'pi pi-sitemap' },
  { key: 'defaults',     label: 'Defaults',       icon: 'pi pi-tags' },
  { key: 'sync-control', label: 'Sync control',   icon: 'pi pi-sliders-h' },
  { key: 'initial-sync', label: 'Initial sync',   icon: 'pi pi-upload' },
  { key: 'match',        label: 'Match existing', icon: 'pi pi-link' },
  { key: 'sync',         label: 'Sync',           icon: 'pi pi-sync' },
  { key: 'coverage',     label: 'Coverage',       icon: 'pi pi-chart-bar' },
  { key: 'logs',         label: 'Logs',           icon: 'pi pi-history' },
  { key: 'api-clients',  label: 'API clients',    icon: 'pi pi-key' }
];

/**
 * Settings → QuickBooks Integration (plan §6.2). One page with a tab per job. Until setup is
 * complete the tabs open in order and a checklist shows where the organization is:
 * Connected → Preflight passed → Mappings saved → Initial sync loaded → Matching confirmed → Live.
 *
 * The page holds the connection, preflight, settings and summary the checklist is computed from;
 * each tab does its own work and reports back what changed.
 */
@Component({
  selector: 'app-quickbooks-integration',
  standalone: true,
  imports: [
    CommonModule, ButtonModule, TabsModule, TagModule, ToastModule, TooltipModule,
    ConnectionTabComponent, PreflightTabComponent, MappingsTabComponent, InitialSyncTabComponent, MatchTabComponent,
    SyncTabComponent, ApiClientsTabComponent,
    SyncControlTabComponent, DefaultsTabComponent, AccountsTabComponent, CoverageTabComponent, LogsTabComponent,
    ...FLOW
  ],
  templateUrl: './quickbooks-integration.component.html',
  styleUrls: ['./quickbooks-integration.component.scss'],
  providers: [MessageService]
})
export class QuickBooksIntegrationComponent implements OnInit {
  connection: ConnectionStatusModel | null = null;
  preflight: PreflightResultModel | null = null;
  settings: IntegrationSettingsModel | null = null;
  summary: SyncSummaryModel | null = null;

  isLoading = true;
  loadFailed = false;
  loadError = '';

  activeTab: QboTabKey = 'connection';
  steps: SetupStep[] = [];
  unlocked = new Set<QboTabKey>(['connection']);

  readonly statusLabel = CONNECTION_STATUS_LABEL;

  constructor(
    private service: QuickBooksIntegrationService,
    private authService: AuthService,
    private route: ActivatedRoute
  ) {}

  ngOnInit(): void {
    // Back from Intuit's sign-in: the Connection tab shows what happened.
    const q = this.route.snapshot.queryParamMap;
    const cameBack = q.has('result');
    // D-34 — ?tab=logs (the "QuickBooks Logs" menu entry), or any other tab, when it is open.
    const asked = q.get('tab') as QboTabKey | null;
    this.load(cameBack ? 'connection' : (asked && QBO_TAB_KEYS.includes(asked) ? asked : null));
    // The menu entry while the page is already open only changes the query string.
    this.route.queryParamMap?.pipe(skip(1), takeUntilDestroyed(this.destroyRef)).subscribe(p => {
      const tab = p.get('tab') as QboTabKey | null;
      if (tab && QBO_TAB_KEYS.includes(tab)) this.selectTab(tab);
    });
  }

  private readonly destroyRef = inject(DestroyRef);

  // ── D-34: a log row's "Sync item" opens the Sync tab on that record ─────────
  syncFocus: SyncItemFocus | null = null;

  onOpenSyncItem(focus: SyncItemFocus): void {
    this.syncFocus = focus;
    this.selectTab('sync');
  }

  // ── Permissions ─────────────────────────────────────────────────────────────

  get canManage(): boolean { return this.authService.hasPermission(PERM_MANAGE); }
  get canSync(): boolean { return this.authService.hasPermission(PERM_SYNC); }

  get tabs(): TabDef[] {
    return TABS.filter(t => t.key !== 'api-clients' || this.canManage);
  }

  // ── State ───────────────────────────────────────────────────────────────────

  get isComplete(): boolean { return setupComplete(this.steps); }
  get current(): SetupStep | null { return currentStep(this.steps); }
  get isConnected(): boolean { return !!this.connection?.isConnected; }
  get mode(): 'DryRun' | 'Live' { return this.settings?.mode ?? this.connection?.mode ?? 'DryRun'; }
  get mustReconnect(): boolean { return needsReconnect(this.connection); }

  isUnlocked(tab: QboTabKey): boolean { return this.unlocked.has(tab); }

  lockReason(tab: QboTabKey): string {
    if (this.isUnlocked(tab)) return '';
    const step = this.current;
    return step ? `Finish "${step.label}" first.` : '';
  }

  // ── Loading ─────────────────────────────────────────────────────────────────

  load(prefer: QboTabKey | null = null): void {
    this.isLoading = true;
    this.loadFailed = false;

    this.service.getConnection().subscribe({
      next: (connection) => {
        this.connection = connection;
        this.loadDependents(() => {
          this.isLoading = false;
          this.activeTab = this.pickTab(prefer);
        });
      },
      error: (err) => {
        this.isLoading = false;
        this.loadFailed = true;
        this.loadError = qboErrorMessage(err, 'The QuickBooks integration could not be loaded.');
      }
    });
  }

  /** Preflight, settings and counts, for everything after "Connected". Each may fail on its own. */
  private loadDependents(done?: () => void): void {
    const status = this.connection?.status;
    if (!this.connection || status === 'NotConnected') {
      this.preflight = null;
      this.settings = null;
      this.summary = null;
      this.recompute();
      done?.();
      return;
    }

    forkJoin({
      preflight: this.isConnected ? this.service.getPreflight().pipe(catchError(() => of(null))) : of(null),
      settings: this.service.getSettings().pipe(catchError(() => of(null))),
      summary: this.service.getSyncSummary().pipe(catchError(() => of(null)))
    }).subscribe(r => {
      this.preflight = r.preflight;
      this.settings = r.settings;
      this.summary = r.summary;
      this.recompute();
      done?.();
    });
  }

  private recompute(): void {
    this.steps = setupSteps({
      connection: this.connection, preflight: this.preflight, settings: this.settings, summary: this.summary
    });
    this.unlocked = unlockedTabs(this.steps, this.canManage);
    // A44 — the new tabs are not setup steps; they open once QuickBooks is connected.
    if (this.isConnected) A44_TABS.forEach(t => this.unlocked.add(t));
    if (!this.unlocked.has(this.activeTab)) this.activeTab = 'connection';
  }

  /** Where to open: where the admin asked, else the step to do next, else the dashboard. */
  private pickTab(prefer: QboTabKey | null): QboTabKey {
    if (prefer && this.unlocked.has(prefer)) return prefer;
    if (this.isComplete) return 'sync';
    const step = this.current;
    return step && this.unlocked.has(step.tab) ? step.tab : 'connection';
  }

  // ── Navigation ──────────────────────────────────────────────────────────────

  selectTab(tab: string | number | undefined): void {
    const key = tab as QboTabKey;
    if (!key || !this.unlocked.has(key)) return;
    this.activeTab = key;
  }

  // ── What the tabs report ────────────────────────────────────────────────────

  onConnectionChange(connection: ConnectionStatusModel): void {
    this.connection = connection;
    this.loadDependents();
  }

  onPreflightChange(preflight: PreflightResultModel): void {
    this.preflight = preflight;
    this.recompute();
  }

  /** Settings changed (mappings saved, matching confirmed, mode switched): preflight may read differently now. */
  onSettingsChange(settings: IntegrationSettingsModel): void {
    this.settings = settings;
    this.recompute();
    if (this.isConnected) {
      this.service.getPreflight().pipe(catchError(() => of(null))).subscribe(p => {
        if (p) { this.preflight = p; this.recompute(); }
      });
    }
  }

  onSummaryChange(summary: SyncSummaryModel): void {
    this.summary = summary;
    this.recompute();
  }

  /** A load on the Initial sync tab changed the counts the checklist reads. */
  refreshSummary(): void {
    this.service.getSyncSummary().pipe(catchError(() => of(null))).subscribe(s => {
      if (s) this.onSummaryChange(s);
    });
  }
}
