import { Component, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { Observable, forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TableModule } from 'primeng/table';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  SERVICE_PRIORITY_OPTIONS, ServiceLedgerEntry, ServiceLedgerNet, ServiceMaterial, ServiceOrderAction, ServiceOrderDetail,
  ServiceOrderService, readinessIcon, servicePriorityLabel, serviceStatusLabel, serviceStatusTone
} from '../../../../services/service-order.service';
import { ApiResponse, InventoryService, ProductListItemModel, WarehouseModel } from '../../../../services/inventory.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { UserService } from '../../../../services/user.service';
import { AuthService } from '../../../service/auth.service';
import { AssigneeOption, assigneeOptions$ } from '../../../sales/sale-inquiries/sale-inquiry.shared';
import { ProductVariantPickerComponent, VariantPickerSelection } from '../../../../shared/product-variant-picker/product-variant-picker.component';
import { TimelinePanelComponent } from '../../../../shared/timeline-panel/timeline-panel.component';
import { FLOW, FlowAnchorsComponent, FlowSection, FlowStage, flowLabel, flowStagesFrom, flowTone } from '../../../../shared/flow';
import { PartnerOption, SERVICE_ORDERS_ROUTE, customerOptions$, fromHhmm, fromYmd, hhmm, serverMessage, ymd } from '../service-order-shared';

const STAGES = ['Draft', 'Planned', 'Materials', 'Ready', 'In progress', 'Completed', 'Closed'];
const STAGE_INDEX: Record<string, number> = {
  DRAFT: 0, PLANNED: 1, MATERIAL_PENDING: 2, WAITING: 2, READY: 3, IN_PROGRESS: 4, COMPLETED: 5, CLOSED: STAGES.length
};
/** Before these, everything on the order may be edited; afterwards only its notes (SVC-12). */
const FULL_EDIT = ['DRAFT', 'PLANNED'];
/** IN_PROGRESS too: stock reserved for a resumed job by a run elsewhere (GRN page) is issued from here (API-CONTRACT note 7). */
const RESERVABLE = ['PLANNED', 'MATERIAL_PENDING', 'WAITING', 'READY', 'IN_PROGRESS'];
const COMPLETION_VISIBLE = ['IN_PROGRESS', 'COMPLETED', 'CLOSED'];

export const COMPLETE_CONFIRM_MESSAGE = 'This will finalize consumption and return unused materials to warehouse.';

export interface CompletionRow { material: ServiceMaterial; consumed: number; }

/** A36-P2-13 / P3-09 / P3-10 / P4-05 / P4-06 — the service order object page. */
@Component({
  selector: 'app-service-order-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, CalendarModule, CheckboxModule, ConfirmDialogModule, DialogModule, DropdownModule, InputNumberModule, InputTextModule,
    TableModule, TextareaModule, ToastModule, TooltipModule,
    ProductVariantPickerComponent, TimelinePanelComponent, ...FLOW
  ],
  templateUrl: './service-order-detail.component.html',
  styleUrls: ['../_service-shared.scss'],
  providers: [MessageService, ConfirmationService]
})
export class ServiceOrderDetailComponent implements OnInit {
  @ViewChild(FlowAnchorsComponent) anchors?: FlowAnchorsComponent;

  readonly route = SERVICE_ORDERS_ROUTE;
  readonly tone = serviceStatusTone;
  readonly statusLabel = serviceStatusLabel;
  readonly priorityLabel = servicePriorityLabel;
  readonly readiness = readinessIcon;
  readonly flowTone = flowTone;
  readonly flowLabel = flowLabel;
  readonly priorityOptions = SERVICE_PRIORITY_OPTIONS;

  uuid = '';
  order: ServiceOrderDetail | null = null;
  isLoading = true;
  loadFailed = false;
  busy = false;

  // ── Details edit ───────────────────────────────────────────────────────
  editing = false;
  edit = {
    customerUuid: null as string | null, quantity: null as number | null, warehouseUuid: null as string | null,
    assignedUserId: null as number | null, assignedRoleId: null as number | null,
    scheduledDate: null as Date | null, scheduledTime: null as Date | null,
    estimatedHours: null as number | null, priority: 1, notes: ''
  };
  customerOptions: PartnerOption[] = [];
  warehouseOptions: { label: string; value: string }[] = [];
  userOptions: AssigneeOption[] = [];
  roleOptions: { label: string; value: number }[] = [];
  private optionsLoaded = false;

  // ── Ad-hoc materials ───────────────────────────────────────────────────
  materialProducts: ProductListItemModel[] = [];
  private materialProductsLoaded = false;
  adhoc = { productUuid: null as string | null, variantUuid: null as string | null, quantity: null as number | null, notes: '' };

  // ── Completion ─────────────────────────────────────────────────────────
  completionRows: CompletionRow[] = [];
  actualHours: number | null = null;
  completionNotes = '';
  customerSignature = false;

  // ── Cancel ─────────────────────────────────────────────────────────────
  cancelVisible = false;
  cancelReason = '';

  constructor(
    private routeInfo: ActivatedRoute,
    private service: ServiceOrderService,
    private inventory: InventoryService,
    private partners: BusinessPartnerService,
    private users: UserService,
    private auth: AuthService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit(): void {
    this.uuid = this.routeInfo.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getById(this.uuid).subscribe({
      next: res => {
        this.isLoading = false;
        if (res.success && res.result) this.setOrder(res.result);
        else this.loadFailed = true;
      },
      error: () => { this.isLoading = false; this.loadFailed = true; }
    });
  }

  private setOrder(o: ServiceOrderDetail): void {
    this.order = { ...o, materials: o.materials ?? [], ledger: o.ledger ?? [], allowedActions: o.allowedActions ?? [] };
    this.editing = false;
    this.completionRows = this.order.materials
      .filter(m => m.issuedQuantity > 0)
      .map(m => ({ material: m, consumed: m.status === 'CONSUMED' || m.status === 'RETURNED' ? m.consumedQuantity : m.issuedQuantity }));
    this.actualHours = o.actualHours ?? null;
    this.completionNotes = o.completionNotes ?? '';
    this.customerSignature = !!o.customerSignature;
    if (this.canAddMaterial) this.loadMaterialProducts();
  }

  // ── What the person may do: the server's allowedActions AND their permission ──

  private allows(action: ServiceOrderAction, permission: string): boolean {
    return !!this.order?.allowedActions.includes(action) && this.auth.hasPermission(permission);
  }

  get canEdit(): boolean     { return this.allows('EDIT', 'SERVICE_ORDER_EDIT'); }
  get canPlan(): boolean     { return this.allows('PLAN', 'SERVICE_ORDER_EDIT'); }
  get canStart(): boolean    { return this.allows('START', 'SERVICE_ORDER_EDIT'); }
  get canComplete(): boolean { return this.allows('COMPLETE', 'SERVICE_ORDER_COMPLETE'); }
  get canClose(): boolean    { return this.allows('CLOSE', 'SERVICE_ORDER_EDIT'); }
  get canCancel(): boolean   { return this.allows('CANCEL', 'SERVICE_ORDER_CANCEL'); }
  get canAddMaterial(): boolean { return this.allows('ADD_MATERIAL', 'SERVICE_ORDER_EDIT'); }
  get canReserve(): boolean {
    return !!this.order && RESERVABLE.includes(this.order.status) && this.order.materials.some(m => m.sourceType === 'STOCK')
      && this.auth.hasPermission('SERVICE_ORDER_EDIT');
  }
  get canRemoveMaterials(): boolean { return this.auth.hasPermission('SERVICE_ORDER_EDIT'); }

  /** DRAFT/PLANNED: every field; later: notes only. */
  get fullEdit(): boolean { return !!this.order && FULL_EDIT.includes(this.order.status); }

  // ── Header ─────────────────────────────────────────────────────────────

  get stages(): FlowStage[] {
    const o = this.order;
    if (!o) return [];
    if (o.status === 'CANCELLED') {
      const reached = o.actualStartDate ? 5 : 1;
      return flowStagesFrom([...STAGES.slice(0, reached), 'Cancelled'], reached, { failed: true });
    }
    const current = STAGE_INDEX[o.status] ?? 0;
    const subs: (string | null)[] = STAGES.map(() => null);
    if (o.status === 'MATERIAL_PENDING') subs[2] = '⚠ Material pending';
    if (o.status === 'WAITING') subs[2] = '⚠ Waiting for materials';
    if (o.actualStartDate) subs[4] = this.shortDate(o.actualStartDate);
    if (o.actualEndDate) subs[5] = this.shortDate(o.actualEndDate);
    return flowStagesFrom(STAGES, current, { subs });
  }

  get sections(): FlowSection[] {
    const o = this.order;
    if (!o) return [];
    const s: FlowSection[] = [
      { id: 'sec-details', label: 'Details' },
      { id: 'sec-materials', label: 'Materials', count: o.materials.length }
    ];
    if (this.showCompletion) s.push({ id: 'sec-completion', label: 'Completion' });
    s.push({ id: 'sec-ledger', label: 'Ledger', count: o.ledger.length });
    return s;
  }

  get showCompletion(): boolean { return !!this.order && COMPLETION_VISIBLE.includes(this.order.status); }

  get assignedText(): string {
    const o = this.order;
    return o?.assignedUserName || (o?.assignedRoleName ? `Team: ${o.assignedRoleName}` : 'Unassigned');
  }

  private shortDate(iso: string): string {
    const d = new Date(iso);
    return isNaN(d.getTime()) ? '' : d.toLocaleDateString(undefined, { day: '2-digit', month: 'short' });
  }

  // ── Materials ──────────────────────────────────────────────────────────

  private get liveMaterials(): ServiceMaterial[] {
    return (this.order?.materials ?? []).filter(m => m.status !== 'CANCELLED');
  }
  get availableMaterials(): ServiceMaterial[] { return this.liveMaterials.filter(m => !(m.shortageQuantity > 0)); }
  get shortageMaterials(): ServiceMaterial[] { return this.liveMaterials.filter(m => m.shortageQuantity > 0); }

  sourceLabel(m: ServiceMaterial): string {
    return m.sourceType === 'SUBCONTRACT' ? 'Subcontract' : m.sourceType === 'INTERNAL_LABOR' ? 'Labour' : 'Stock';
  }

  reserveAll(): void {
    this.run(this.service.allocate(this.uuid), 'Reserved what is available.');
  }

  private loadMaterialProducts(): void {
    if (this.materialProductsLoaded) return;
    this.materialProductsLoaded = true;
    this.inventory.getProducts({ activeOnly: true, pageSize: 500 }).pipe(catchError(() => of(null))).subscribe(res => {
      // Ad-hoc materials are things issued from stock: not services.
      this.materialProducts = (res?.result?.data ?? []).filter(p => p.productType !== 'SERVICE');
    });
  }

  onAdhocPicked(sel: VariantPickerSelection): void {
    this.adhoc.productUuid = sel.productUuid;
    this.adhoc.variantUuid = sel.variantUuid;
  }

  get canSubmitAdhoc(): boolean { return !this.busy && !!this.adhoc.variantUuid && !!this.adhoc.quantity && this.adhoc.quantity > 0; }

  addAdhoc(): void {
    if (!this.canSubmitAdhoc) return;
    this.run(this.service.addMaterial(this.uuid, {
      variantUuid: this.adhoc.variantUuid!, quantity: this.adhoc.quantity!, notes: this.adhoc.notes.trim() || undefined
    }), 'Material added.', () => this.adhoc = { productUuid: null, variantUuid: null, quantity: null, notes: '' });
  }

  removeMaterial(m: ServiceMaterial): void {
    this.confirmationService.confirm({
      header: 'Remove material',
      message: `Remove ${m.productName}${m.variantName ? ' – ' + m.variantName : ''} from this service order?`,
      acceptLabel: 'Remove', rejectLabel: 'Keep it', acceptButtonStyleClass: 'p-button-danger',
      accept: () => this.run(this.service.removeMaterial(this.uuid, m.uuid), 'Material removed.')
    });
  }

  // ── Lifecycle actions ──────────────────────────────────────────────────

  plan(): void  { this.run(this.service.plan(this.uuid), 'Planned. Materials were checked against stock.'); }
  start(): void { this.run(this.service.start(this.uuid), 'Started. Reserved materials were issued.'); }
  close(): void { this.run(this.service.close(this.uuid), 'Closed.'); }

  /** "Complete" opens the Completion section, where the consumption is entered and confirmed. */
  openCompletion(): void { this.anchors?.go('sec-completion'); }

  openCancel(): void { this.cancelReason = ''; this.cancelVisible = true; }

  confirmCancel(): void {
    const reason = this.cancelReason.trim();
    if (!reason) return;
    this.run(this.service.cancel(this.uuid, { reason }), 'Cancelled. Issued materials were returned.', () => this.cancelVisible = false);
  }

  // ── Completion ─────────────────────────────────────────────────────────

  get completionEditable(): boolean { return this.order?.status === 'IN_PROGRESS' && this.canComplete; }

  returnOf(r: CompletionRow): number {
    return Math.max(0, Math.round((r.material.issuedQuantity - (r.consumed ?? 0)) * 10000) / 10000);
  }

  rowError(r: CompletionRow): string | null {
    if (r.consumed == null || r.consumed < 0) return 'Cannot be negative';
    if (r.consumed > r.material.issuedQuantity) return 'More than was issued';
    return null;
  }

  get hoursRequired(): boolean { return this.order?.invoicingPolicy === 'TIME_AND_MATERIAL'; }

  get completionErrors(): string[] {
    const errors: string[] = [];
    if (this.completionRows.some(r => this.rowError(r))) errors.push('Consumed must be between 0 and the issued quantity.');
    if (this.hoursRequired && !(this.actualHours != null && this.actualHours > 0)) errors.push('Actual hours are required for a time & material service.');
    if (this.actualHours != null && this.actualHours < 0) errors.push('Actual hours cannot be negative.');
    return errors;
  }

  get canSubmitCompletion(): boolean { return this.completionEditable && !this.busy && this.completionErrors.length === 0; }

  confirmComplete(): void {
    if (!this.canSubmitCompletion) return;
    this.confirmationService.confirm({
      header: 'Complete service order',
      message: COMPLETE_CONFIRM_MESSAGE,
      acceptLabel: 'Complete', rejectLabel: 'Not yet',
      accept: () => this.complete()
    });
  }

  complete(): void {
    this.run(this.service.complete(this.uuid, {
      consumedMaterials: this.completionRows.map(r => ({ smrUuid: r.material.uuid, consumedQuantity: r.consumed })),
      actualHours: this.actualHours ?? undefined,
      completionNotes: this.completionNotes.trim() || undefined,
      customerSignature: this.customerSignature
    }), 'Completed. Unused materials were returned to the warehouse.');
  }

  // ── Ledger ─────────────────────────────────────────────────────────────

  isReturn(e: ServiceLedgerEntry): boolean { return e.quantity < 0 || e.movementType === 'SERVICE_RETURN'; }

  get netByProduct(): ServiceLedgerNet[] {
    const byVariant = new Map<string, ServiceLedgerNet>();
    for (const e of this.order?.ledger ?? []) {
      const row = byVariant.get(e.variantUuid) ?? { variantUuid: e.variantUuid, productName: e.productName, uom: e.uom, netQuantity: 0 };
      row.netQuantity = Math.round((row.netQuantity + e.quantity) * 10000) / 10000;
      byVariant.set(e.variantUuid, row);
    }
    return [...byVariant.values()];
  }

  // ── Details edit ───────────────────────────────────────────────────────

  startEdit(): void {
    const o = this.order;
    if (!o) return;
    this.edit = {
      customerUuid: o.customerUuid, quantity: o.quantity, warehouseUuid: o.warehouseUuid,
      assignedUserId: o.assignedUserId ?? null, assignedRoleId: o.assignedRoleId ?? null,
      scheduledDate: fromYmd(o.scheduledDate), scheduledTime: fromHhmm(o.scheduledTime),
      estimatedHours: o.estimatedHours ?? null, priority: o.priority, notes: o.notes ?? ''
    };
    this.editing = true;
    this.anchors?.go('sec-details');
    if (this.fullEdit) this.loadEditOptions();
  }

  private loadEditOptions(): void {
    if (this.optionsLoaded || !this.order) return;
    this.optionsLoaded = true;
    const o = this.order;
    forkJoin({
      customers:  customerOptions$(this.partners),
      warehouses: this.inventory.getWarehouses().pipe(catchError(() => of(null))),
      users:      assigneeOptions$(this.users, this.auth, { id: o.assignedUserId, name: o.assignedUserName }),
      roles:      this.auth.getRoles().pipe(catchError(() => of(null)))
    }).subscribe(({ customers, warehouses, users, roles }) => {
      // Keep the current values choosable even when they fall outside the first page of options.
      this.customerOptions = customers.some(c => c.value === o.customerUuid) ? customers : [{ label: o.customerName, value: o.customerUuid }, ...customers];
      const wh = ((warehouses?.result ?? []) as WarehouseModel[]).filter(w => w.isActive !== false).map(w => ({ label: `${w.code} – ${w.name}`, value: w.uuid }));
      this.warehouseOptions = wh.some(w => w.value === o.warehouseUuid) ? wh : [{ label: o.warehouseName, value: o.warehouseUuid }, ...wh];
      this.userOptions = users;
      this.roleOptions = (roles?.result ?? []).map(r => ({ label: r.value, value: r.id }));
    });
  }

  onEditUserChange(): void { if (this.edit.assignedUserId) this.edit.assignedRoleId = null; }
  onEditRoleChange(): void { if (this.edit.assignedRoleId) this.edit.assignedUserId = null; }

  get canSaveEdit(): boolean {
    if (this.busy) return false;
    if (!this.fullEdit) return true;
    return !!this.edit.customerUuid && !!this.edit.warehouseUuid && !!this.edit.quantity && this.edit.quantity > 0;
  }

  saveEdit(): void {
    const o = this.order;
    if (!o || !this.canSaveEdit) return;
    const e = this.edit;
    // Past PLANNED the server takes only the notes; the rest is sent back unchanged.
    const body = this.fullEdit
      ? {
          customerUuid: e.customerUuid!, quantity: e.quantity!, warehouseUuid: e.warehouseUuid!,
          assignedUserId: e.assignedUserId, assignedRoleId: e.assignedRoleId,
          scheduledDate: ymd(e.scheduledDate) ?? null, scheduledTime: e.scheduledDate ? (hhmm(e.scheduledTime) ?? null) : null,
          estimatedHours: e.estimatedHours, priority: e.priority, notes: e.notes.trim() || null, rowVersion: o.rowVersion
        }
      : {
          customerUuid: o.customerUuid, quantity: o.quantity, warehouseUuid: o.warehouseUuid,
          assignedUserId: o.assignedUserId ?? null, assignedRoleId: o.assignedRoleId ?? null,
          scheduledDate: o.scheduledDate ?? null, scheduledTime: o.scheduledTime ?? null,
          estimatedHours: o.estimatedHours ?? null, priority: o.priority, notes: e.notes.trim() || null, rowVersion: o.rowVersion
        };
    this.run(this.service.update(this.uuid, body), 'Saved.');
  }

  cancelEdit(): void { this.editing = false; }

  // ── Plumbing ───────────────────────────────────────────────────────────

  /** Every action answers with the refreshed detail; without one, the page reloads. 409 = someone else saved first. */
  private run(call: Observable<ApiResponse<ServiceOrderDetail>>, success: string, after?: () => void): void {
    this.busy = true;
    call.subscribe({
      next: res => {
        this.busy = false;
        after?.();
        if (res?.result && (res.result as ServiceOrderDetail).uuid) this.setOrder(res.result);
        else this.load();
        this.messageService.add({ severity: 'success', summary: 'Done', detail: success });
      },
      error: err => {
        this.busy = false;
        if (err?.status === 409) {
          this.messageService.add({ severity: 'warn', summary: 'Changed elsewhere', detail: 'Someone else changed this service order. It has been reloaded — please try again.' });
          this.load();
          return;
        }
        this.messageService.add({ severity: 'error', summary: 'Not done', detail: serverMessage(err, 'The service order could not be updated.') });
      }
    });
  }
}
