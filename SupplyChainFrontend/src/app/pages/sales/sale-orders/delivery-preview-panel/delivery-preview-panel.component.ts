import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

import { SaleOrderDeliveryPreviewModel } from '../../../../services/sale-order.service';
import { DeliveryPreviewView, PreviewLineRef, buildDeliveryPreviewView } from '../fulfillment-route-display';

/**
 * A33 PC-08 — "On confirmation, N delivery orders will be created": the lines grouped by route (and warehouse, once
 * known), and the lines no delivery can carry yet. Shown on a DRAFT only; computed by the server, nothing saved.
 * Collapsible.
 */
@Component({
  selector: 'app-delivery-preview-panel',
  standalone: true,
  imports: [CommonModule],
  template: `
    <section class="preview-panel" data-testid="delivery-preview" [class.collapsed]="collapsed">
      <button type="button" class="preview-head" (click)="collapsed = !collapsed" [attr.aria-expanded]="!collapsed"
              data-testid="delivery-preview-toggle">
        <i class="pi" [ngClass]="collapsed ? 'pi-chevron-right' : 'pi-chevron-down'"></i>
        <span class="preview-title">Delivery preview</span>
        <i *ngIf="loading" class="pi pi-spin pi-spinner muted" aria-label="Updating"></i>
        <span *ngIf="collapsed && view" class="muted">— {{ headline(view) }}</span>
      </button>

      <div *ngIf="!collapsed" class="preview-body" data-testid="delivery-preview-body">
        <p *ngIf="failed" class="preview-warn" data-testid="delivery-preview-failed">
          <i class="pi pi-info-circle"></i> The preview could not be worked out just now. Saving is not affected.
        </p>

        <ng-container *ngIf="view as v">
          <p class="preview-headline" data-testid="delivery-preview-headline">{{ headline(v) }}</p>

          <ul class="preview-groups">
            <li *ngFor="let g of v.groups; let k = index" data-testid="delivery-preview-group">
              <div class="group-head">
                <span class="group-no">DO-{{ k + 1 }}</span>
                <strong>{{ g.routeName }}</strong>
                <span class="muted">({{ g.stepsText }})</span>
                <span class="mode">{{ g.modeLabel }}</span>
                <span *ngIf="g.warehouseName" class="muted">· from {{ g.warehouseName }}</span>
              </div>
              <div class="group-lines">
                <span *ngFor="let l of g.lines; let last = last">
                  Line {{ l.number }}: {{ l.description }}<ng-container *ngIf="l.quantity != null"> × {{ l.quantity | number:'1.0-4' }}</ng-container>{{ last ? '' : ' · ' }}
                </span>
              </div>
            </li>
          </ul>

          <ul *ngIf="v.unroutable.length" class="preview-unroutable" data-testid="delivery-preview-unroutable">
            <li *ngFor="let u of v.unroutable">
              <i class="pi pi-exclamation-triangle"></i> Line {{ u.number }}: {{ u.description }} — {{ u.message }}
            </li>
          </ul>

          <p *ngIf="v.exempt.length" class="muted" data-testid="delivery-preview-exempt">
            No delivery needed:
            <ng-container *ngFor="let e of v.exempt; let last = last">Line {{ e.number }} ({{ e.description }}){{ last ? '.' : ', ' }}</ng-container>
          </p>

          <ul *ngIf="v.orderBlockers.length" class="preview-unroutable" data-testid="delivery-preview-order-blockers">
            <li *ngFor="let b of v.orderBlockers"><i class="pi pi-exclamation-triangle"></i> {{ b }}</li>
          </ul>
        </ng-container>

        <p *ngIf="!view && !failed && loading" class="muted">Working out the deliveries…</p>
      </div>
    </section>
  `,
  styles: [`
    .preview-panel { border: 1px solid var(--surface-200); border-radius: .625rem; background: var(--surface-0); margin: 1rem 0; }
    .preview-head {
      display: flex; align-items: center; gap: .5rem; width: 100%; padding: .75rem 1rem; border: 0; background: none;
      cursor: pointer; text-align: left; font: inherit; color: inherit;
    }
    .preview-title { font-weight: 700; }
    .preview-body { padding: 0 1rem 1rem; }
    .preview-headline { font-weight: 600; margin: 0 0 .5rem; }
    .preview-groups, .preview-unroutable { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: .5rem; }
    .group-head { display: flex; flex-wrap: wrap; gap: .375rem; align-items: baseline; }
    .group-no { font-family: monospace; color: var(--text-color-secondary); }
    .mode { font-size: .8125rem; padding: 0 .375rem; border-radius: .25rem; background: var(--surface-100); }
    .group-lines { font-size: .8125rem; color: var(--text-color-secondary); margin-left: 1rem; }
    .preview-unroutable { margin-top: .625rem; color: var(--orange-700, #c2410c); font-size: .875rem; }
    .preview-warn { color: var(--orange-700, #c2410c); font-size: .875rem; }
    .muted { color: var(--text-color-secondary); font-size: .8125rem; }
  `]
})
export class DeliveryPreviewPanelComponent {
  @Input() preview: SaleOrderDeliveryPreviewModel | null = null;
  @Input() loading = false;
  @Input() failed = false;
  /** Renames the server's line numbers to the page's own; the preview's own numbering when absent. */
  @Input() lineRef: ((lineNumber: number) => PreviewLineRef | null) | null = null;

  collapsed = false;

  get view(): DeliveryPreviewView | null {
    return this.preview ? buildDeliveryPreviewView(this.preview, this.lineRef ?? undefined) : null;
  }

  headline(v: DeliveryPreviewView): string {
    const n = v.deliveryCount;
    if (n === 0) return 'No delivery order can be created yet.';
    return `On confirmation, ${n} delivery order${n === 1 ? '' : 's'} will be created${v.canConfirm ? '' : ' once the problems below are fixed'}:`;
  }
}
