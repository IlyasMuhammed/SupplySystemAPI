import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';

import { AgingDetailLine, AgingPartnerLine, AgingReport } from '../../../../services/ar-ap.service';
import { money, reportLabels } from '../ar-ap.shared';

/** A row's line-level detail as the parent loaded it. */
export type AgingDetailState = AgingDetailLine[] | 'loading' | 'error';

/**
 * A48 tasks 541 / 543 / 544 / 558 / 559 — the aged receivables / payables table shared by both dashboards: one column
 * per bucket, a total row, the partner name drilling down (`partnerLink`), and a row click expanding the partner's open
 * documents (the parent loads them with `includeDetails` and passes them back in `details`). Base currency (D-13).
 */
@Component({
  selector: 'app-aging-table',
  standalone: true,
  imports: [CommonModule, RouterModule],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<div class="gl-scroll">
  <table class="aa-table" [style.min-width.rem]="18 + labels.length * 7" data-testid="aging-table">
    <thead><tr>
      <th style="width:2rem"></th>
      <th>{{ partnerLabel }}</th>
      <th *ngFor="let l of labels" class="gl-num">{{ l }}</th>
      <th class="gl-num">Total</th>
      <ng-container *ngIf="hasCredits"><th class="gl-num">Credits</th><th class="gl-num">Net</th></ng-container>
    </tr></thead>
    <tbody>
      <ng-container *ngFor="let line of report?.lines ?? []">
        <tr class="aa-click" (click)="toggle(line)" [attr.data-testid]="'aging-row-' + line.partner.uuid">
          <td><i class="pi" [ngClass]="isOpen(line) ? 'pi-chevron-down' : 'pi-chevron-right'" aria-hidden="true"></i></td>
          <td>
            <a *ngIf="partnerLink; else plain" [routerLink]="partnerLink(line.partner.uuid)" (click)="$event.stopPropagation()"
               [attr.data-testid]="'aging-link-' + line.partner.uuid">{{ line.partner.name }}</a>
            <ng-template #plain>{{ line.partner.name }}</ng-template>
            <span class="sf-mu sf-sm" *ngIf="line.partner.code"> · {{ line.partner.code }}</span>
            <span class="sf-pill er" *ngIf="line.onHold" style="margin-left:.35rem">Hold</span>
          </td>
          <td *ngFor="let l of labels; let i = index" class="gl-num" [class.aa-bad]="i >= 3 && amountAt(line, i) > 0">{{ cell(amountAt(line, i)) }}</td>
          <td class="gl-num"><b>{{ amount(line.totalOutstanding) }}</b></td>
          <ng-container *ngIf="hasCredits"><td class="gl-num">{{ line.unappliedCredits ? '−' + amount(line.unappliedCredits) : '' }}</td>
            <td class="gl-num">{{ amount(line.netBalance ?? line.totalOutstanding - (line.unappliedCredits ?? 0)) }}</td></ng-container>
        </tr>
        <ng-container *ngIf="isOpen(line)">
          <tr class="aa-sub" *ngIf="details[line.partner.uuid] === 'loading'"><td></td><td [attr.colspan]="span"><i class="pi pi-spin pi-spinner"></i> Loading the documents…</td></tr>
          <tr class="aa-sub" *ngIf="details[line.partner.uuid] === 'error'"><td></td><td [attr.colspan]="span" class="aa-bad">The documents could not be loaded.</td></tr>
          <tr class="aa-sub" *ngIf="isList(details[line.partner.uuid]) && !asList(details[line.partner.uuid]).length">
            <td></td><td [attr.colspan]="span">No open documents.</td></tr>
          <tr class="aa-sub" *ngFor="let d of asList(details[line.partner.uuid])" [attr.data-testid]="'aging-detail-' + d.documentNumber">
            <td></td>
            <td><span class="sf-mono">{{ d.documentNumber }}</span>
              <span class="sf-mu"> · {{ d.documentDate | date:'dd MMM yyyy' }}<ng-container *ngIf="d.dueDate"> · due {{ d.dueDate | date:'dd MMM yyyy' }}</ng-container></span>
              <span class="sf-mu" *ngIf="d.daysOverdue > 0"> · {{ d.daysOverdue }}d overdue</span></td>
            <td *ngFor="let l of labels" class="gl-num">{{ l === d.bucketLabel ? amount(d.openAmountBase) : '' }}</td>
            <td class="gl-num sf-mu">{{ d.currencyCode && d.currencyCode !== baseCurrency ? amount(d.openAmount) + ' ' + d.currencyCode : '' }}</td>
          </tr>
        </ng-container>
      </ng-container>
      <tr *ngIf="!report?.lines?.length"><td [attr.colspan]="span + 1" class="sf-empty" data-testid="aging-empty">{{ emptyText }}</td></tr>
    </tbody>
    <tfoot *ngIf="report?.lines?.length">
      <tr data-testid="aging-total">
        <td></td><td>TOTAL <span class="sf-mu sf-sm">{{ report?.totals?.totalPartners ?? report?.lines?.length }} · {{ report?.totals?.totalOpenItems ?? 0 }} documents</span></td>
        <td *ngFor="let l of labels; let i = index" class="gl-num">{{ cell(report?.totals?.bucketTotals?.[i] ?? 0) }}</td>
        <td class="gl-num">{{ amount(report?.totals?.grandTotal ?? report?.totalOutstanding ?? 0) }}</td>
        <ng-container *ngIf="hasCredits"><td class="gl-num">{{ creditsTotal ? '−' + amount(creditsTotal) : '' }}</td>
          <td class="gl-num" data-testid="aging-net">{{ amount(report?.totals?.netBalance ?? (report?.totals?.grandTotal ?? 0) - creditsTotal) }}</td></ng-container>
      </tr>
    </tfoot>
  </table>
</div>`
})
export class AgingTableComponent {
  @Input() report: AgingReport | null = null;
  @Input() partnerLabel = 'Customer';
  @Input() emptyText = 'Nothing outstanding.';
  @Input() baseCurrency: string | null = null;
  /** Route of a partner's drill-down page; none → plain names. */
  @Input() partnerLink: ((uuid: string) => any[]) | null = null;
  @Input() details: Record<string, AgingDetailState> = {};
  /** A row was opened (the parent loads its detail when it has none yet). */
  @Output() expand = new EventEmitter<string>();

  private open = new Set<string>();

  get labels(): string[] { return reportLabels(this.report); }
  /** D-RPT-2 — the credits column shows only when some partner has unapplied credit. */
  get hasCredits(): boolean { return (this.report?.lines ?? []).some(l => !!l.unappliedCredits); }
  /** Columns after the chevron: partner, buckets, total (+ credits, net). */
  get span(): number { return this.labels.length + 2 + (this.hasCredits ? 2 : 0); }
  get creditsTotal(): number {
    return this.report?.totals?.unappliedCredits ?? (this.report?.lines ?? []).reduce((s, l) => s + Number(l.unappliedCredits || 0), 0);
  }

  isOpen(l: AgingPartnerLine): boolean { return this.open.has(l.partner.uuid); }

  toggle(l: AgingPartnerLine): void {
    const id = l.partner.uuid;
    if (this.open.has(id)) { this.open.delete(id); return; }
    this.open.add(id);
    if (l.details?.length && !this.details[id]) this.details = { ...this.details, [id]: l.details };
    if (!this.details[id]) this.expand.emit(id);
  }

  amountAt(l: AgingPartnerLine, i: number): number { return Number(l.bucketAmounts?.[i] ?? 0); }
  cell(n: number): string { return n ? money(n) : '—'; }
  amount(n: number | null | undefined): string { return money(n); }
  isList(v: AgingDetailState | undefined): boolean { return Array.isArray(v); }
  asList(v: AgingDetailState | undefined): AgingDetailLine[] { return Array.isArray(v) ? v : []; }
}
