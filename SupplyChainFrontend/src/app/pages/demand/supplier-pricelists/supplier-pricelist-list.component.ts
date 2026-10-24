import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { PricelistGridComponent } from '../../../shared/supplier-pricelist/pricelist-grid.component';
import { AuthService } from '../../service/auth.service';
import { FLOW } from '../../../shared/flow';

/** A45 §4 C4 — Purchase → Supplier Pricelists: every vendor price, filtered by product, vendor, active, and search. */
@Component({
  selector: 'app-supplier-pricelist-list',
  standalone: true,
  imports: [CommonModule, PricelistGridComponent, ...FLOW],
  template: `
<sf-page [crumbs]="['Procurement', 'Supplier pricelists']">
  <div class="sf-body">
    <div class="sf-lr-head">
      <div>
        <h1>Supplier pricelists</h1>
        <div class="sf-sub">What each vendor charges per product, quantity tier and period — used to price purchase order lines</div>
      </div>
    </div>
    <app-pricelist-grid [showFilters]="true" [canEdit]="canEdit"></app-pricelist-grid>
  </div>
</sf-page>`
})
export class SupplierPricelistListComponent {
  constructor(private auth: AuthService) {}

  /** Writing a price follows the purchase order write permissions (A45 D-8). */
  get canEdit(): boolean {
    return this.auth.hasPermission('PO_CREATE') || this.auth.hasPermission('PO_EDIT');
  }
}
