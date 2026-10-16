import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TagModule } from 'primeng/tag';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';

import {
  QuickBooksIntegrationService, ReleaseConfirmationModel, SyncControlDocumentType, SyncControlRowModel, UpdateSyncControlResult,
  qboErrorMessage
} from '../../../../services/quickbooks-integration.service';
import {
  SYNC_CONTROL_HINT, SYNC_CONTROL_LABEL, SYNC_CONTROL_STATUS_TONE, SyncControlStatus, completeSyncControl, releaseCount,
  syncControlStatus
} from '../a44.shared';

interface Draft {
  isEnabled: boolean;
  holdWhenDisabled: boolean;
}

interface Outcome {
  type: SyncControlDocumentType;
  result?: UpdateSyncControlResult;
  error?: string;
  heldCount?: number;
}

/**
 * A44 D-14 — Sync control: per document type, whether it is sent to QuickBooks and, while it is not, whether its
 * records are held to be sent later. Turning a held type on releases what it held (confirmed first). Replaces the six
 * "send automatically" switches of the Mappings tab.
 */
@Component({
  selector: 'app-qbo-sync-control-tab',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, ConfirmDialogModule, TagModule, ToggleSwitchModule, TooltipModule],
  templateUrl: './sync-control-tab.component.html',
  styleUrls: ['../quickbooks-tab.scss', './sync-control-tab.component.scss'],
  providers: [ConfirmationService]
})
export class SyncControlTabComponent implements OnInit {
  @Input() canManage = false;
  /** Something was released or changed: the page's counts are stale. */
  @Output() changed = new EventEmitter<void>();

  readonly label = SYNC_CONTROL_LABEL;
  readonly hint = SYNC_CONTROL_HINT;
  readonly tone = SYNC_CONTROL_STATUS_TONE;

  rows: SyncControlRowModel[] = [];
  drafts: Record<string, Draft> = {};
  isLoading = true;
  loadFailed = false;
  loadError = '';
  isSaving = false;
  releasing: Record<string, boolean> = {};

  constructor(
    private service: QuickBooksIntegrationService,
    private messages: MessageService,
    private confirmation: ConfirmationService
  ) {}

  ngOnInit(): void { this.load(); }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getSyncControl().subscribe({
      next: (rows) => { this.isLoading = false; this.apply(rows ?? []); },
      error: (err) => {
        this.isLoading = false;
        this.loadFailed = true;
        this.loadError = qboErrorMessage(err, 'Sync control could not be loaded.');
      }
    });
  }

  private apply(rows: SyncControlRowModel[]): void {
    this.rows = completeSyncControl(rows);
    this.drafts = {};
    for (const r of this.rows) this.drafts[r.documentType] = { isEnabled: r.isEnabled, holdWhenDisabled: r.holdWhenDisabled };
  }

  // ── State ───────────────────────────────────────────────────────────────────

  draft(r: SyncControlRowModel): Draft { return this.drafts[r.documentType]; }

  status(r: SyncControlRowModel): SyncControlStatus { return syncControlStatus(r); }

  draftStatus(r: SyncControlRowModel): SyncControlStatus { return syncControlStatus(this.draft(r)); }

  isChanged(r: SyncControlRowModel): boolean {
    const d = this.draft(r);
    return !!d && (d.isEnabled !== r.isEnabled || d.holdWhenDisabled !== r.holdWhenDisabled);
  }

  get changedRows(): SyncControlRowModel[] { return this.rows.filter(r => this.isChanged(r)); }

  get dirty(): boolean { return this.changedRows.length > 0; }

  /** Types this save turns on that hold records — each releases its held count. */
  get releases(): { type: SyncControlDocumentType; label: string; count: number }[] {
    return this.changedRows
      .map(r => ({ type: r.documentType, label: this.label[r.documentType], count: releaseCount(r, this.draft(r)) }))
      .filter(x => x.count > 0);
  }

  get releaseTotal(): number { return this.releases.reduce((n, r) => n + r.count, 0); }

  /** "Enabling POS sales will release 12 queued transactions." */
  get releaseBanner(): string {
    const list = this.releases;
    if (!list.length) return '';
    const names = list.map(r => r.label).join(', ');
    const n = this.releaseTotal;
    return `Enabling ${names} will release ${n} queued ${n === 1 ? 'transaction' : 'transactions'}.`;
  }

  get totalHeld(): number { return this.rows.reduce((n, r) => n + (r.heldCount ?? 0), 0); }

  // ── Editing ─────────────────────────────────────────────────────────────────

  setEnabled(r: SyncControlRowModel, value: boolean): void {
    if (!this.canManage) return;
    this.drafts = { ...this.drafts, [r.documentType]: { ...this.draft(r), isEnabled: !!value } };
  }

  setHold(r: SyncControlRowModel, value: boolean): void {
    if (!this.canManage) return;
    this.drafts = { ...this.drafts, [r.documentType]: { ...this.draft(r), holdWhenDisabled: !!value } };
  }

  discard(): void { this.apply(this.rows); }

  // ── Saving ──────────────────────────────────────────────────────────────────

  save(): void {
    if (!this.canManage || this.isSaving || !this.dirty) return;
    if (!this.releaseTotal) { this.send(false); return; }
    this.confirmation.confirm({
      key: 'qbo-sync-control',
      header: 'Release held transactions?',
      icon: 'pi pi-send',
      message: `${this.releaseBanner} They are sent to QuickBooks in the order they were held.`,
      acceptLabel: 'Save and release',
      rejectLabel: 'Cancel',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.send(true)
    });
  }

  private send(confirmRelease: boolean): void {
    const changed = this.changedRows;
    this.isSaving = true;
    forkJoin(changed.map(r => {
      const d = this.draft(r);
      return this.service.updateSyncControl(r.documentType, { isEnabled: d.isEnabled, holdWhenDisabled: d.holdWhenDisabled, confirmRelease }).pipe(
        map(result => ({ type: r.documentType, result } as Outcome)),
        catchError(err => of({
          type: r.documentType, error: qboErrorMessage(err, 'Not saved.'),
          // 409 { documentType, heldCount }: more is held than this page knew — the banner shows it on the next try.
          heldCount: err?.status === 409 ? (err?.error?.result as ReleaseConfirmationModel | undefined)?.heldCount : undefined
        } as Outcome)));
    })).subscribe(outcomes => {
      this.isSaving = false;
      const ok = outcomes.filter(o => o.result);
      const failed = outcomes.filter(o => o.error);
      // Keep the failed rows' drafts so the admin can try again.
      const keep = new Map(failed.map(f => [f.type, this.drafts[f.type]]));
      const updated = this.rows.map(r => {
        const o = ok.find(x => x.type === r.documentType);
        if (o?.result?.row) return { ...r, ...o.result.row, documentType: r.documentType };
        const f = failed.find(x => x.type === r.documentType);
        return f?.heldCount != null ? { ...r, heldCount: f.heldCount } : r;
      });
      this.apply(updated);
      keep.forEach((d, type) => { if (d) this.drafts[type] = d; });

      const released = ok.reduce((n, o) => n + (o.result?.released ?? 0), 0);
      if (ok.length) {
        this.messages.add({
          severity: 'success', summary: 'Saved',
          detail: released ? `Sync control is saved. ${released} held ${released === 1 ? 'transaction was' : 'transactions were'} released.` : 'Sync control is saved.'
        });
        this.changed.emit();
      }
      for (const f of failed) {
        this.messages.add({ severity: 'error', summary: `${this.label[f.type]} not saved`, detail: f.error });
      }
    });
  }

  /** Sends what a type holds now (a type that is on but still holds records, e.g. after a failed release). */
  release(r: SyncControlRowModel): void {
    if (!this.canManage || this.releasing[r.documentType] || !(r.heldCount ?? 0)) return;
    this.releasing = { ...this.releasing, [r.documentType]: true };
    this.service.releaseHeld(r.documentType).subscribe({
      next: (res) => {
        this.releasing = { ...this.releasing, [r.documentType]: false };
        const n = res?.released ?? 0;
        const still = res?.stillHeld ?? Math.max(0, (r.heldCount ?? 0) - n);
        this.rows = this.rows.map(x => x.documentType === r.documentType ? { ...x, heldCount: still } : x);
        this.messages.add({ severity: 'success', summary: 'Released', detail: `${n} held ${n === 1 ? 'transaction is' : 'transactions are'} queued for QuickBooks.` });
        this.changed.emit();
      },
      error: (err) => {
        this.releasing = { ...this.releasing, [r.documentType]: false };
        this.messages.add({ severity: 'error', summary: 'Not released', detail: qboErrorMessage(err, 'The held transactions could not be released.') });
      }
    });
  }
}
