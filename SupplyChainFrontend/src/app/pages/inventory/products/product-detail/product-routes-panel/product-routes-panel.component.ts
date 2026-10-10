import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';

import {
  FulfillmentRoutesService, ProductVariantRouteModel, routeCategoryLabel, routeCategorySeverity
} from '../../../../../services/fulfillment-routes.service';

/**
 * A37 D-12 / RTE-02/03 — the product page's Routes panel: each variant's configured route, whether it is available
 * (its module on), the route that actually applies, and the server's warning. GET /api/products/{uuid}/routes.
 */
@Component({
  selector: 'app-product-routes-panel',
  standalone: true,
  imports: [CommonModule, TableModule, TagModule, TooltipModule],
  template: `
<div class="sf-card p0" data-testid="product-routes">
  <div class="sf-ch"><h2>Fulfillment routes</h2><span class="sf-mu sf-sm">configured on each variant, and what applies</span></div>
  <div *ngIf="failed" class="sf-empty" data-testid="product-routes-failed">{{ failed }}</div>
  <p-table *ngIf="!failed" [value]="rows" [loading]="loading" styleClass="p-datatable-sm" [tableStyle]="{ 'min-width': '52rem' }">
    <ng-template pTemplate="header">
      <tr><th>Variant</th><th>Configured route</th><th>Effective route</th><th>Note</th></tr>
    </ng-template>
    <ng-template pTemplate="body" let-r>
      <tr [attr.data-testid]="'route-row-' + r.variantUuid">
        <td><div>{{ r.variantName }}</div><small class="sf-mono sf-mu">{{ r.sku }}</small></td>
        <td [attr.data-testid]="'configured-' + r.variantUuid">
          <ng-container *ngIf="r.routeUuid; else noOwn">
            {{ r.routeName }}
            <p-tag *ngIf="r.category" [value]="categoryLabel(r.category)" [severity]="categorySeverity(r.category)" styleClass="ml-1"></p-tag>
            <span *ngIf="!r.isAvailable" class="sf-pill wn" [attr.data-testid]="'unavailable-' + r.variantUuid">Unavailable</span>
          </ng-container>
          <ng-template #noOwn><span class="sf-mu">None — organization default</span></ng-template>
        </td>
        <td [attr.data-testid]="'effective-' + r.variantUuid">{{ r.effectiveRouteName || '—' }}</td>
        <td [attr.data-testid]="'warning-' + r.variantUuid">
          <span *ngIf="r.warning" class="sf-pill wn"><i class="pi pi-exclamation-triangle"></i> {{ r.warning }}</span>
        </td>
      </tr>
    </ng-template>
    <ng-template pTemplate="emptymessage">
      <tr><td colspan="4" class="sf-empty">This product has no variants.</td></tr>
    </ng-template>
  </p-table>
</div>
  `,
  styles: [`.ml-1 { margin-left: .25rem; } .sf-pill { margin-left: .25rem; }`]
})
export class ProductRoutesPanelComponent implements OnChanges {
  @Input({ required: true }) productUuid!: string;
  /** 403 (no Logistics, or no route view code): the host hides the panel. */
  @Output() forbidden = new EventEmitter<void>();

  rows: ProductVariantRouteModel[] = [];
  loading = false;
  failed: string | null = null;

  readonly categoryLabel = routeCategoryLabel;
  readonly categorySeverity = routeCategorySeverity;

  constructor(private service: FulfillmentRoutesService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['productUuid'] && this.productUuid) this.load();
  }

  load(): void {
    this.loading = true;
    this.failed = null;
    this.service.getProductRoutes(this.productUuid).subscribe({
      next: res => { this.loading = false; this.rows = res.result ?? []; },
      error: err => {
        this.loading = false;
        this.rows = [];
        if (err?.status === 403) { this.failed = null; this.forbidden.emit(); return; }
        this.failed = err?.error?.message || 'The routes could not be loaded.';
      }
    });
  }
}
