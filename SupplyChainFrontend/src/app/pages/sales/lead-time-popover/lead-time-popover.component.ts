import { Component, ElementRef, EventEmitter, HostListener, Input, OnDestroy, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TooltipModule } from 'primeng/tooltip';
import { DatePickerModule } from 'primeng/datepicker';
import { Observable, Subscription } from 'rxjs';

import { LeadTimeComponentResult, LeadTimeResultModel, leadTimeSourceLabel } from '../../../services/lead-time.service';
import { fromDateOnly, toDateOnly } from '../../../shared/date-only';
import { displayDate } from '../sale-inquiries/sale-inquiry.shared';
import { DeliveryDateState, deliveryDateState, longDate } from './lead-time-display';

/** What a calculation gave: the breakdown, and (from a saved line's endpoint) the line as the server stored it. */
export interface LeadTimeCalculation {
  leadTime: LeadTimeResultModel;
  line?: unknown;
}

/**
 * Apply, on a result: 'clear-manual' (the calculation is stored with the line, so dropping the manual date makes the
 * calculated one the line's date — D-15) or 'set-manual' (nothing stores the calculation, so the earliest date is
 * written as the line's own date).
 */
export type LeadTimeApplyMode = 'clear-manual' | 'set-manual';

const PANEL_WIDTH = 368;

/**
 * A34 PC-07/08/09 — a sales line's delivery date with where it comes from (⏱ Calculated / ✎ Manual / — Not
 * calculated), the ⏱ button that calculates the lead time on demand only (BR-C4-01), and a popover with the breakdown
 * (component, days, source), "Total: N days → Earliest delivery: …", Apply and Override date.
 *
 * The host decides which endpoint calculates (a saved line's own …/lead-time, or POST api/lead-time/calculate for an
 * unsaved form line) through `calculator`, and stores the manual date itself when `manualDateChange` fires.
 */
@Component({
  selector: 'app-lead-time-popover',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, TooltipModule, DatePickerModule],
  template: `
    <span class="lt" data-testid="lead-time">
      <span class="lt-date" data-testid="lt-date">{{ state.effective ? shortDate(state.effective) : '—' }}</span>
      <span class="lt-indicator" [ngClass]="'lt-' + state.source.toLowerCase()" [pTooltip]="state.tooltip"
            tooltipPosition="top" data-testid="lt-indicator">{{ state.label }}</span>
      <button *ngIf="state.source === 'MANUAL' && canOverride" type="button" class="lt-icon-btn" [disabled]="saving"
              (click)="clearManual()" aria-label="Clear the manual date" pTooltip="Clear the manual date"
              data-testid="lt-clear">×</button>
      <button *ngIf="showCalculate" type="button" class="lt-icon-btn lt-calc" [disabled]="loading || saving"
              (click)="calculate($event)" aria-label="Calculate the lead time" pTooltip="Calculate the lead time"
              data-testid="lt-calculate">
        <i *ngIf="loading" class="pi pi-spin pi-spinner"></i><span *ngIf="!loading">⏱</span>
      </button>
      <button *ngIf="canOverride" type="button" class="lt-icon-btn" [disabled]="saving"
              (click)="startOverride($event)" aria-label="Set the delivery date" pTooltip="Set the delivery date by hand"
              data-testid="lt-set-date">✎</button>
      <i *ngIf="saving" class="pi pi-spin pi-spinner lt-saving"></i>
    </span>

    <div *ngIf="open" class="lt-panel" [class.lt-panel-fixed]="!inline" [ngStyle]="inline ? null : panelStyle"
         role="dialog" aria-label="Lead time" data-testid="lt-panel">
      <div class="lt-panel-head">
        <strong>Lead time{{ label ? ' — ' + label : '' }}</strong>
        <button type="button" class="lt-icon-btn" (click)="close()" aria-label="Close" data-testid="lt-close">×</button>
      </div>

      <p *ngIf="loading" class="lt-muted" data-testid="lt-loading"><i class="pi pi-spin pi-spinner"></i> Calculating…</p>
      <p *ngIf="error && !loading" class="lt-error" role="alert" data-testid="lt-error">{{ error }}</p>

      <ng-container *ngIf="result && !loading && !overriding">
        <table class="lt-breakdown" data-testid="lt-breakdown">
          <tr *ngFor="let c of result.components" data-testid="lt-component">
            <td>{{ c.name }}</td>
            <td class="lt-num">{{ c.days }} day{{ c.days === 1 ? '' : 's' }}</td>
            <td>
              <span class="lt-badge" [attr.data-source]="c.source">{{ sourceLabel(c.source) }}</span>
              <span *ngIf="c.detail" class="lt-muted"> · {{ c.detail }}</span>
              <span *ngIf="!isCounted(c)" class="lt-muted" data-testid="lt-not-counted"
                    pTooltip="Shown for reference: it is not part of the total (materials come through the BOM)"> · not counted</span>
            </td>
          </tr>
        </table>
        <p class="lt-total" data-testid="lt-total">
          Total: {{ result.totalLeadTimeDays }} day{{ result.totalLeadTimeDays === 1 ? '' : 's' }} → Earliest delivery: {{ long(result.earliestDeliveryDate) }}
        </p>
        <p *ngIf="result.meetsRequestedDate === false" class="lt-warn" data-testid="lt-misses">
          <i class="pi pi-exclamation-triangle"></i> Later than the requested date{{ result.latestStartDate ? ' — it would have had to start by ' + long(result.latestStartDate) : '' }}.
        </p>
      </ng-container>

      <div class="lt-actions" *ngIf="!overriding && !loading">
        <ng-container *ngIf="canOverride; else noOverride">
          <p-button label="Apply" icon="pi pi-check" size="small" [disabled]="!result || saving"
                    pTooltip="Use the calculated date for this line" (onClick)="apply()" data-testid="lt-apply"></p-button>
          <p-button label="Override date" icon="pi pi-pencil" size="small" [outlined]="true" [disabled]="saving"
                    (onClick)="startOverride()" data-testid="lt-override"></p-button>
        </ng-container>
        <ng-template #noOverride>
          <small *ngIf="overrideNote" class="lt-muted" data-testid="lt-override-note">{{ overrideNote }}</small>
        </ng-template>
      </div>

      <div *ngIf="overriding" class="lt-override" data-testid="lt-override-editor">
        <p-datepicker [(ngModel)]="overrideValue" [inline]="true" data-testid="lt-override-date"></p-datepicker>
        <div class="lt-actions">
          <p-button label="Back" [text]="true" severity="secondary" size="small" (onClick)="overriding = false"></p-button>
          <p-button label="Save date" icon="pi pi-check" size="small" [disabled]="!overrideValue || saving"
                    (onClick)="saveOverride()" data-testid="lt-override-save"></p-button>
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host { display: inline-block; position: relative; }
    .lt { display: inline-flex; align-items: center; gap: .3rem; white-space: nowrap; }
    .lt-date { font-variant-numeric: tabular-nums; }
    .lt-indicator { font-size: .75rem; padding: 0 .3rem; border-radius: .25rem; }
    .lt-calculated { color: var(--text-color-secondary); }
    .lt-manual { color: var(--amber-800, #92400e); background: var(--amber-50, #fffbeb); border: 1px solid var(--amber-300, #fcd34d); }
    .lt-none { color: var(--surface-500, #9ca3af); }
    .lt-icon-btn {
      border: 0; background: none; cursor: pointer; padding: 0 .2rem; font: inherit; line-height: 1.2;
      color: var(--text-color-secondary); border-radius: .25rem;
    }
    .lt-icon-btn:hover:not(:disabled) { background: var(--surface-100); color: var(--text-color); }
    .lt-icon-btn:disabled { opacity: .5; cursor: default; }
    .lt-calc { font-size: 1rem; }
    .lt-saving { font-size: .75rem; color: var(--text-color-secondary); }
    .lt-panel {
      width: 368px; max-width: calc(100vw - 16px); background: var(--surface-0, #fff); color: var(--text-color);
      border: 1px solid var(--surface-200); border-radius: .5rem; box-shadow: 0 6px 24px rgba(0,0,0,.14);
      padding: .75rem; margin-top: .375rem; white-space: normal; text-align: left;
    }
    .lt-panel-fixed { position: fixed; z-index: 1102; margin-top: 0; }
    .lt-panel-head { display: flex; align-items: center; justify-content: space-between; gap: .5rem; margin-bottom: .5rem; }
    .lt-breakdown { width: 100%; border-collapse: collapse; font-size: .8125rem; }
    .lt-breakdown td { padding: .2rem .25rem; border-bottom: 1px solid var(--surface-100); vertical-align: top; }
    .lt-num { text-align: right; white-space: nowrap; }
    .lt-badge { font-size: .6875rem; padding: 0 .3rem; border-radius: .25rem; background: var(--surface-100); white-space: nowrap; }
    .lt-total { font-weight: 600; margin: .5rem 0 .25rem; }
    .lt-warn { color: var(--orange-700, #c2410c); font-size: .8125rem; margin: .25rem 0; }
    .lt-error { color: var(--red-600, #dc2626); font-size: .8125rem; margin: .25rem 0; }
    .lt-muted { color: var(--text-color-secondary); font-size: .8125rem; }
    .lt-actions { display: flex; gap: .5rem; justify-content: flex-end; margin-top: .5rem; flex-wrap: wrap; }
    .lt-override { display: flex; flex-direction: column; gap: .25rem; }
  `]
})
export class LeadTimePopoverComponent implements OnDestroy {
  @Input() variantUuid: string | null | undefined = null;
  @Input() quantity: number | null | undefined = null;
  /** The line's own date (yyyy-MM-dd or the server's date-only form); null = none. */
  @Input() manualDate: string | null | undefined = null;
  @Input() calculatedDate: string | null | undefined = null;
  @Input() calculatedDays: number | null | undefined = null;
  @Input() calculatedAt: string | null | undefined = null;
  /** The document's EDIT code and a status the calculation is allowed in. */
  @Input() canCalculate = false;
  /** The line's date may be set or cleared here. */
  @Input() canOverride = false;
  /** Shown in the popover in place of Apply / Override date when the date can't be set here. */
  @Input() overrideNote: string | null = null;
  @Input() calculator: (() => Observable<LeadTimeCalculation>) | null = null;
  @Input() applyMode: LeadTimeApplyMode = 'clear-manual';
  /** The host is storing a date: the actions wait. */
  @Input() saving = false;
  /** In the flow of the page (a dialog form) rather than floating over a table. */
  @Input() inline = false;
  /** Names the line in the popover's title, e.g. "Line 2: Custom Gear". */
  @Input() label: string | null = null;

  @Output() calculated = new EventEmitter<LeadTimeCalculation>();
  /** yyyy-MM-dd to set the line's date, null to clear it. */
  @Output() manualDateChange = new EventEmitter<string | null>();

  result: LeadTimeResultModel | null = null;
  loading = false;
  error: string | null = null;
  open = false;
  overriding = false;
  overrideValue: Date | null = null;
  panelStyle: Record<string, string> = {};

  private sub: Subscription | null = null;

  constructor(private host: ElementRef<HTMLElement>) {}

  get state(): DeliveryDateState {
    return deliveryDateState({
      manualDate: this.manualDate, calculatedDate: this.calculatedDate,
      calculatedDays: this.calculatedDays, calculatedAt: this.calculatedAt
    });
  }

  get showCalculate(): boolean {
    return this.canCalculate && !!this.calculator && !!this.variantUuid && (this.quantity ?? 0) > 0;
  }

  readonly sourceLabel = leadTimeSourceLabel;
  readonly long = longDate;

  shortDate(iso: string): string { return displayDate(iso); }

  /**
   * Contract v1.3 — a component flagged includedInTotal: false (SUPPLIER on a MANUFACTURE route) is shown but not
   * counted. The total shown is always the server's totalLeadTimeDays, never a sum of the rows.
   */
  isCounted(c: LeadTimeComponentResult): boolean {
    return (c as LeadTimeComponentResult & { includedInTotal?: boolean | null }).includedInTotal !== false;
  }

  calculate(event?: Event) {
    if (!this.showCalculate || this.loading) return;
    this.openAt(event);
    this.overriding = false;
    this.loading = true;
    this.error = null;
    this.sub?.unsubscribe();
    this.sub = this.calculator!().subscribe({
      next: (c) => {
        this.loading = false;
        this.result = c.leadTime;
        this.calculated.emit(c);
      },
      error: (err) => {
        this.loading = false;
        this.error = err?.error?.message ?? 'The lead time could not be calculated.';
      }
    });
  }

  /** Use the calculated date for the line (see LeadTimeApplyMode). */
  apply() {
    if (!this.result || !this.canOverride) return;
    if (this.applyMode === 'set-manual') {
      this.manualDateChange.emit(this.result.earliestDeliveryDate.slice(0, 10));
    } else if (this.state.source === 'MANUAL') {
      this.manualDateChange.emit(null);
    }
    this.close();
  }

  startOverride(event?: Event) {
    if (!this.canOverride) return;
    if (!this.open) this.openAt(event);
    const start = this.state.effective ?? this.result?.earliestDeliveryDate ?? null;
    this.overrideValue = start ? fromDateOnly(start) : null;
    this.overriding = true;
  }

  saveOverride() {
    if (!this.overrideValue || !this.canOverride) return;
    this.manualDateChange.emit(toDateOnly(this.overrideValue));
    this.close();
  }

  clearManual() {
    if (!this.canOverride) return;
    this.manualDateChange.emit(null);
  }

  close() {
    this.open = false;
    this.overriding = false;
  }

  /** Floats the popover under the button that opened it, kept on screen. */
  private openAt(event?: Event) {
    this.open = true;
    if (this.inline) return;
    const anchor = (event?.currentTarget as HTMLElement | null) ?? this.host.nativeElement;
    const rect = anchor.getBoundingClientRect();
    const width = Math.min(PANEL_WIDTH, window.innerWidth - 16);
    const left = Math.max(8, Math.min(rect.left, window.innerWidth - width - 8));
    this.panelStyle = { top: `${Math.round(rect.bottom + 6)}px`, left: `${Math.round(left)}px` };
  }

  @HostListener('document:keydown.escape')
  onEscape() { if (this.open) this.close(); }

  /** A click outside closes it (the date picker is inline, so picking a day stays inside). */
  @HostListener('document:mousedown', ['$event'])
  onDocumentMouseDown(event: MouseEvent) {
    if (this.open && event.target instanceof Node && !this.host.nativeElement.contains(event.target)) this.close();
  }

  /** A floating popover would drift from its line. */
  @HostListener('window:resize')
  @HostListener('window:scroll')
  onViewportChange() { if (this.open && !this.inline) this.close(); }

  ngOnDestroy() { this.sub?.unsubscribe(); }
}
