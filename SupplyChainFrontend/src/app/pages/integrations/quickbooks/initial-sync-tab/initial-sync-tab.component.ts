import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import {
  BackfillResult, QuickBooksIntegrationService, SyncItemModel, SyncKind, SyncMode, qboErrorMessage
} from '../../../../services/quickbooks-integration.service';
import { KIND_ICON, KIND_LABEL, KIND_PLURAL, QboTabKey, sourceRoute } from '../quickbooks.shared';

/**
 * The first load: SCM's customers, vendors and items are handed to the gateway while the
 * connection is in dry run, so every one is validated and nothing reaches QuickBooks. What the
 * gateway refuses is listed with the fix, linked to the record in SCM.
 */
@Component({
  selector: 'app-qbo-initial-sync-tab',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, TableModule, TagModule, TooltipModule],
  templateUrl: './initial-sync-tab.component.html',
  styleUrls: ['../quickbooks-tab.scss', './initial-sync-tab.component.scss']
})
export class InitialSyncTabComponent implements OnInit {
  @Input() canManage = false;
  @Input() mode: SyncMode | null = null;
  /** A load finished, so the dashboard counts have changed. */
  @Output() loaded = new EventEmitter<BackfillResult>();
  @Output() navigate = new EventEmitter<QboTabKey>();

  readonly kinds: SyncKind[] = ['Customer', 'Vendor', 'Item'];
  readonly kindLabel = KIND_LABEL;
  readonly kindPlural = KIND_PLURAL;
  readonly kindIcon = KIND_ICON;

  running: Partial<Record<SyncKind, boolean>> = {};
  results: Partial<Record<SyncKind, BackfillResult>> = {};

  blocked: SyncItemModel[] = [];
  blockedTotal = 0;
  blockedPage = 1;
  readonly pageSize = 20;
  isLoadingBlocked = false;
  blockedFailed = false;

  constructor(private service: QuickBooksIntegrationService, private messages: MessageService) {}

  ngOnInit(): void {
    this.loadBlocked(1);
  }

  get isLive(): boolean { return this.mode === 'Live'; }

  loadKind(kind: SyncKind): void {
    if (!this.canManage || this.running[kind]) return;
    this.running = { ...this.running, [kind]: true };

    this.service.backfill(kind).subscribe({
      next: (res) => {
        this.running = { ...this.running, [kind]: false };
        this.results = { ...this.results, [kind]: res };
        this.messages.add({
          severity: 'success', summary: `${KIND_LABEL[kind]}s loaded`,
          detail: `${res.sent} ${KIND_PLURAL[kind]} handed to the gateway${this.isLive ? '.' : ' in dry run: checked, nothing sent to QuickBooks.'}`
        });
        this.loaded.emit(res);
        this.loadBlocked(1);
      },
      error: (err) => {
        this.running = { ...this.running, [kind]: false };
        this.messages.add({ severity: 'error', summary: 'Not loaded', detail: qboErrorMessage(err, `The ${KIND_PLURAL[kind]} could not be loaded.`) });
      }
    });
  }

  loadBlocked(page = 1): void {
    this.isLoadingBlocked = true;
    this.blockedFailed = false;

    this.service.getSyncItems({ state: 'Blocked', page, pageSize: this.pageSize }).subscribe({
      next: (res) => {
        this.isLoadingBlocked = false;
        this.blocked = res?.data ?? [];
        this.blockedTotal = res?.totalRecords ?? 0;
        this.blockedPage = page;
      },
      error: () => {
        this.isLoadingBlocked = false;
        this.blockedFailed = true;
      }
    });
  }

  onBlockedPage(event: TableLazyLoadEvent): void {
    const page = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize)) + 1;
    if (page !== this.blockedPage) this.loadBlocked(page);
  }

  labelOf(kind: string): string { return KIND_LABEL[kind as SyncKind] ?? kind; }
  iconOf(kind: string): string { return KIND_ICON[kind as SyncKind] ?? 'pi pi-circle'; }

  link(item: SyncItemModel): string[] | null {
    return sourceRoute(item.kind, item.externalId, item.sourceSystem);
  }
}
