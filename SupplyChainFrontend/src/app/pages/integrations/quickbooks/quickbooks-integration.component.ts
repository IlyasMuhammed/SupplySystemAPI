import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TabsModule } from 'primeng/tabs';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';

import {
  ConnectionStatusModel, IntegrationSettingsModel, PreflightResultModel, QuickBooksIntegrationService, SyncSummaryModel,
  qboErrorMessage
} from '../../../services/quickbooks-integration.service';
import { AuthService } from '../../service/auth.service';
import {
  CONNECTION_STATUS_LABEL, PERM_MANAGE, PERM_SYNC, QboTabKey, SetupStep, currentStep, needsReconnect, setupComplete,
  setupSteps, unlockedTabs
} from './quickbooks.shared';
import { ConnectionTabComponent } from './connection-tab/connection-tab.component';
import { PreflightTabComponent } from './preflight-tab/preflight-tab.component';
import { MappingsTabComponent } from './mappings-tab/mappings-tab.component';
import { InitialSyncTabComponent } from './initial-sync-tab/initial-sync-tab.component';
import { MatchTabComponent } from './match-tab/match-tab.component';
import { SyncTabComponent } from './sync-tab/sync-tab.component';
import { ApiClientsTabComponent } from './api-clients-tab/api-clients-tab.component';

interface TabDef {
  key: QboTabKey;
  label: string;
  icon: string;
}

const TABS: TabDef[] = [
  { key: 'connection',   label: 'Connection',     icon: 'pi pi-link' },
  { key: 'preflight',    label: 'Preflight',      icon: 'pi pi-verified' },
  { key: 'mappings',     label: 'Mappings',       icon: 'pi pi-arrow-right-arrow-left' },
  { key: 'initial-sync', label: 'Initial sync',   icon: 'pi pi-upload' },
  { key: 'match',        label: 'Match existing', icon: 'pi pi-link' },
  { key: 'sync',         label: 'Sync',           icon: 'pi pi-sync' },
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
    SyncTabComponent, ApiClientsTabComponent
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
    const cameBack = this.route.snapshot.queryParamMap.has('result');
    this.load(cameBack ? 'connection' : null);
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
