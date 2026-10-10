import { Component, Injector, computed, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { MenuItem, MessageService } from 'primeng/api';
import { MenuModule } from 'primeng/menu';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { of } from 'rxjs';
import { catchError, switchMap } from 'rxjs/operators';

import { SyncKind, SyncStatus } from '../../../models/quickbooks-integration.models';
import { QuickBooksIntegrationService, qboErrorMessage } from '../../../services/quickbooks-integration.service';
import { QboSyncStatusStore } from './qbo-sync-status.store';
import { STATE_DESCRIPTION, stateLabel, stateSeverity, stateTagClass } from './qbo-sync-state';

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

  private readonly store = inject(QboSyncStatusStore);
  private readonly injector = inject(Injector);
  private readonly messages = inject(MessageService, { optional: true });

  private readonly pushing = signal(false);

  private readonly request = computed(() => ({
    available: this.store.isAvailable(),
    kind: this.kind(),
    externalId: this.externalId()
  }));

  /** null renders nothing. */
  readonly status = toSignal(
    toObservable(this.request).pipe(
      switchMap(r => r.available && r.kind && r.externalId
        ? this.store.status(r.kind, r.externalId).pipe(catchError(() => of(null)))
        : of(null))
    ),
    { initialValue: null as SyncStatus | null }
  );

  readonly menuItems = computed<MenuItem[]>(() => {
    const s = this.status();
    if (!s) return [];
    const items: MenuItem[] = [];
    if (s.deepLink) {
      items.push({ label: 'View in QuickBooks', icon: 'pi pi-external-link', url: s.deepLink, target: '_blank' });
    }
    if (this.store.canPush()) {
      items.push({
        label: 'Push now', icon: 'pi pi-upload', disabled: this.pushing(),
        command: () => this.pushNow()
      });
    }
    return items;
  });

  text(s: SyncStatus): string {
    const prefix = this.label();
    return prefix ? `${prefix}: ${stateLabel(s.state)}` : stateLabel(s.state);
  }

  severity(s: SyncStatus) { return stateSeverity(s.state); }

  tagClass(s: SyncStatus): string { return stateTagClass(s.state); }

  tooltip(s: SyncStatus): string {
    const parts: string[] = [];
    if (s.lastError) parts.push(s.lastError);
    else parts.push(STATE_DESCRIPTION[s.state] ?? '');
    if (s.remoteDocNumber) parts.push(`QuickBooks number ${s.remoteDocNumber}.`);
    if (s.lastSyncedAt) parts.push(`Last synced ${new Date(s.lastSyncedAt).toLocaleString()}.`);
    return parts.filter(Boolean).join(' ');
  }

  /** Asks SCM to send this record to QuickBooks again, then looks its state up again. */
  pushNow(): void {
    const kind = this.kind();
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
      next: () => {
        this.pushing.set(false);
        this.messages?.add({ severity: 'success', summary: 'Sent to QuickBooks', detail: 'The record is queued to be sent again.' });
        this.store.refresh(kind, id);
      },
      error: (err) => {
        this.pushing.set(false);
        this.messages?.add({ severity: 'error', summary: 'Not sent', detail: qboErrorMessage(err, 'The record could not be pushed to QuickBooks.') });
      }
    });
  }
}
