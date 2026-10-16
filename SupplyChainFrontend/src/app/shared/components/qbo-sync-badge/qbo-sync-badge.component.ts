import { Component, Injector, computed, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { MenuItem, MessageService } from 'primeng/api';
import { MenuModule } from 'primeng/menu';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { combineLatest, of } from 'rxjs';
import { catchError, map, switchMap } from 'rxjs/operators';

import { SyncKind, SyncStatus } from '../../../models/quickbooks-integration.models';
import { QuickBooksIntegrationService, qboErrorMessage } from '../../../services/quickbooks-integration.service';
import { QboSyncStatusStore } from './qbo-sync-status.store';
import { KIND_LABEL, STATE_DESCRIPTION, isHeldCode, stateLabel, stateSeverity, stateTagClass } from './qbo-sync-state';
import { QboRetryService, isListed, retryActionLabel, retryToast } from './qbo-retry.service';

/**
 * A record's QuickBooks sync state, for SCM pages: a coloured tag with the last error in a tooltip,
 * and a small menu with "View in QuickBooks" and "Push now".
 *
 *   <app-qbo-sync-badge kind="Customer" [externalId]="partner.uuid" label="Customer"></app-qbo-sync-badge>
 *
 * Shows nothing at all when the organization has the integration off, the user may not view it, the
 * organization never connected QuickBooks, or the lookup fails. Every badge on a page shares one
 * lookup per kind (see QboSyncStatusStore).
 */
@Component({
  selector: 'app-qbo-sync-badge',
  standalone: true,
  imports: [CommonModule, TagModule, TooltipModule, MenuModule],
  template: `
    @if (status(); as s) {
      <span class="qbo-badge" data-testid="qbo-sync-badge" [attr.data-state]="s.state">
        @if (menuItems().length) {
          <button type="button" class="qbo-badge-trigger" (click)="qboMenu.toggle($event)"
                  [attr.aria-label]="'QuickBooks: ' + text(s) + '. Open actions'" data-testid="qbo-badge-trigger">
            <p-tag [value]="text(s)" [severity]="severity(s)" [styleClass]="tagClass(s)" icon="pi pi-sync"
                   [pTooltip]="tooltip(s)" tooltipPosition="top"></p-tag>
            <i class="pi pi-angle-down qbo-badge-caret" aria-hidden="true"></i>
          </button>
          <p-menu #qboMenu [model]="menuItems()" [popup]="true" appendTo="body" data-testid="qbo-badge-menu"></p-menu>
        } @else {
          <p-tag [value]="text(s)" [severity]="severity(s)" [styleClass]="tagClass(s)" icon="pi pi-sync"
                 [pTooltip]="tooltip(s)" tooltipPosition="top"></p-tag>
        }
        @if (actionLabel(); as a) {
          <button type="button" class="qbo-badge-action" (click)="retry()" [disabled]="retrying()"
                  [attr.aria-label]="a + ' (QuickBooks)'" data-testid="qbo-badge-action">
            <i class="pi" [class.pi-spin]="retrying()" [class.pi-spinner]="retrying()"
               [class.pi-replay]="!retrying() && a === 'Retry'" [class.pi-send]="!retrying() && a !== 'Retry'" aria-hidden="true"></i>
            {{ a }}
          </button>
        }
      </span>
    }
  `,
  styles: [`
    :host { display: inline-flex; vertical-align: middle; }
    .qbo-badge { display: inline-flex; align-items: center; }
    .qbo-badge-trigger {
      display: inline-flex; align-items: center; gap: .125rem; padding: 0; margin: 0;
      border: 0; background: none; cursor: pointer; border-radius: 999px;
    }
    .qbo-badge-trigger:focus-visible { outline: 2px solid var(--primary-color); outline-offset: 2px; }
    .qbo-badge-caret { font-size: .7rem; color: var(--text-color-secondary); }
    .qbo-badge { gap: .375rem; }
    .qbo-badge-action {
      display: inline-flex; align-items: center; gap: .25rem; padding: .125rem .5rem; margin: 0;
      border: 1px solid var(--sms-border); border-radius: 999px; background: var(--sms-surface);
      color: var(--sms-primary-text); font: inherit; font-size: .75rem; font-weight: 600; cursor: pointer; white-space: nowrap;
    }
    .qbo-badge-action:hover:not(:disabled) { background: var(--sms-primary-soft); }
    .qbo-badge-action:disabled { opacity: .6; cursor: progress; }
    .qbo-badge-action:focus-visible { outline: 2px solid var(--primary-color); outline-offset: 2px; }
    .qbo-badge-action i { font-size: .7rem; }
    :host ::ng-deep .p-tag { white-space: nowrap; font-size: .75rem; }
    :host ::ng-deep .p-tag.qbo-tone-teal { background: var(--sms-teal-soft); color: var(--sms-teal); }
    :host ::ng-deep .p-tag.qbo-tone-grey { background: var(--sms-surface-2); color: var(--sms-text-muted); }
  `]
})
export class QboSyncBadgeComponent {
  /** Customer | Vendor | Item | SalesInvoice | Bill */
  readonly kind = input<SyncKind | null | undefined>(null);
  /** The SCM record's UUID (a variant's UUID for an item). */
  readonly externalId = input<string | null | undefined>(null);
  /** Put before the state, e.g. "Customer" on a partner that is also a vendor. */
  readonly label = input<string | null | undefined>(null);
  /**
   * A44 — other kinds the same SCM record may have been sent as (a POS invoice as a SalesReceipt, a POS credit note as a
   * CreditMemo or RefundReceipt). The first kind with a record in the gateway is shown; otherwise `kind`'s status.
   */
  readonly alsoKinds = input<SyncKind[] | null | undefined>(null);
  /** A44 D-33 — the inline "Send to QuickBooks" / "Retry" button (detail pages). List pages turn it off. */
  readonly inlineAction = input<boolean>(true);

  private readonly store = inject(QboSyncStatusStore);
  private readonly injector = inject(Injector);
  private readonly messages = inject(MessageService, { optional: true });

  private readonly pushing = signal(false);
  private readonly retryService = inject(QboRetryService);
  readonly retrying = signal(false);

  private readonly request = computed(() => ({
    available: this.store.isAvailable(),
    kind: this.kind(),
    externalId: this.externalId(),
    also: (this.alsoKinds() ?? []).filter(k => !!k && k !== this.kind())
  }));

  /**
   * The status shown, and whether its kind is the one the record really has in the gateway. With `alsoKinds`, the first
   * candidate the gateway holds (any state but a bare NotSynced; a held record counts) wins; when none does, the primary
   * kind's NotSynced is shown but its kind is NOT known (REV-20: a POS cash sale is a SalesReceipt, not an Invoice).
   */
  private readonly pick = toSignal(
    toObservable(this.request).pipe(
      switchMap(r => {
        if (!r.available || !r.kind || !r.externalId) return of(null);
        const primary = this.store.status(r.kind, r.externalId).pipe(catchError(() => of(null)), map(s => s ? { s, known: true } : null));
        if (!r.also.length) return primary;
        const all = [r.kind, ...r.also].map(k => this.store.status(k, r.externalId).pipe(catchError(() => of(null))));
        return combineLatest(all).pipe(map(list => {
          const found = list.find(s => !!s && (s.state !== 'NotSynced' || isHeldCode(s.lastErrorCode)));
          if (found) return { s: found, known: true };
          return list[0] ? { s: list[0], known: false } : null;
        }));
      })
    ),
    { initialValue: null as { s: SyncStatus; known: boolean } | null }
  );

  /** null renders nothing. */
  readonly status = computed<SyncStatus | null>(() => this.pick()?.s ?? null);

  /** The label the record's state calls for (before permissions and kind): Send to QuickBooks / Retry / none. */
  private readonly wantedLabel = computed<string | null>(() => {
    const s = this.status();
    if (!s || !this.inlineAction() || !this.store.connected()) return null;
    return retryActionLabel(s.state, this.held(s));
  });

  /** REV-20 — for a record of unknown kind, the kind its posting was made as (by-source documentType); null = unknown. */
  private readonly postedKind = toSignal(
    // Asked only when an action could be offered: the state calls for one and the user may push or act on postings.
    toObservable(computed(() => ({
      unknown: !!this.pick() && !this.pick()!.known && !!this.wantedLabel()
        && (this.retryService.canPush() || this.retryService.mayActOnPostings(this.kind())),
      id: this.externalId()
    }))).pipe(
      switchMap(r => r.unknown && r.id ? this.retryService.postedKind(r.id).pipe(catchError(() => of(null))) : of(null))
    ),
    { initialValue: null as SyncKind | null }
  );

  /**
   * The kind every action uses (button, Push now, refresh): the gateway's own kind for the record, else the kind it was
   * posted as. Null for a document whose kind cannot be determined — then nothing is offered (no duplicate Invoice).
   */
  readonly effectiveKind = computed<SyncKind | null>(() => {
    const p = this.pick();
    if (!p) return null;
    return p.known ? ((p.s.kind as SyncKind | undefined) ?? this.kind() ?? null) : this.postedKind();
  });

  /**
   * REV-19 — without INTEGRATION_SYNC the button is only worth showing when the document has a posting target the user
   * may retry (by-source `canAct`). Asked once per record / state, and only in that case.
   */
  private readonly postingActable = toSignal(
    toObservable(computed(() => ({ wanted: this.wantedLabel(), kind: this.effectiveKind(), id: this.externalId() }))).pipe(
      switchMap(r => r.wanted && r.kind && r.id && !this.retryService.canPush() && this.retryService.mayActOnPostings(r.kind)
        ? this.retryService.hasActableTarget(r.kind, r.id)
        : of(false))
    ),
    { initialValue: false }
  );

  /**
   * A44 D-33 — the inline button: "Send to QuickBooks" (NotSynced, not held) or "Retry" (Failed / Blocked /
   * NeedsResolution), on a live connection, for a record whose kind is known, when the user may push
   * (INTEGRATION_SYNC) or retry one of its postings.
   */
  readonly actionLabel = computed<string | null>(() => {
    const label = this.wantedLabel();
    if (!label || !this.effectiveKind()) return null;
    return this.retryService.canPush() || this.postingActable() ? label : null;
  });

  /** Runs the D-33 action for the record as the gateway holds it (or as it was posted). */
  retry(): void {
    const id = this.externalId();
    const kind = this.effectiveKind();
    if (!kind || !id || this.retrying() || !this.actionLabel()) return;
    this.retrying.set(true);
    this.retryService.run(kind, id).subscribe(outcome => {
      this.retrying.set(false);
      this.messages?.add({ ...retryToast(outcome), life: 5000 });
      // CTRL: nothing to send for the document — the badge stays as it is.
      if (outcome.result !== 'notFound') this.refreshAll(id);
    });
  }

  /** Looks every candidate kind up again (the record may now exist under the kind it was posted as). */
  private refreshAll(id: string): void {
    const kinds = new Set<SyncKind>([this.kind(), ...(this.alsoKinds() ?? []), this.effectiveKind()].filter(Boolean) as SyncKind[]);
    kinds.forEach(k => this.store.refresh(k, id));
  }

  readonly menuItems = computed<MenuItem[]>(() => {
    const s = this.status();
    if (!s) return [];
    const items: MenuItem[] = [];
    if (s.deepLink) {
      items.push({ label: 'View in QuickBooks', icon: 'pi pi-external-link', url: s.deepLink, target: '_blank' });
    }
    // REV-20 — never push a kind the record may not have.
    if (this.store.canPush() && this.effectiveKind()) {
      items.push({
        label: 'Push now', icon: 'pi pi-upload', disabled: this.pushing(),
        command: () => this.pushNow()
      });
    }
    return items;
  });

  /** A44 D-14 — held by sync control: waiting to be released, not missing. */
  private held(s: SyncStatus): boolean { return s.state === 'NotSynced' && isHeldCode(s.lastErrorCode); }

  text(s: SyncStatus): string {
    const prefix = this.label();
    const state = this.held(s) ? 'Held' : stateLabel(s.state);
    return prefix ? `${prefix}: ${state}` : state;
  }

  severity(s: SyncStatus) { return this.held(s) ? 'warn' : stateSeverity(s.state); }

  tagClass(s: SyncStatus): string { return this.held(s) ? 'qbo-tone-amber' : stateTagClass(s.state); }

  tooltip(s: SyncStatus): string {
    const parts: string[] = [];
    if (this.held(s)) parts.push('Held: this document type is not being sent to QuickBooks yet. It is sent when sync control turns it on.');
    else if (s.lastError) parts.push(s.lastError);
    else parts.push(STATE_DESCRIPTION[s.state] ?? '');
    if (s.kind && this.kind() && s.kind !== this.kind()) parts.push(`Sent as a ${KIND_LABEL[s.kind]?.toLowerCase() ?? s.kind}.`);
    if (s.remoteDocNumber) parts.push(`QuickBooks number ${s.remoteDocNumber}.`);
    if (s.lastSyncedAt) parts.push(`Last synced ${new Date(s.lastSyncedAt).toLocaleString()}.`);
    return parts.filter(Boolean).join(' ');
  }

  /** Asks SCM to send this record to QuickBooks again, then looks its state up again. */
  pushNow(): void {
    // REV-20 — the kind the record has (or was posted as); a record with alternate kinds and none known is not pushed.
    const kind = this.effectiveKind() ?? ((this.alsoKinds() ?? []).length ? null : this.kind());
    const id = this.externalId();
    if (!kind || !id || this.pushing() || !this.store.canPush()) return;

    let api: QuickBooksIntegrationService;
    try {
      api = this.injector.get(QuickBooksIntegrationService);
    } catch {
      return;
    }

    this.pushing.set(true);
    api.pushNow(kind, [id]).subscribe({
      next: (res) => {
        this.pushing.set(false);
        if (isListed(res?.notFound, id)) {
          this.messages?.add({ severity: 'info', summary: 'Nothing to send', detail: 'Nothing to send for this document.' });
          return;
        }
        if (res?.skippedInMatching?.length) {
          this.messages?.add({ severity: 'warn', summary: 'Not sent',
            detail: 'This record was skipped in QuickBooks matching, so it is not sent. Change its decision under Match existing first.' });
          return;
        }
        this.messages?.add({ severity: 'success', summary: 'Sent to QuickBooks', detail: 'The record is queued to be sent again.' });
        this.refreshAll(id);
      },
      error: (err) => {
        this.pushing.set(false);
        this.messages?.add({ severity: 'error', summary: 'Not sent', detail: qboErrorMessage(err, 'The record could not be pushed to QuickBooks.') });
      }
    });
  }
}
