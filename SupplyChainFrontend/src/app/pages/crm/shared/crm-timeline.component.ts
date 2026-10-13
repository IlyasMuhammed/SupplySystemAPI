import { Component, EventEmitter, Input, OnDestroy, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { InputTextModule } from 'primeng/inputtext';
import { TooltipModule } from 'primeng/tooltip';
import { ActivityDocument, TimelineEntry, activityIcon, crmLabel } from '../../../services/crm.service';

/** What the quick-add buttons ask the parent to open (an activity type, or a document). */
export type TimelineQuickAdd = 'PHONE_CALL' | 'MEETING' | 'NOTE' | 'TASK' | 'EMAIL_SENT' | 'DOCUMENT';

const SCM_ROUTES: Record<string, string> = {
  INQUIRY: '/portal/pages/sales/inquiries', QUOTATION: '/portal/pages/sales/quotations', ORDER: '/portal/pages/sales/orders'
};

/**
 * A41 §12 / §15.3 — the conversation timeline of a lead or an opportunity: quick-add buttons, newest-first feed with an icon
 * per kind, documents with direction, status / stage / owner changes, linked sales documents, and (on an opportunity) the
 * source lead's history below an "Activities from lead" line. Paging is "Show more". A text search (server-side, over the
 * whole timeline) filters the feed; the feed scrolls inside its own box once it is taller than the screen.
 */
@Component({
  selector: 'app-crm-timeline',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, IconFieldModule, InputIconModule, InputTextModule, TooltipModule],
  template: `
<div class="ctl" data-testid="timeline">
  <div class="ctl-bar">
    <div class="ctl-add" *ngIf="canAdd">
      <p-button *ngFor="let b of quickAdds" size="small" [text]="true" [label]="b.label" [icon]="'pi ' + b.icon"
                (onClick)="add.emit(b.type)" [attr.data-testid]="'tl-add-' + b.type"></p-button>
    </div>
    <span class="sf-end" *ngIf="canExport">
      <p-button size="small" [text]="true" label="Export" icon="pi pi-file-excel" (onClick)="export.emit()" data-testid="tl-export"></p-button>
    </span>
  </div>
  <p-iconfield class="sf-search ctl-search">
    <p-inputicon styleClass="pi pi-search"></p-inputicon>
    <input pInputText type="text" [(ngModel)]="query" (ngModelChange)="onQuery()" placeholder="Search the timeline: subject, notes, people, documents"
           aria-label="Search the timeline" data-testid="tl-search" />
  </p-iconfield>

  <div class="sf-empty" *ngIf="!loading && !entries.length">{{ query.trim() ? 'Nothing in the timeline matches “' + query.trim() + '”.' : 'No activity yet.' }}</div>
  <div class="ctl-scroll" data-testid="tl-scroll">
  <ol class="ctl-feed">
    <ng-container *ngFor="let e of entries; let i = index">
      <li class="ctl-sep" *ngIf="e.fromLead && (i === 0 || !entries[i - 1].fromLead)" data-testid="tl-lead-separator">
        Activities from lead {{ e.leadNumber }}
      </li>
      <li class="ctl-item" [attr.data-testid]="'tl-' + e.entryType" [class.ctl-done]="e.isCompleted">
        <span class="ctl-dot" [ngClass]="dotClass(e)"><i class="pi" [ngClass]="icon(e)"></i></span>
        <div class="ctl-body">
          <div class="ctl-head">
            <strong>{{ title(e) }}</strong>
            <span class="ctl-when">{{ e.date | date:'dd MMM yyyy, HH:mm' }}</span>
          </div>

          <ng-container *ngIf="e.entryType === 'ACTIVITY'">
            <div class="ctl-sub">
              <span *ngIf="e.performedBy">{{ e.performedBy.name }}</span>
              <span *ngIf="e.contactPerson"> → {{ e.contactPerson.name }}</span>
              <span *ngIf="e.durationMinutes"> · {{ e.durationMinutes }} min</span>
              <span *ngIf="e.pipelineStage" class="sf-mu"> · {{ e.pipelineStage }}</span>
              <span *ngIf="e.isCompleted === false && e.activityType === 'TASK'" class="sf-pill wn ctl-pill">Open</span>
            </div>
            <div class="ctl-desc" *ngIf="e.description" [innerHTML]="e.description"></div>
            <div class="ctl-sub" *ngIf="e.outcome">Outcome: <span class="sf-pill" [ngClass]="outcomeTone(e.outcome)">{{ label(e.outcome) }}</span>
              <span *ngIf="e.outcomeNotes" class="sf-mu"> {{ e.outcomeNotes }}</span></div>
            <div class="ctl-sub" *ngIf="e.followUpDate"><i class="pi pi-calendar"></i> Next: {{ e.followUpType ? label(e.followUpType) + ' · ' : '' }}{{ e.followUpDate | date:'dd MMM yyyy' }}
              <span *ngIf="e.followUpNotes"> — {{ e.followUpNotes }}</span></div>
            <div class="ctl-sub ctl-actions" *ngIf="e.activityUuid">
              <a href="javascript:void(0)" (click)="openActivity.emit(e.activityUuid!)" data-testid="tl-open-activity">Open</a>
            </div>
          </ng-container>

          <div class="ctl-docs" *ngIf="e.documents.length">
            <button type="button" *ngFor="let d of e.documents" class="ctl-doc" (click)="download.emit(d)" [pTooltip]="docTip(d)"
                    data-testid="tl-document">
              <i class="pi pi-paperclip"></i> {{ d.documentName }} <span class="sf-mu">({{ direction(d.direction) }})</span>
            </button>
          </div>

          <div class="ctl-sub" *ngIf="isChange(e)">
            <span *ngIf="e.changedBy">by {{ e.changedBy.name }}</span><span *ngIf="e.reason"> · {{ e.reason }}</span>
          </div>

          <div class="ctl-sub" *ngIf="e.entryType === 'SCM_DOCUMENT'">
            <a class="sf-mono" [routerLink]="[scmRoute(e.scmKind), e.scmUuid]" data-testid="tl-scm-link">{{ e.scmNumber }}</a>
            <span class="sf-pill ctl-pill">{{ label(e.scmStatus) }}</span>
            <span *ngIf="e.grandTotal"> · {{ e.grandTotal | number:'1.2-2' }} {{ e.currencyCode }}</span>
          </div>
        </div>
      </li>
    </ng-container>
  </ol>
  <div class="ctl-more" *ngIf="entries.length < total">
    <p-button size="small" [text]="true" [loading]="loading" label="Show more" icon="pi pi-angle-down" (onClick)="loadMore.emit()" data-testid="tl-more"></p-button>
  </div>
  </div>
</div>`,
  styles: [`
    .ctl-bar { display: flex; align-items: center; gap: .25rem; margin-bottom: .5rem; flex-wrap: wrap; }
    .ctl-add { display: flex; flex-wrap: wrap; gap: .1rem; }
    .ctl-search { display: block; margin-bottom: .5rem; }
    .ctl-search input { width: 100%; }
    .ctl-scroll { max-height: calc(100vh - 16rem); min-height: 12rem; overflow-y: auto; overscroll-behavior: contain; padding-right: .25rem; }
    .ctl-feed { list-style: none; margin: 0; padding: 0; }
    .ctl-item { display: flex; gap: .65rem; padding: .6rem 0; border-bottom: 1px solid var(--sms-border-soft); }
    .ctl-dot { flex: 0 0 2rem; height: 2rem; border-radius: 50%; display: grid; place-items: center; background: var(--sms-primary-soft); color: var(--sms-primary-text); }
    .ctl-dot.st { background: var(--sms-violet-soft); color: var(--sms-violet); }
    .ctl-dot.scm { background: var(--sms-teal-soft); color: var(--sms-teal); }
    .ctl-dot.ok { background: var(--sms-ok-soft); color: var(--sms-ok); }
    .ctl-body { flex: 1; min-width: 0; }
    .ctl-head { display: flex; justify-content: space-between; gap: .5rem; }
    .ctl-when { color: var(--sms-text-muted); font-size: .8rem; white-space: nowrap; }
    .ctl-sub { color: var(--sms-text-muted); font-size: .85rem; margin-top: .15rem; }
    .ctl-desc { margin-top: .3rem; padding: .4rem .6rem; background: var(--sms-bg); border-radius: var(--sms-radius-sm); overflow-wrap: anywhere; }
    .ctl-desc ::ng-deep p { margin: 0 0 .3rem; }
    .ctl-docs { display: flex; flex-wrap: wrap; gap: .35rem; margin-top: .35rem; }
    .ctl-doc { border: 1px solid var(--sms-border); background: var(--sms-surface); border-radius: var(--sms-radius-sm); padding: .2rem .5rem; cursor: pointer; color: var(--sms-text); font-size: .85rem; }
    .ctl-pill { margin-left: .35rem; }
    .ctl-sep { padding: .6rem 0 .3rem; color: var(--sms-text-muted); font-size: .8rem; text-transform: uppercase; letter-spacing: .04em; border-bottom: 1px dashed var(--sms-border); }
    .ctl-more { text-align: center; padding-top: .5rem; }
    .ctl-done .ctl-head strong { color: var(--sms-text); }
  `]
})
export class CrmTimelineComponent implements OnDestroy {
  @Input() entries: TimelineEntry[] = [];
  @Input() total = 0;
  @Input() loading = false;
  @Input() canAdd = false;
  @Input() canExport = false;
  @Output() add = new EventEmitter<TimelineQuickAdd>();
  @Output() export = new EventEmitter<void>();
  @Output() loadMore = new EventEmitter<void>();
  @Output() openActivity = new EventEmitter<string>();
  @Output() download = new EventEmitter<ActivityDocument>();
  /** The search text, debounced; the parent reloads page 1 of the timeline with it. */
  @Output() searchChange = new EventEmitter<string>();

  query = '';
  private queryTimer: ReturnType<typeof setTimeout> | null = null;

  onQuery(): void {
    if (this.queryTimer) clearTimeout(this.queryTimer);
    this.queryTimer = setTimeout(() => this.searchChange.emit(this.query.trim()), 400);
  }

  ngOnDestroy(): void { if (this.queryTimer) clearTimeout(this.queryTimer); }

  readonly quickAdds: { type: TimelineQuickAdd; label: string; icon: string }[] = [
    { type: 'PHONE_CALL', label: 'Call', icon: 'pi-phone' },
    { type: 'MEETING', label: 'Meeting', icon: 'pi-users' },
    { type: 'NOTE', label: 'Note', icon: 'pi-pencil' },
    { type: 'TASK', label: 'Task', icon: 'pi-check-square' },
    { type: 'EMAIL_SENT', label: 'Email', icon: 'pi-send' },
    { type: 'DOCUMENT', label: 'Document', icon: 'pi-paperclip' }
  ];

  label = crmLabel;

  icon(e: TimelineEntry): string {
    switch (e.entryType) {
      case 'ACTIVITY': return activityIcon(e.activityType);
      case 'DOCUMENT': return 'pi-paperclip';
      case 'STATUS_CHANGE': return 'pi-flag';
      case 'STAGE_CHANGE': return 'pi-arrow-right';
      case 'ASSIGNMENT': return 'pi-user';
      case 'CREATED': return 'pi-plus';
      case 'CONVERTED': return 'pi-sync';
      case 'SCM_DOCUMENT': return 'pi-file';
      default: return 'pi-circle';
    }
  }

  dotClass(e: TimelineEntry): string {
    if (e.entryType === 'SCM_DOCUMENT') return 'scm';
    if (e.entryType === 'CONVERTED') return 'ok';
    return this.isChange(e) || e.entryType === 'CREATED' ? 'st' : '';
  }

  isChange(e: TimelineEntry): boolean {
    return e.entryType === 'STATUS_CHANGE' || e.entryType === 'STAGE_CHANGE' || e.entryType === 'ASSIGNMENT' || e.entryType === 'CONVERTED';
  }

  title(e: TimelineEntry): string {
    switch (e.entryType) {
      case 'ACTIVITY': return `${crmLabel(e.activityType)} — ${e.subject ?? ''}`;
      case 'DOCUMENT': return `Document — ${e.documentName ?? ''}`;
      case 'STATUS_CHANGE': return `Status: ${crmLabel(e.fromValue) || '—'} → ${crmLabel(e.toValue)}`;
      case 'STAGE_CHANGE': return `Stage: ${e.fromValue || '—'} → ${e.toValue ?? ''}`;
      case 'ASSIGNMENT': return `Assigned to ${e.toValue ?? 'nobody'}`;
      case 'CREATED': return 'Created';
      case 'CONVERTED': return `Converted to opportunity ${e.toValue ?? ''}`;
      case 'SCM_DOCUMENT': return `Sale ${crmLabel(e.scmKind).toLowerCase()} created`;
      default: return crmLabel(e.entryType);
    }
  }

  direction(d: string): string {
    return d === 'SENT_TO_CUSTOMER' ? '→ customer' : d === 'RECEIVED_FROM_CUSTOMER' ? '← customer' : 'internal';
  }

  docTip(d: ActivityDocument): string {
    return `${crmLabel(d.documentType)} · ${this.direction(d.direction)}${d.sharedByName ? ' · ' + d.sharedByName : ''}`;
  }

  outcomeTone(o: string): string {
    return o === 'SUCCESSFUL' || o === 'COMPLETED' ? 'ok' : o === 'UNSUCCESSFUL' || o === 'CANCELLED' ? 'er' : o === 'NO_RESPONSE' ? '' : 'wn';
  }

  scmRoute(kind: string | null | undefined): string { return SCM_ROUTES[kind ?? ''] ?? '/portal/pages/sales/orders'; }
}
