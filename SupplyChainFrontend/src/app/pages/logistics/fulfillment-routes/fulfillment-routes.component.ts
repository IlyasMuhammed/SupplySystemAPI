import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { SelectModule } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable } from 'rxjs';

import {
  AssignRouteByCategoryResult, FulfillmentRouteCategory, FulfillmentRouteModel, FulfillmentRoutesService, routeCategoryLabel,
  routeCategoryOf, routeCategorySeverity, routesVisibleToOrg
} from '../../../services/fulfillment-routes.service';
import { AuthService } from '../../service/auth.service';
import { TenantService } from '../../service/tenant.service';
import { FulfillmentRouteEditorComponent, RouteSavedEvent } from './fulfillment-route-editor/fulfillment-route-editor.component';
import { FulfillmentRouteAssignComponent } from './fulfillment-route-assign/fulfillment-route-assign.component';
import { abbreviatedSteps, defaultClassLabel } from './fulfillment-routes.shared';

/**
 * A33-PA-07 — Settings → Fulfillment Routes (spec §10.1, contract §3). Lists every route of the organization with its
 * abbreviated step chain, the default marker of each class (shipping / self-pickup, L-1) and the system lock.
 *
 * Gating (contract §2): the page opens with FULFILLMENT_ROUTE_VIEW or _MANAGE; every change needs _MANAGE; "Assign to
 * category" needs _ASSIGN. System routes can't be deleted and their steps are locked (D-10); a default route can't be
 * deactivated or deleted (L-5) — the server refuses all of these too.
 *
 * A34-PA-09: a category column and filter; no "make default" on a MANUFACTURE route (D-6); MANUFACTURE routes are
 * hidden from an organization without MODULE_MANUFACTURING (D-9, contract §3: the server lists them regardless).
 */
@Component({
  selector: 'app-fulfillment-routes',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, CheckboxModule, ConfirmDialogModule, DialogModule, SelectModule, TableModule,
    TagModule, ToastModule, TooltipModule, FulfillmentRouteEditorComponent, FulfillmentRouteAssignComponent
  ],
  templateUrl: './fulfillment-routes.component.html',
  styleUrls: ['./fulfillment-routes.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class FulfillmentRoutesComponent implements OnInit {
  routes: FulfillmentRouteModel[] = [];
  isLoading = false;
  loadFailed = false;
  showInactive = true;
  busy: Record<string, boolean> = {};

  editorVisible = false;
  editorReadOnly = false;
  editing: FulfillmentRouteModel | null = null;

  assignVisible = false;
  assigning: FulfillmentRouteModel | null = null;

  constructor(
    private service: FulfillmentRoutesService,
    private authService: AuthService,
    private messages: MessageService,
    private confirmation: ConfirmationService
  ) {}

  ngOnInit(): void { this.load(); }

  get canManage(): boolean { return this.authService.hasPermission('FULFILLMENT_ROUTE_MANAGE'); }
  get canAssign(): boolean { return this.authService.hasPermission('FULFILLMENT_ROUTE_ASSIGN'); }

  // ── A34 category ─────────────────────────────────────────────────────────────────────────────────────────
  private readonly tenantService = inject(TenantService);
  readonly categoryLabel = (r: FulfillmentRouteModel) => routeCategoryLabel(routeCategoryOf(r));
  readonly categorySeverity = (r: FulfillmentRouteModel) => routeCategorySeverity(routeCategoryOf(r));
  categoryFilter: FulfillmentRouteCategory | null = null;

  get manufacturingEnabled(): boolean { return this.tenantService.hasFeature('MODULE_MANUFACTURING'); }

  get categoryFilterOptions(): { label: string; value: FulfillmentRouteCategory | null }[] {
    const options: { label: string; value: FulfillmentRouteCategory | null }[] = [
      { label: 'All categories', value: null }, { label: 'Stock', value: 'STOCK' }
    ];
    if (this.manufacturingEnabled) options.push({ label: 'Manufacture', value: 'MANUFACTURE' });
    return options;
  }

  isManufacture(route: FulfillmentRouteModel): boolean { return routeCategoryOf(route) === 'MANUFACTURE'; }

  /** The routes this organization may see (D-9). */
  private get orgRoutes(): FulfillmentRouteModel[] { return routesVisibleToOrg(this.routes, this.manufacturingEnabled); }

  get visibleRoutes(): FulfillmentRouteModel[] {
    return this.orgRoutes.filter(r =>
      (this.showInactive || r.isActive) && (!this.categoryFilter || routeCategoryOf(r) === this.categoryFilter));
  }

  get inactiveCount(): number { return this.orgRoutes.filter(r => !r.isActive).length; }

  stepChain(route: FulfillmentRouteModel): string {
    return abbreviatedSteps([...route.steps].sort((a, b) => a.stepOrder - b.stepOrder).map(s => s.stepCode));
  }

  defaultLabel(route: FulfillmentRouteModel): string { return route.requiresShipping ? 'Shipping' : 'Self-pickup'; }

  defaultTooltip(route: FulfillmentRouteModel): string {
    return `The default route for ${defaultClassLabel(route.requiresShipping)}`;
  }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getRoutes(true).subscribe({
      next: res => { this.isLoading = false; this.routes = res.result ?? []; },
      error: () => { this.isLoading = false; this.loadFailed = true; this.routes = []; }
    });
  }

  // ── Editor ──────────────────────────────────────────────────────────────────────────────────────────────

  openCreate(): void {
    if (!this.canManage) return;
    this.editing = null;
    this.editorReadOnly = false;
    this.editorVisible = true;
  }

  openEdit(route: FulfillmentRouteModel): void {
    this.editing = route;
    this.editorReadOnly = !this.canManage;
    this.editorVisible = true;
  }

  onSaved(event: RouteSavedEvent): void {
    this.editorVisible = false;
    this.messages.add({ severity: 'success', summary: event.created ? `${event.route.code} created` : `${event.route.code} saved` });
    if (event.warning) this.messages.add({ severity: 'warn', summary: 'Default not changed', detail: event.warning, life: 8000 });
    this.load();
  }

  // ── Bulk assign (D-14) ──────────────────────────────────────────────────────────────────────────────────

  openAssign(route: FulfillmentRouteModel): void {
    if (!this.canAssign || !route.isActive) return;
    this.assigning = route;
    this.assignVisible = true;
  }

  onAssigned(result: AssignRouteByCategoryResult): void {
    this.messages.add({ severity: 'success', summary: 'Route assigned', detail: `${result.updated} updated, ${result.skipped} skipped.` });
  }

  // ── Default (L-1: one per class) ────────────────────────────────────────────────────────────────────────

  confirmSetDefault(route: FulfillmentRouteModel): void {
    if (this.isManufacture(route)) return; // D-6: the server refuses it (400)
    const cls = defaultClassLabel(route.requiresShipping);
    const current = this.routes.find(r => r.isDefault && r.requiresShipping === route.requiresShipping && r.uuid !== route.uuid);
    this.confirmation.confirm({
      key: 'fulfillment-routes',
      header: `Make ${route.code} the default?`,
      message: `Lines of ${cls} with no route of their own will use ${route.name}.` +
        (current ? ` It replaces ${current.name} (${current.code}) as the default for ${cls}.` : ''),
      icon: 'pi pi-star',
      acceptLabel: 'Make default', rejectLabel: 'Cancel',
      accept: () => this.run(route, this.service.setDefault(route.uuid), `${route.code} is now the default for ${cls}`)
    });
  }

  confirmClearDefault(route: FulfillmentRouteModel): void {
    const cls = defaultClassLabel(route.requiresShipping);
    this.confirmation.confirm({
      key: 'fulfillment-routes',
      header: `Clear the default for ${cls}?`,
      message: `There will be no default route for ${cls}: their lines with no route of their own (on the line or the ` +
        `product variant) will block confirmation until one is set.`,
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Clear default', rejectLabel: 'Keep it',
      accept: () => this.run(route, this.service.clearDefault(route.uuid), `${route.code} is no longer a default`)
    });
  }

  // ── Activate / deactivate / delete ──────────────────────────────────────────────────────────────────────

  confirmDeactivate(route: FulfillmentRouteModel): void {
    if (route.isDefault) return;
    this.confirmation.confirm({
      key: 'fulfillment-routes',
      header: `Deactivate ${route.code}?`,
      message: 'It can no longer be chosen for a variant or a sale order line. A route still used by active variants or ' +
        'open sale order lines can\'t be deactivated; deliveries already created keep their route.',
      icon: 'pi pi-ban',
      acceptLabel: 'Deactivate', rejectLabel: 'Keep active',
      accept: () => this.run(route, this.service.deactivateRoute(route.uuid), `${route.code} deactivated`)
    });
  }

  activate(route: FulfillmentRouteModel): void {
    this.run(route, this.service.activateRoute(route.uuid), `${route.code} reactivated`);
  }

  confirmDelete(route: FulfillmentRouteModel): void {
    if (route.isSystem || route.isDefault) return;
    this.confirmation.confirm({
      key: 'fulfillment-routes',
      header: `Delete ${route.code}?`,
      message: 'Only a route nothing refers to (no variant, sale order line or delivery) can be deleted; otherwise deactivate it.',
      icon: 'pi pi-trash',
      acceptLabel: 'Delete', rejectLabel: 'Keep',
      acceptButtonStyleClass: 'p-button-danger',
      accept: () => this.run(route, this.service.deleteRoute(route.uuid), `${route.code} deleted`)
    });
  }

  private run(route: FulfillmentRouteModel, call: Observable<unknown>, done: string): void {
    if (!this.canManage || this.busy[route.uuid]) return;
    this.busy[route.uuid] = true;
    call.subscribe({
      next: () => {
        this.busy[route.uuid] = false;
        this.messages.add({ severity: 'success', summary: done });
        this.load();
      },
      error: err => {
        this.busy[route.uuid] = false;
        this.messages.add({ severity: 'error', summary: 'Not changed', detail: err?.error?.message || 'The route could not be changed.', life: 8000 });
      }
    });
  }
}
