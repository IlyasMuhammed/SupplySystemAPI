import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { SelectModule } from 'primeng/select';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';

import {
  FulfillmentRouteCategory, FulfillmentRouteModel, FulfillmentRoutesService, isRouteAvailable, routeCategoryLabel, routeCategoryOf,
  routeCategorySeverity, routeUnavailableReason, routesForVariant, unavailableRouteOptions
} from '../../../../../services/fulfillment-routes.service';

interface RouteOption {
  label: string;
  value: string;
  stepsText: string;
  category: FulfillmentRouteCategory;
  inactive?: boolean;
  /** The variant already has it, but it would be refused today (D-3 / D-9). */
  notAllowed?: boolean;
  /** A37 RTE-01 — the server marks it unavailable (its module is off): listed disabled, or kept when it is the variant's own. */
  disabled?: boolean;
  unavailableReason?: string;
}

/**
 * A33-PB-04 — the variant's default fulfillment route, in the variant dialog (spec §4.3). A dropdown of the active
 * routes with the chosen route's steps under it; clearable. Editable only with FULFILLMENT_ROUTE_ASSIGN (the host
 * passes `canAssign`), read-only otherwise. The host saves it through PUT api/variants/{uuid}/fulfillment-route.
 *
 * A34-PA-06/PA-10 (contract §4.2): MANUFACTURE (make-to-order) routes are offered only when the product's supply method
 * is MANUFACTURE (D-3) and the organization has MODULE_MANUFACTURING (D-9); the server refuses the rest with 400. The
 * chosen route's category shows as a badge (Stock green, Manufacture orange).
 */
@Component({
  selector: 'app-variant-route-field',
  standalone: true,
  imports: [CommonModule, FormsModule, SelectModule, TagModule, TooltipModule],
  templateUrl: './variant-route-field.component.html',
  styleUrls: ['./variant-route-field.component.scss']
})
export class VariantRouteFieldComponent implements OnInit {
  @Input() value: string | null = null;
  @Input() canAssign = false;
  /** The variant's saved route, to show it even when it is no longer active or the routes can't be loaded. */
  @Input() currentCode: string | null = null;
  @Input() currentName: string | null = null;
  /** A34 D-3: the product's supply method is MANUFACTURE. */
  @Input() productManufactured = false;
  /** A34 D-9: the organization has MODULE_MANUFACTURING. */
  @Input() manufacturingEnabled = false;
  @Output() valueChange = new EventEmitter<string | null>();

  readonly infoText = 'This route determines the warehouse operations when this variant is sold. ' +
    'It can be overridden on each sale order line.';
  readonly categoryLabel = routeCategoryLabel;
  readonly categorySeverity = routeCategorySeverity;

  routes: FulfillmentRouteModel[] = [];
  loading = false;
  loadFailed = false;

  constructor(private service: FulfillmentRoutesService) {}

  ngOnInit(): void {
    this.loading = true;
    this.service.getRoutes().subscribe({
      next: res => { this.loading = false; this.routes = res.result ?? []; },
      error: () => { this.loading = false; this.loadFailed = true; }
    });
  }

  get options(): RouteOption[] {
    const toOption = (r: FulfillmentRouteModel): RouteOption =>
      ({ label: `${r.name} (${r.code})`, value: r.uuid, stepsText: r.stepsText, category: routeCategoryOf(r) });
    const available = this.routes.filter(r => isRouteAvailable(r));
    const options = routesForVariant(available, this.productManufactured, this.manufacturingEnabled).map(toOption);
    if (this.value && !options.some(o => o.value === this.value)) {
      const saved = this.routes.find(r => r.uuid === this.value);
      if (saved && !isRouteAvailable(saved)) {
        options.push({ ...toOption(saved), unavailableReason: routeUnavailableReason(saved) });
      } else if (saved) {
        options.push({ ...toOption(saved), notAllowed: true });
      } else {
        const name = this.currentName ?? 'Current route';
        options.push({
          label: `${name}${this.currentCode ? ` (${this.currentCode})` : ''} — inactive`, value: this.value, stepsText: '',
          category: 'STOCK', inactive: true
        });
      }
    }
    // A37 — the other unavailable routes are shown, disabled, with the reason.
    for (const u of unavailableRouteOptions(this.routes, options.map(o => o.value), true)) {
      const route = this.routes.find(r => r.uuid === u.value)!;
      options.push({ ...toOption(route), label: u.label, disabled: true, unavailableReason: u.reason });
    }
    return options;
  }

  get selected(): RouteOption | null { return this.options.find(o => o.value === this.value) ?? null; }

  get clearable(): boolean { return this.canAssign && !!this.value; }

  /** Make-to-order routes exist but are held back because the product isn't manufactured (D-3). */
  get manufactureRoutesHidden(): boolean {
    return this.manufacturingEnabled && !this.productManufactured && this.routes.some(r => routeCategoryOf(r) === 'MANUFACTURE');
  }

  onChange(value: string | null): void {
    if (!this.canAssign) return;
    this.value = value ?? null;
    this.valueChange.emit(this.value);
  }
}
