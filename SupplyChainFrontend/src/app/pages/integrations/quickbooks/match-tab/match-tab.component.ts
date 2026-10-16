import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { SelectButtonModule } from 'primeng/selectbutton';
import { TableModule } from 'primeng/table';
import { TabsModule } from 'primeng/tabs';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  IntegrationSettingsModel, MatchCandidateModel, MatchConfidence, MatchDecisionChoice, MatchKind, MatchScanResultModel,
  QuickBooksIntegrationService, RemoteEntityModel, qboErrorMessage
} from '../../../../services/quickbooks-integration.service';
import {
  CONFIDENCE_SEVERITY, DECISION_LABEL, DraftDecision, KIND_PLURAL, Option, buildDecisions, initialDraft, linkAllExact
} from '../quickbooks.shared';

type ConfidenceFilter = 'All' | MatchConfidence;

/**
 * Links SCM's customers, vendors and items to records the accountant already entered in QuickBooks,
 * so nothing is created twice. Nothing is created or changed in QuickBooks here.
 */
@Component({
  selector: 'app-qbo-match-tab',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, ConfirmDialogModule, DialogModule, InputTextModule, SelectModule, SelectButtonModule,
    TableModule, TabsModule, TagModule, TooltipModule
  ],
  templateUrl: './match-tab.component.html',
  styleUrls: ['../quickbooks-tab.scss', './match-tab.component.scss'],
  providers: [ConfirmationService]
})
export class MatchTabComponent implements OnInit {
  @Input() canManage = false;
  @Input() settings: IntegrationSettingsModel | null = null;
  @Output() settingsChange = new EventEmitter<IntegrationSettingsModel>();

  readonly kindOptions: Option<MatchKind>[] = [
    { label: 'Customers', value: 'Customer' },
    { label: 'Vendors', value: 'Vendor' },
    { label: 'Items', value: 'Item' }
  ];
  readonly confidenceOptions: Option<ConfidenceFilter>[] = [
    { label: 'All', value: 'All' },
    { label: 'Exact', value: 'Exact' },
    { label: 'Probable', value: 'Probable' },
    { label: 'No match', value: 'None' }
  ];

  kind: MatchKind = 'Customer';
  confidenceFilter: ConfidenceFilter = 'All';

  candidates: MatchCandidateModel[] = [];
  isLoading = false;
  loadFailed = false;
  isScanning = false;
  isSaving = false;
  isCompleting = false;

  scans: Partial<Record<MatchKind, MatchScanResultModel>> = {};

  /** Each kind keeps its unsaved choices while the admin looks at another. */
  private readonly drafts: Record<MatchKind, Map<string, DraftDecision>> = {
    Customer: new Map(), Vendor: new Map(), Item: new Map()
  };

  constructor(
    private service: QuickBooksIntegrationService,
    private messages: MessageService,
    private confirmation: ConfirmationService
  ) {}

  ngOnInit(): void {
    this.loadCandidates();
    this.loadRemote();
  }

  // ── A44 D-17 — QuickBooks records not linked to anything in SCM ─────────────

  /** Unlinked imported QuickBooks records of the current kind; null while not loaded. */
  remote: RemoteEntityModel[] | null = null;
  /** False when the server has no remote-entity endpoints (older server): the section is hidden. */
  remoteAvailable = true;
  isLoadingRemote = false;
  isImporting = false;
  remoteSearch = '';

  linkVisible = false;
  linkRemoteItem: RemoteEntityModel | null = null;
  linkExternalId: string | null = null;
  isLinking = false;

  get filteredRemote(): RemoteEntityModel[] {
    const term = this.remoteSearch.trim().toLowerCase();
    const list = this.remote ?? [];
    return term ? list.filter(r => (r.name ?? '').toLowerCase().includes(term) || r.remoteId.toLowerCase().includes(term)) : list;
  }

  /** SCM records of this kind that are not linked yet: the targets a QuickBooks record can be linked to. */
  get linkTargets(): Option<string>[] {
    return this.candidates
      .filter(c => !(c.decision === 'Link' && c.remoteId))
      .map(c => ({ label: c.localLabel, value: c.externalId }))
      .sort((a, b) => a.label.localeCompare(b.label));
  }

  extraText(r: RemoteEntityModel): string {
    const parts: [string, string | null | undefined][] = [
      ['Company', r.companyName !== r.name ? r.companyName : null], ['Email', r.email], ['Tax id', r.taxId],
      ['Account', r.accountNumber], ['SKU', r.sku], ['Currency', r.currencyCode]
    ];
    const text = parts.filter(([, v]) => !!v).map(([k, v]) => `${k}: ${v}`);
    if (r.active === false) text.push('inactive in QuickBooks');
    return text.join(' · ');
  }

  loadRemote(): void {
    const kind = this.kind;
    this.isLoadingRemote = true;
    this.service.getRemote(kind, false).subscribe({
      next: (list) => {
        if (kind !== this.kind) return;
        this.isLoadingRemote = false;
        this.remoteAvailable = true;
        this.remote = list ?? [];
      },
      error: (err) => {
        if (kind !== this.kind) return;
        this.isLoadingRemote = false;
        this.remote = null;
        if (err?.status === 404) this.remoteAvailable = false;
      }
    });
  }

  /** Reads every QuickBooks record of this kind into SCM as remote-only (nothing is created in SCM). */
  importRemote(): void {
    if (!this.canManage || this.isImporting) return;
    const kind = this.kind;
    this.isImporting = true;
    this.service.importRemote(kind).subscribe({
      next: (res) => {
        this.isImporting = false;
        this.messages.add({
          severity: 'success', summary: 'Imported',
          detail: `${res?.read ?? 0} QuickBooks ${KIND_PLURAL[kind]} read${res?.unlinked != null ? `; ${res.unlinked} are not linked to SCM` : ''}.`
        });
        if (kind === this.kind) this.loadRemote();
      },
      error: (err) => {
        this.isImporting = false;
        this.messages.add({ severity: 'error', summary: 'Not imported', detail: qboErrorMessage(err, 'QuickBooks could not be read.') });
      }
    });
  }

  openLink(r: RemoteEntityModel): void {
    if (!this.canManage) return;
    this.linkRemoteItem = r;
    const sameName = this.linkTargets.filter(t => t.label.trim().toLowerCase() === (r.name ?? '').trim().toLowerCase());
    this.linkExternalId = sameName.length === 1 ? sameName[0].value : null;
    this.linkVisible = true;
  }

  link(): void {
    const r = this.linkRemoteItem;
    if (!this.canManage || !r || !this.linkExternalId || this.isLinking) return;
    const kind = this.kind;
    this.isLinking = true;
    this.service.linkRemote(kind, r.remoteId, { externalId: this.linkExternalId }).subscribe({
      next: () => {
        this.isLinking = false;
        this.linkVisible = false;
        this.messages.add({ severity: 'success', summary: 'Linked', detail: `${r.name || 'The QuickBooks record'} is linked. Nothing was changed in QuickBooks.` });
        this.drafts[kind].clear();
        if (kind === this.kind) { this.loadRemote(); this.loadCandidates(); }
      },
      error: (err) => {
        this.isLinking = false;
        this.messages.add({ severity: 'error', summary: 'Not linked', detail: qboErrorMessage(err, 'The records could not be linked.') });
      }
    });
  }

  // ── State ───────────────────────────────────────────────────────────────────

  get kindPlural(): string { return KIND_PLURAL[this.kind]; }

  get draftsForKind(): Map<string, DraftDecision> { return this.drafts[this.kind]; }

  /** What the table shows: rebuilt when the proposals or the filter change, not on every render. */
  filtered: MatchCandidateModel[] = [];

  setConfidenceFilter(value: ConfidenceFilter): void {
    this.confidenceFilter = value ?? 'All';
    this.applyFilter();
  }

  private setCandidates(list: MatchCandidateModel[]): void {
    this.candidates = list;
    this.applyFilter();
  }

  private applyFilter(): void {
    this.filtered = this.confidenceFilter === 'All'
      ? this.candidates
      : this.candidates.filter(c => c.confidence === this.confidenceFilter);
  }

  get pendingChanges(): number { return buildDecisions(this.candidates, this.draftsForKind).length; }

  get undecided(): number {
    return this.candidates.filter(c => !this.draft(c).decision).length;
  }

  get exactLinkable(): number {
    return this.candidates.filter(c => c.confidence === 'Exact' && !!c.remoteId).length;
  }

  get matchingConfirmed(): boolean { return !!this.settings?.matchingConfirmedAt; }

  draft(c: MatchCandidateModel): DraftDecision {
    return this.draftsForKind.get(c.id) ?? initialDraft(c);
  }

  private readonly optionsWithLink: Option<MatchDecisionChoice>[] = [
    { label: DECISION_LABEL.Link, value: 'Link' },
    { label: DECISION_LABEL.CreateNew, value: 'CreateNew' },
    { label: DECISION_LABEL.Skip, value: 'Skip' }
  ];
  private readonly optionsWithoutLink: Option<MatchDecisionChoice>[] = [
    { label: `${DECISION_LABEL.Link} (no QuickBooks record proposed)`, value: 'Link', disabled: true },
    { label: DECISION_LABEL.CreateNew, value: 'CreateNew' },
    { label: DECISION_LABEL.Skip, value: 'Skip' }
  ];

  /** Link is only offered when there is a QuickBooks record to link to. */
  decisionOptions(c: MatchCandidateModel): Option<MatchDecisionChoice>[] {
    return c.remoteId ? this.optionsWithLink : this.optionsWithoutLink;
  }

  confidenceOf(c: MatchCandidateModel) {
    return CONFIDENCE_SEVERITY[c.confidence] ?? 'secondary';
  }

  isChanged(c: MatchCandidateModel): boolean {
    const d = this.draft(c);
    return !!d.decision && d.decision !== c.decision;
  }

  // ── Loading ─────────────────────────────────────────────────────────────────

  onKindTab(value: string | number | undefined): void {
    if (value === 'Customer' || value === 'Vendor' || value === 'Item') this.selectKind(value);
  }

  selectKind(kind: MatchKind): void {
    if (!kind || kind === this.kind && this.candidates.length) return;
    this.kind = kind;
    this.confidenceFilter = 'All';
    this.loadCandidates();
    this.remote = null;
    this.remoteSearch = '';
    if (this.remoteAvailable) this.loadRemote();
  }

  loadCandidates(): void {
    const kind = this.kind;
    this.isLoading = true;
    this.loadFailed = false;

    this.service.getMatchCandidates(kind).subscribe({
      next: (list) => {
        if (kind !== this.kind) return;
        this.isLoading = false;
        this.setCandidates(list ?? []);
      },
      error: () => {
        if (kind !== this.kind) return;
        this.isLoading = false;
        this.loadFailed = true;
        this.setCandidates([]);
      }
    });
  }

  /** Reads this kind's records from QuickBooks and proposes matches. */
  scan(): void {
    if (!this.canManage || this.isScanning) return;
    const kind = this.kind;
    this.isScanning = true;

    this.service.scanMatches(kind).subscribe({
      next: (res) => {
        this.isScanning = false;
        this.scans = { ...this.scans, [kind]: res };
        this.messages.add({
          severity: 'success', summary: 'Scan finished',
          detail: `${res.exact} exact and ${res.probable} probable matches among ${res.localCount} ${KIND_PLURAL[kind]}.`
        });
        if (kind === this.kind) this.loadCandidates();
      },
      error: (err) => {
        this.isScanning = false;
        this.messages.add({ severity: 'error', summary: 'Scan failed', detail: qboErrorMessage(err, 'QuickBooks could not be scanned.') });
      }
    });
  }

  // ── Deciding ────────────────────────────────────────────────────────────────

  setDecision(c: MatchCandidateModel, decision: MatchDecisionChoice | null): void {
    if (!this.canManage) return;
    this.draftsForKind.set(c.id, { decision, remoteId: decision === 'Link' ? (c.remoteId ?? null) : null });
  }

  linkAllExact(): void {
    if (!this.canManage) return;
    const changed = linkAllExact(this.candidates, this.draftsForKind);
    this.messages.add(changed
      ? { severity: 'info', summary: 'Exact matches linked', detail: `${changed} ${changed === 1 ? 'record is' : 'records are'} set to Link. Save to keep the decisions.` }
      : { severity: 'info', summary: 'Nothing to change', detail: 'Every exact match is already set to Link.' });
  }

  save(): void {
    if (!this.canManage || this.isSaving) return;
    const decisions = buildDecisions(this.candidates, this.draftsForKind);
    if (!decisions.length) {
      this.messages.add({ severity: 'info', summary: 'Nothing to save', detail: 'No decision has changed.' });
      return;
    }

    const kind = this.kind;
    this.isSaving = true;
    this.service.confirmMatches(kind, { decisions }).subscribe({
      next: (list) => {
        this.isSaving = false;
        this.drafts[kind].clear();
        if (kind === this.kind) this.setCandidates(list ?? this.candidates);
        this.messages.add({ severity: 'success', summary: 'Decisions saved', detail: `${decisions.length} ${decisions.length === 1 ? 'decision' : 'decisions'} saved. Nothing was changed in QuickBooks.` });
      },
      error: (err) => {
        this.isSaving = false;
        this.messages.add({ severity: 'error', summary: 'Not saved', detail: qboErrorMessage(err, 'The decisions could not be saved.') });
      }
    });
  }

  discard(): void {
    this.draftsForKind.clear();
  }

  // ── Done ────────────────────────────────────────────────────────────────────

  confirmComplete(): void {
    if (!this.canManage || this.isCompleting) return;
    const unsaved = this.pendingChanges;
    const lines = ['Matching is recorded as done for customers, vendors and items. This is required before going Live.'];
    if (unsaved) lines.push(`You have ${unsaved} unsaved ${unsaved === 1 ? 'decision' : 'decisions'} here; save first or they are lost.`);
    if (this.undecided) lines.push(`${this.undecided} ${this.kindPlural} have no decision: they will not be linked to an existing QuickBooks record.`);

    this.confirmation.confirm({
      key: 'qbo-match-complete',
      header: 'Mark matching complete?',
      icon: 'pi pi-check-circle',
      message: lines.join(' '),
      acceptLabel: 'Mark complete',
      rejectLabel: 'Not yet',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.markComplete()
    });
  }

  markComplete(): void {
    if (!this.canManage || this.isCompleting) return;
    this.isCompleting = true;
    this.service.confirmMatchingComplete(true).subscribe({
      next: (settings) => {
        this.isCompleting = false;
        this.settings = settings;
        this.settingsChange.emit(settings);
        this.messages.add({ severity: 'success', summary: 'Matching complete', detail: 'You can now switch to Live on the Sync tab once preflight passes.' });
      },
      error: (err) => {
        this.isCompleting = false;
        this.messages.add({ severity: 'error', summary: 'Not recorded', detail: qboErrorMessage(err, 'Matching could not be marked complete.') });
      }
    });
  }
}
