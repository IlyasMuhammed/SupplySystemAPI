import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { MessageModule } from 'primeng/message';
import { TableModule } from 'primeng/table';
import { TabViewModule } from 'primeng/tabview';
import { TagModule } from 'primeng/tag';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable } from 'rxjs';

import {
  CreateFinishedGoodsReceiptRequest, CreateProductionIssueLineRequest, CreateProductionIssueRequest,
  CreateQualityInspectionLineRequest, FinishedGoodsReceipt, ProductionLedger, ProductionMaterial, ProductionOrderDetail,
  ProductionOrderService, QualityInspection, priorityLabel, productionStatusSeverity, qiResultSeverity, readinessSeverity
} from '../../../../services/production-order.service';
import { AuthService } from '../../../service/auth.service';
import { ApiResponse } from '../../../../services/inventory.service';
import { AllocationService } from '../../../../services/allocation.service';
import { DeliveryListItemModel, LogisticsService } from '../../../../services/logistics.service';
import { DELIVERY_STATUS_SEVERITY } from '../../../logistics/deliveries/delivery-list/delivery-list.component';
import { formatCode } from '../../../../shared/format-code';
import { FLOW, FlowStage, flowStagesFrom } from '../../../../shared/flow';

/** A34 — statuses after a quality inspection has been recorded: accepted and yield mean something. */
const INSPECTED_STATUSES = ['QUALITY_INSPECTION', 'COMPLETED', 'CLOSED'];

/** A30 §29.3 — one production order: its materials, whether they are covered, supply raised for what is short, and floor issues. */
@Component({
  selector: 'app-production-order-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, ConfirmDialogModule, DialogModule, DropdownModule, InputNumberModule, InputTextModule, MessageModule,
    TableModule, TabViewModule, TagModule, TextareaModule, ToastModule, TooltipModule,
    ...FLOW
  ],
  templateUrl: './production-order-detail.component.html',
  styleUrls: ['./production-order-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class ProductionOrderDetailComponent implements OnInit {
  readonly severity = productionStatusSeverity;
  readonly readinessSeverity = readinessSeverity;
  readonly priorityLabel = priorityLabel;
  readonly qiResultSeverity = qiResultSeverity;

  uuid = '';
  order: ProductionOrderDetail | null = null;
  isLoading = true;
  loadFailed = false;
  busy = false;
  activeTab = 0;

  reportVisible = false;
  reportQuantity: number | null = null;
  reportNotes = '';

  cancelVisible = false;
  cancelReason = '';

  issueVisible = false;
  issueType: CreateProductionIssueRequest['issueType'] = 'STANDARD';
  issueWarehouseUuid: string | null = null;
  issueConfirmNow = true;
  issueLines: (CreateProductionIssueLineRequest & { materialName?: string; uom?: string })[] = [];

  reverseVisible = false;
  reverseReason = '';
  reverseTarget: string | null = null;

  qi: QualityInspection | null = null;
  isLoadingQi = false;
  qiVisible = false;
  qiLines: (CreateQualityInspectionLineRequest & { key: number })[] = [];
  qiNotes = '';

  fgrs: FinishedGoodsReceipt[] = [];
  isLoadingFgrs = false;
  fgrVisible = false;
  fgrQuantity: number | null = null;
  fgrConfirmNow = true;

  ledger: ProductionLedger | null = null;
  isLoadingLedger = false;

  // A31 C8/C10 — a GRN receipt no longer runs allocation by itself, so this tab needs its own
  // trigger to check/reserve availability for this order's own materials.
  isRunningAllocation = false;
  runningRowUuid: string | null = null;

  // A34 PE-06 — the deliveries made from this order (live status; Material can't read Logistics).
  productionDeliveries: DeliveryListItemModel[] = [];
  isLoadingDeliveries = false;
  isCreatingDelivery = false;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private service: ProductionOrderService,
    private allocationService: AllocationService,
    private authService: AuthService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    private logisticsService: LogisticsService
  ) {}

  ngOnInit(): void {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  // ── Permissions × status ──────────────────────────────────────────────────

  private has(code: string): boolean { return this.authService.hasPermission(code); }
  private is(...statuses: string[]): boolean { return !!this.order && statuses.includes(this.order.status); }

  get canPlan(): boolean     { return this.has('PROD_PLAN')   && this.is('DRAFT'); }
  get canStart(): boolean    { return this.has('PROD_START')  && this.is('READY'); }
  get canReport(): boolean   { return this.has('PROD_REPORT') && this.is('IN_PROGRESS'); }
  get canComplete(): boolean { return this.has('PROD_REPORT') && this.is('IN_PROGRESS') && (this.order?.producedQuantity ?? 0) > 0; }
  get canCancel(): boolean   { return this.has('PROD_CANCEL') && this.is('DRAFT', 'PLANNED', 'MATERIAL_PENDING', 'READY', 'IN_PROGRESS'); }
  get canIssue(): boolean    { return this.has('MI_CREATE')   && this.is('READY', 'IN_PROGRESS'); }
  get canManage(): boolean   { return this.has('PROD_MANAGER'); }

  canConfirm(status: string): boolean { return this.has('MI_CONFIRM') && status === 'DRAFT'; }
  canReverse(status: string): boolean { return this.has('MI_REVERSE') && status === 'CONFIRMED'; }

  get canInspect(): boolean { return this.has('QI_APPROVE') && this.is('QUALITY_INSPECTION') && !this.qi; }
  get canReceive(): boolean { return this.has('FGR_CREATE') && this.is('QUALITY_INSPECTION') && !!this.qi && this.qi.outstandingForFgr > 0; }
  canConfirmFgr(status: string): boolean { return this.has('FGR_CONFIRM') && status === 'DRAFT'; }

  /** SMS Flow header strip: Draft → Planned → In progress → Inspection → Completed (cancelled = red). */
  get stages(): FlowStage[] {
    const o = this.order;
    if (!o) return [];
    if (o.status === 'CANCELLED') return flowStagesFrom(['Draft', 'Cancelled'], 1, { failed: true });
    const order: Record<string, number> = {
      DRAFT: 0, PLANNED: 1, MATERIAL_PENDING: 1, READY: 1, IN_PROGRESS: 2, QUALITY_INSPECTION: 3, COMPLETED: 5, CLOSED: 5
    };
    const planSub = o.status === 'MATERIAL_PENDING' ? 'materials pending' : o.status === 'READY' ? 'ready' : null;
    const made = o.plannedQuantity > 0 ? `${Math.round((o.producedQuantity / o.plannedQuantity) * 100)}% made` : null;
    return flowStagesFrom(['Draft', 'Planned', 'In progress', 'Inspection', 'Completed'], order[o.status] ?? 0, {
      subs: [null, planSub, o.status === 'IN_PROGRESS' ? made : null, null, null]
    });
  }

  // ── Loading ───────────────────────────────────────────────────────────────

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.qi = null;
    this.fgrs = [];
    this.ledger = null;
    this.service.getById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.order = res.result ?? null;
        if (!this.order) this.loadFailed = true;
        this.loadProductionDeliveries();
        if (this.activeTab === 3) this.loadQuality();
        if (this.activeTab === 4) this.loadLedger();
      },
      error: () => { this.isLoading = false; this.loadFailed = true; }
    });
  }

  onTabChange(index: number): void {
    this.activeTab = index;
    if (index === 3 && !this.qi && this.fgrs.length === 0) this.loadQuality();
    if (index === 4 && !this.ledger) this.loadLedger();
  }

  // ── Quality & FGR loading ────────────────────────────────────────────────────

  loadQuality(): void {
    this.isLoadingQi = true;
    this.service.getInspectionForOrder(this.uuid).subscribe({
      next: (res) => {
        this.isLoadingQi = false;
        this.qi = res.result ?? null;
        this.loadFgrs();
      },
      error: () => { this.isLoadingQi = false; }
    });
  }

  loadFgrs(): void {
    this.isLoadingFgrs = true;
    this.service.getFgrsForOrder(this.uuid).subscribe({
      next: (res) => { this.isLoadingFgrs = false; this.fgrs = res.result ?? []; },
      error: () => { this.isLoadingFgrs = false; }
    });
  }

  loadLedger(): void {
    this.isLoadingLedger = true;
    this.service.getLedgerForOrder(this.uuid).subscribe({
      next: (res) => { this.isLoadingLedger = false; this.ledger = res.result ?? null; },
      error: () => { this.isLoadingLedger = false; }
    });
  }

  // ── Materials tab: Available / Shortage split (A31 C8) ──────────────────────
  // ProductionMaterialModel already carries IsCovered (a real hold covers the outstanding qty) and
  // ShortageQuantity (nothing — held or planned — covers it), so the split needs no new endpoint.

  get availableMaterials(): ProductionMaterial[] {
    return (this.order?.materials ?? []).filter(m => m.status !== 'CANCELLED' && m.isCovered);
  }

  get shortageMaterials(): ProductionMaterial[] {
    return (this.order?.materials ?? []).filter(m => m.status !== 'CANCELLED' && !m.isCovered);
  }

  get canRunAllocation(): boolean {
    return this.has('ALLOCATION_RUN') && (this.order?.materials.some(m => m.status !== 'CANCELLED') ?? false);
  }

  /** A31 C10 — checks/reserves availability across every distinct material variant/warehouse this order uses. */
  runAllocationForOrder(): void {
    if (!this.canRunAllocation || this.isRunningAllocation) return;
    this.isRunningAllocation = true;
    this.service.runAllocation(this.uuid).subscribe({
      next: () => {
        this.isRunningAllocation = false;
        this.messageService.add({ severity: 'success', summary: 'Allocation run', detail: 'Checked availability for this order\'s materials.' });
        this.load();
      },
      error: (err) => { this.isRunningAllocation = false; this.fail(err); }
    });
  }

  /** Same run, scoped to just the one material's variant/warehouse — for a single shortage row. */
  runAllocationForRow(m: ProductionMaterial): void {
    if (!this.canRunAllocation || this.runningRowUuid) return;
    this.runningRowUuid = m.uuid;
    this.allocationService.run(m.materialVariantUuid, m.warehouseUuid).subscribe({
      next: () => {
        this.runningRowUuid = null;
        this.messageService.add({ severity: 'success', summary: 'Allocation run', detail: `Checked availability for ${m.materialProductName}.` });
        this.load();
      },
      error: (err) => { this.runningRowUuid = null; this.fail(err); }
    });
  }

  // ── Execution actions ──────────────────────────────────────────────────────

  plan(): void { this.run(this.service.plan(this.uuid), 'Planned. Materials are held for what is available; supply was raised for the rest.'); }
  start(): void { this.run(this.service.start(this.uuid), 'Production started.'); }

  openReport(): void { this.reportQuantity = null; this.reportNotes = ''; this.reportVisible = true; }
  confirmReport(): void {
    if (!this.reportQuantity || this.reportQuantity <= 0) return;
    this.run(this.service.reportOutput(this.uuid, { quantity: this.reportQuantity, notes: this.reportNotes || undefined }),
      'Output reported.', () => this.reportVisible = false);
  }

  complete(): void { this.run(this.service.complete(this.uuid), 'Sent to quality inspection.'); }

  openCancel(): void { this.cancelReason = ''; this.cancelVisible = true; }
  confirmCancel(): void {
    if (!this.cancelReason.trim()) return;
    this.run(this.service.cancel(this.uuid, { reason: this.cancelReason.trim() }), 'Production order cancelled.', () => this.cancelVisible = false);
  }

  // ── Material issue dialog ───────────────────────────────────────────────────

  get issueableMaterials(): ProductionMaterial[] {
    return this.order?.materials.filter(m => m.status !== 'CANCELLED') ?? [];
  }

  openIssue(): void {
    this.issueType = 'STANDARD';
    this.issueWarehouseUuid = this.order?.warehouseUuid ?? null;
    this.issueConfirmNow = true;
    this.issueLines = this.issueableMaterials
      .filter(m => m.outstanding > 0)
      .map(m => ({ requirementUuid: m.uuid, quantity: 0, materialName: `${m.materialProductName} – ${m.materialVariantName}`, uom: m.uom }));
    if (this.issueLines.length === 0) {
      this.issueLines = this.issueableMaterials.map(m => ({ requirementUuid: m.uuid, quantity: 0, materialName: `${m.materialProductName} – ${m.materialVariantName}`, uom: m.uom }));
    }
    this.issueVisible = true;
  }

  get issueLinesValid(): boolean {
    return this.issueLines.some(l => l.quantity > 0) &&
      this.issueLines.every(l => l.quantity >= 0) &&
      (this.issueType !== 'SUBSTITUTION' || this.issueLines.filter(l => l.quantity > 0).every(l => !!l.materialVariantUuid));
  }

  confirmIssueDialog(): void {
    if (!this.issueLinesValid || this.busy) return;
    const req: CreateProductionIssueRequest = {
      issueType: this.issueType,
      warehouseUuid: this.issueWarehouseUuid || undefined,
      confirm: this.issueConfirmNow,
      lines: this.issueLines.filter(l => l.quantity > 0).map(l => ({
        requirementUuid: l.requirementUuid,
        materialVariantUuid: this.issueType === 'SUBSTITUTION' ? l.materialVariantUuid : undefined,
        quantity: l.quantity,
        notes: l.notes || undefined
      }))
    };
    this.busy = true;
    this.service.createIssue(this.uuid, req).subscribe({
      next: () => {
        this.busy = false;
        this.issueVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Done', detail: this.issueConfirmNow ? 'Material issue confirmed.' : 'Material issue drafted.' });
        this.load();
      },
      error: (err) => { this.busy = false; this.fail(err); }
    });
  }

  confirmIssueRow(issueUuid: string): void {
    this.run(this.service.confirmIssue(issueUuid), 'Material issue confirmed.');
  }

  openReverse(issueUuid: string): void { this.reverseTarget = issueUuid; this.reverseReason = ''; this.reverseVisible = true; }
  confirmReverse(): void {
    if (!this.reverseTarget || !this.reverseReason.trim()) return;
    this.run(this.service.reverseIssue(this.reverseTarget, { reason: this.reverseReason.trim() }), 'Material issue reversed.', () => this.reverseVisible = false);
  }

  // ── Quality inspection dialog ────────────────────────────────────────────────

  openQi(): void {
    this.qiNotes = '';
    this.qiLines = [{ key: 0, checkName: 'Visual inspection', result: 'PASS', quantityChecked: this.order?.producedQuantity ?? 0 }];
    this.qiVisible = true;
  }

  addQiLine(): void {
    this.qiLines.push({ key: this.qiLines.length, checkName: '', result: 'PASS', quantityChecked: 0 });
  }

  removeQiLine(index: number): void { this.qiLines.splice(index, 1); }

  get qiCheckedTotal(): number { return this.qiLines.reduce((sum, l) => sum + (l.quantityChecked || 0), 0); }
  get qiLinesValid(): boolean {
    return this.qiLines.length > 0 &&
      this.qiLines.every(l => l.checkName.trim().length > 0 && l.quantityChecked > 0) &&
      this.qiCheckedTotal === (this.order?.producedQuantity ?? -1);
  }

  confirmQi(): void {
    if (!this.qiLinesValid || this.busy) return;
    this.busy = true;
    this.service.createInspection(this.uuid, {
      lines: this.qiLines.map(l => ({ checkName: l.checkName.trim(), result: l.result, quantityChecked: l.quantityChecked, defectCode: l.defectCode || undefined, notes: l.notes || undefined })),
      notes: this.qiNotes || undefined
    }).subscribe({
      next: () => {
        this.busy = false;
        this.qiVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Recorded', detail: 'Quality inspection recorded.' });
        this.load();
      },
      error: (err) => { this.busy = false; this.fail(err); }
    });
  }

  // ── Finished goods receipt dialog ────────────────────────────────────────────

  openFgr(): void {
    this.fgrQuantity = this.qi?.outstandingForFgr ?? null;
    this.fgrConfirmNow = true;
    this.fgrVisible = true;
  }

  confirmFgrDialog(): void {
    if (!this.fgrQuantity || this.fgrQuantity <= 0 || this.busy) return;
    const req: CreateFinishedGoodsReceiptRequest = { quantity: this.fgrQuantity, confirm: this.fgrConfirmNow };
    this.busy = true;
    this.service.createFgr(this.uuid, req).subscribe({
      next: () => {
        this.busy = false;
        this.fgrVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Done', detail: this.fgrConfirmNow ? 'Finished goods receipt confirmed.' : 'Finished goods receipt drafted.' });
        this.load();
      },
      error: (err) => { this.busy = false; this.fail(err); }
    });
  }

  confirmFgrRow(fgrUuid: string): void {
    this.run(this.service.confirmFgr(fgrUuid), 'Finished goods receipt confirmed.');
  }

  // ── A34 PE-06: made to order → delivery ─────────────────────────────────────

  /** Made to order for a sale order line: it has a route, and its delivery follows its completion (D-18). */
  get isMakeToOrder(): boolean { return !!this.order?.isMakeToOrder; }

  get canViewDeliveries(): boolean { return this.has('DELIVERY_VIEW'); }
  get canViewSaleOrder(): boolean { return this.has('SALE_ORDER_VIEW'); }

  /** D-20 — "Create delivery now": DELIVERY_CREATE, a make-to-order order from a sale order, with accepted goods. */
  get canCreateDeliveryNow(): boolean {
    return this.has('DELIVERY_CREATE') && this.isMakeToOrder && this.order?.sourceType === 'SALES_ORDER'
        && (this.order?.acceptedQuantity ?? 0) > 0 && this.order?.status !== 'CANCELLED';
  }

  /** Accepted and yield mean something once a quality inspection is recorded. */
  get showYield(): boolean {
    return !!this.order && (INSPECTED_STATUSES.includes(this.order.status) || this.order.acceptedQuantity > 0);
  }

  get yieldPercent(): number | null {
    const o = this.order;
    if (!o || !o.plannedQuantity) return null;
    return Math.round((o.acceptedQuantity / o.plannedQuantity) * 100);
  }

  /** "Shortfall of 2 units (planned 50, produced 48)" — D-21. */
  get shortfallText(): string | null {
    const o = this.order;
    const short = o?.shortfallQuantity ?? 0;
    if (!o || short <= 0) return null;
    const n = (v: number) => String(Math.round(v * 10000) / 10000);
    return `Shortfall of ${n(short)} unit${short === 1 ? '' : 's'} (planned ${n(o.plannedQuantity)}, produced ${n(o.acceptedQuantity)})`;
  }

  deliveryStatusLabel(status: string): string { return formatCode(status); }
  deliverySeverity(status: string) { return DELIVERY_STATUS_SEVERITY[status] ?? 'secondary'; }

  /** GET api/logistics/deliveries?productionOrderUuid= — read on every load, so the status is live (no push, D-25). */
  loadProductionDeliveries(): void {
    this.productionDeliveries = [];
    if (!this.isMakeToOrder || !this.canViewDeliveries) return;
    this.isLoadingDeliveries = true;
    this.logisticsService.getDeliveries({ productionOrderUuid: this.uuid, pageSize: 50 }).subscribe({
      next: (res) => { this.isLoadingDeliveries = false; this.productionDeliveries = res.result?.data ?? []; },
      error: () => { this.isLoadingDeliveries = false; this.productionDeliveries = []; }
    });
  }

  createDeliveryNow(): void {
    if (!this.canCreateDeliveryNow || this.isCreatingDelivery) return;
    this.isCreatingDelivery = true;
    this.service.createDelivery(this.uuid).subscribe({
      next: (res) => {
        this.isCreatingDelivery = false;
        const r = res.result;
        if (r && r.quantityCreated > 0) {
          this.messageService.add({
            severity: 'success', summary: 'Delivery created',
            detail: `${r.deliveryNumber ?? 'A delivery'} created for ${r.quantityCreated} unit${r.quantityCreated === 1 ? '' : 's'}.` +
                    (r.skippedReason ? ` ${r.skippedReason}` : '')
          });
        } else {
          this.messageService.add({ severity: 'info', summary: 'Nothing to create', detail: r?.skippedReason || 'Everything accepted is already on a delivery.' });
        }
        this.load();
      },
      error: (err) => { this.isCreatingDelivery = false; this.fail(err); }
    });
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private run(call: Observable<ApiResponse | ApiResponse<unknown>>, detail: string, then?: () => void): void {
    if (this.busy) return;
    this.busy = true;
    call.subscribe({
      next: () => {
        this.busy = false;
        then?.();
        this.messageService.add({ severity: 'success', summary: 'Done', detail });
        this.load();
      },
      error: (err) => { this.busy = false; this.fail(err); }
    });
  }

  private fail(err: { error?: { message?: string } }): void {
    this.messageService.add({ severity: 'error', summary: 'Not done', detail: err.error?.message || 'The action failed.' });
  }
}
