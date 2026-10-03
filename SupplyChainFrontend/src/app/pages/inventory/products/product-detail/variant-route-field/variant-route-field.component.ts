import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { SelectModule } from 'primeng/select';
import { TooltipModule } from 'primeng/tooltip';

import { FulfillmentRouteModel, FulfillmentRoutesService } from '../../../../../services/fulfillment-routes.service';

interface RouteOption { label: string; value: string; stepsText: string; inactive?: boolean; }

/**
 * A33-PB-04 — the variant's default fulfillment route, in the variant dialog (spec §4.3). A dropdown of the active
 * routes with the chosen route's steps under it; clearable. Editable only with FULFILLMENT_ROUTE_ASSIGN (the host
 * passes `canAssign`), read-only otherwise. The host saves it through PUT api/variants/{uuid}/fulfillment-route.
 */
@Component({
  selector: 'app-variant-route-field',
  standalone: true,
  imports: [CommonModule, FormsModule, SelectModule, TooltipModule],
  templateUrl: './variant-route-field.component.html',
  styleUrls: ['./variant-route-field.component.scss']
})
export class VariantRouteFieldComponent implements OnInit {
  @Input() value: string | null = null;
  @Input() canAssign = false;
  /** The variant's saved route, to show it even when it is no longer active or the routes can't be loaded. */
  @Input() currentCode: string | null = null;
  @Input() currentName: string | null = null;
  @Output() valueChange = new EventEmitter<string | null>();

  readonly infoText = 'This route determines the warehouse operations when this variant is sold. ' +
    'It can be overridden on each sale order line.';

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
    const options: RouteOption[] = this.routes.map(r => ({ label: `${r.name} (${r.code})`, value: r.uuid, stepsText: r.stepsText }));
    if (this.value && !options.some(o => o.value === this.value)) {
      const name = this.currentName ?? 'Current route';
      options.push({ label: `${name}${this.currentCode ? ` (${this.currentCode})` : ''} — inactive`, value: this.value, stepsText: '', inactive: true });
    }
    return options;
  }

  get selected(): RouteOption | null { return this.options.find(o => o.value === this.value) ?? null; }

  get clearable(): boolean { return this.canAssign && !!this.value; }

  onChange(value: string | null): void {
    if (!this.canAssign) return;
    this.value = value ?? null;
    this.valueChange.emit(this.value);
  }
}
