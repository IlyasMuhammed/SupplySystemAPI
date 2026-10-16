import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TableModule } from 'primeng/table';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { forkJoin } from 'rxjs';

import {
  POS_FLAGS, POS_ROUTE, PosAdminService, PosOrder, PosOrderLine, PosPaymentMethod, newLocalId, posError,
  posFlagLabel, posFlagTone
} from '../../../services/pos-admin.service';
import {
  EffectivePolicy, POS_REFUND_METHODS, PosReturnsAdminService, ReturnReason, STORE_CREDIT_ROUTE, managerNeedLabel, managerRequired
} from '../../../services/pos-returns-admin.service';
import { AuthService } from '../../service/auth.service';
import { ModuleService } from '../../../services/module.service';
import { FLOW, FlowSection, flowLabel, flowTone } from '../../../shared/flow';
import { QboSyncBadgeComponent } from '../../../shared/components/qbo-sync-badge/qbo-sync-badge.component';
import { SyncKind } from '../../../models/quickbooks-integration.models';

/** A44 D-21 / D-23 — the other kinds a POS sale / its credit note may have been sent to QuickBooks as. */
const QBO_SALE_ALSO: SyncKind[] = ['SalesReceipt'];
const QBO_RETURN_ALSO: SyncKind[] = ['RefundReceipt'];

export interface ReturnLineInput {
  line: PosOrderLine;
  quantity: number;
  /** Σ already refunded for this line by earlier returns (their lines' lineTotal for this originalLineUuid). */
  refundedBefore?: number;
}

/** 2 dp, midpoint away from zero (the server's rounding). */
export const r2 = (n: number) => Math.sign(n) * Math.round(Math.abs(n) * 100 + 1e-9) / 100;

/**
 * POSB's refund rule for one line: round(lineTotal × qty / originalQty), away from zero — except that returning the
 * line's last remaining units refunds exactly what is left (lineTotal − refunded before), so the parts add up to the line.
 */
export function lineRefund(l: ReturnLineInput): number {
  if (!(l.quantity > 0) || !l.line.quantity) return 0;
  if (l.quantity >= l.line.returnableQuantity) return r2(l.line.lineTotal - (l.refundedBefore ?? 0));
  return r2(l.line.lineTotal * l.quantity / l.line.quantity);
}

/** The return total the server will book (Σ line refunds, before the restocking fee). */
export function estimateReturnTotal(lines: readonly ReturnLineInput[]): number {
  return r2(lines.reduce((sum, l) => sum + lineRefund(l), 0));
}

/** A39 D-5 — fee = round(return total × pct / 100); the reason's override wins (0 waives). */
export function restockingFee(returnTotal: number, policyPct: number | null | undefined, reasonOverride?: number | null): number {
  const pct = reasonOverride ?? policyPct ?? 0;
  return pct > 0 ? r2(returnTotal * pct / 100) : 0;
}

/** Contract §3 — the original's largest non-cash tender method, else CASH. */
export function originalRefundMethod(o: Pick<PosOrder, 'payments'>): PosPaymentMethod {
  // As the server (POSR): per method, Σ (amount − change), largest first.
  const byMethod = new Map<PosPaymentMethod, number>();
  for (const p of o.payments ?? []) {
    if (p.isRefund || p.method === 'CASH') continue;
    byMethod.set(p.method, (byMethod.get(p.method) ?? 0) + p.amount - (p.changeGiven ?? 0));
  }
  let best: PosPaymentMethod = 'CASH', max = -Infinity;
  for (const [m, a] of byMethod) if (a > max) { best = m; max = a; }
  return best;
}

/**
 * A39 — the refund methods the store's effective policy allows for this order (contract §3, POSR's check): cash only if
 * allowCashRefund, ON_ACCOUNT only for credit sales (A38), STORE_CREDIT only if allowCreditNoteRefund and
 * FEATURE_POS_CREDIT_NOTES; refundToOriginalMethod ⇒ additionally nothing but the original method. No policy ⇒ A38 rules.
 */
export function allowedRefundMethods(o: Pick<PosOrder, 'payments' | 'paymentStatus'>, policy: EffectivePolicy | null,
                                     storeCreditOn: boolean): PosPaymentMethod[] {
  const credit = o.paymentStatus === 'CREDIT_SALE' || o.paymentStatus === 'PARTIAL_CREDIT';
  const storeCredit = storeCreditOn && (!policy || policy.allowCreditNoteRefund);
  const ok = (m: PosPaymentMethod) =>
    m === 'CASH' ? !policy || policy.allowCashRefund
    : m === 'ON_ACCOUNT' ? credit
    : m === 'STORE_CREDIT' ? storeCredit
    : m !== 'EXCHANGE';
  if (policy?.refundToOriginalMethod) {
    const orig = originalRefundMethod(o);
    // The original is ON_ACCOUNT only for credit sales, so it is allowed whenever it is the original.
    return orig === 'ON_ACCOUNT' || ok(orig) ? [orig] : [];
  }
  return POS_REFUND_METHODS.filter(ok);
}

const DAY = 24 * 60 * 60 * 1000;

/**
 * A38 FSD §17.7.4 — one POS order: lines, payments, links to its sales invoice / credit note, returns; Void (POS_ORDER_VOID,
 * same shift — the server decides) and Return (POS_ORDER_RETURN + FEATURE_POS_RETURNS) (API-CONTRACT §5).
 * A39 — return reason / authorisation / restocking fee / cross-store / store credit on returns; exchanges (RETURNING / NEW
 * lines, net settlement); the return dialog uses the store's effective policy (reasons, refund methods, fee preview) and
 * shows "Manager authorization required." with its reasons when the server answers requiresManager.
 */
@Component({
  selector: 'app-pos-order-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, ButtonModule, DialogModule, DropdownModule, InputNumberModule, InputTextModule, TableModule,
    TextareaModule, ToastModule, TooltipModule, QboSyncBadgeComponent, ...FLOW
  ],
  templateUrl: './order-detail.component.html',
  styles: [`
    .od-err { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .od-refund { display: grid; grid-template-columns: 1fr 1fr; gap: .5rem; align-items: start; margin-bottom: .5rem; }
    .od-sum { display: flex; justify-content: space-between; margin: .35rem 0; }
    .od-bad { color: var(--sms-danger); }
    .od-hint { display: block; margin-top: .5rem; }
    .od-sub { margin: .75rem 1rem .25rem; font-size: .95rem; }
    @media (max-width: 640px) { .od-refund { grid-template-columns: 1fr; } }
    :host ::ng-deep .w-full { width: 100%; }
  `],
  providers: [MessageService]
})
export class PosOrderDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly pos = inject(PosAdminService);
  private readonly returnsApi = inject(PosReturnsAdminService);
  private readonly auth = inject(AuthService);
  private readonly modules = inject(ModuleService);

  readonly qboSaleAlso = QBO_SALE_ALSO;
  readonly qboReturnAlso = QBO_RETURN_ALSO;
  private readonly messages = inject(MessageService);
  private readonly destroyRef = inject(DestroyRef);

  readonly posRoute = POS_ROUTE;
  readonly creditRoute = STORE_CREDIT_ROUTE;
  readonly tone = flowTone;
  readonly label = flowLabel;

  uuid = '';
  order: PosOrder | null = null;
  isLoading = true;
  loadError: string | null = null;
  sections: FlowSection[] = [];
  flagPills: { code: string; label: string; tone: string; hint: string }[] = [];
  hasReturnable = false;
  /** A39 — RETURN or EXCHANGE. */
  isReturnLike = false;
  isExchange = false;
  /** Exchange: the lines taken back (negative) and the new ones. */
  returningLines: PosOrderLine[] = [];
  newLines: PosOrderLine[] = [];
  settlementLabel = '';
  kindLabel = 'POS sale';

  // Void
  voidDialog = false;
  voidReason = '';
  voidBusy = false;
  voidError: string | null = null;

  // Return
  returnDialog = false;
  returnLines: ReturnLineInput[] = [];
  refundMethodOptions: { label: string; value: PosPaymentMethod }[] = [];
  refundMethod: PosPaymentMethod = 'CASH';
  refundReference = '';
  /** Free-text reason — only when the reason list could not be loaded (A38 fallback). */
  returnReason = '';
  reasons: ReturnReason[] = [];
  reasonOptions: { label: string; value: string }[] = [];
  reasonsLoaded = false;
  reasonUuid: string | null = null;
  selectedReason: ReturnReason | null = null;
  returnNote = '';
  policy: EffectivePolicy | null = null;
  policyNote: string | null = null;
  daysSincePurchase = 0;
  returnBusy = false;
  returnError: string | null = null;
  /** The server's manager-required answer, as readable reasons. */
  managerReasons: string[] = [];
  /** What will need a manager, as far as the client can tell (shown before sending). */
  managerHints: string[] = [];
  returnTotal = 0;
  feePct = 0;
  restockingFee = 0;
  refundAmount = 0;
  returnHistoryReady = true;
  returnShiftOptions: { label: string; value: string }[] = [];
  returnShiftUuid: string | null = null;
  private shiftTerminals = new Map<string, string>();
  /** The store whose effective policy is in `policy`. */
  private policyStore: string | null = null;

  get canVoid(): boolean { return this.auth.hasPermission('POS_ORDER_VOID'); }
  get canReturn(): boolean { return this.auth.hasPermission('POS_ORDER_RETURN') && this.modules.isFeatureEnabled('FEATURE_POS_RETURNS'); }
  get canOpenInvoice(): boolean { return this.auth.hasAnyPermission('SALES_INVOICE_VIEW', 'SALES_INVOICE_MANAGE'); }
  get canViewStoreCredit(): boolean { return this.auth.hasPermission('POS_CREDIT_NOTE_VIEW'); }
  get canOverride(): boolean { return this.auth.hasPermission('POS_RETURN_OVERRIDE'); }
  get isSale(): boolean { return this.order?.orderType === 'SALE'; }
  /** The server also refuses a void outside the order's shift ("Orders can only be voided within the same shift."). */
  get showVoid(): boolean { return this.canVoid && this.isSale && this.order?.status === 'COMPLETED'; }
  get showReturn(): boolean {
    return this.canReturn && this.isSale && (this.order?.status === 'COMPLETED' || this.order?.status === 'PARTIALLY_RETURNED') && this.hasReturnable;
  }

  ngOnInit(): void {
    // The component is reused when only :uuid changes (original order ⇄ its returns).
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(p => {
      this.uuid = p.get('uuid') ?? '';
      this.load();
    });
  }

  load(): void {
    this.isLoading = true;
    this.loadError = null;
    this.pos.getOrder(this.uuid).subscribe({
      next: res => {
        this.isLoading = false;
        const o = res.result ?? null;
        if (!o) { this.order = null; this.loadError = res.message || 'Order not found.'; return; }
        this.setOrder(o);
      },
      error: err => {
        this.isLoading = false;
        this.order = null;
        this.loadError = posError(err, err?.status === 404 ? 'Order not found.' : 'Failed to load the order.');
      }
    });
  }

  private setOrder(o: PosOrder): void {
    this.order = o;
    this.flagPills = (o.flags ?? []).map(code => ({ code, label: posFlagLabel(code), tone: posFlagTone(code), hint: POS_FLAGS[code]?.hint ?? '' }));
    this.hasReturnable = (o.lines ?? []).some(l => (l.returnableQuantity ?? 0) > 0);
    this.isExchange = o.orderType === 'EXCHANGE';
    this.isReturnLike = o.orderType === 'RETURN' || this.isExchange;
    this.kindLabel = o.orderType === 'RETURN' ? 'POS return' : this.isExchange ? 'POS exchange' : 'POS sale';
    // Exchange lines: returning ones carry a negative quantity (or point at the original line).
    this.returningLines = this.isExchange ? (o.lines ?? []).filter(l => l.quantity < 0 || (!!l.originalLineUuid && l.lineTotal < 0)) : [];
    this.newLines = this.isExchange ? (o.lines ?? []).filter(l => !this.returningLines.includes(l)) : [];
    this.settlementLabel = o.settlement === 'PAY' ? 'Customer paid the difference'
      : o.settlement === 'REFUND' ? 'Difference refunded' : o.settlement === 'EVEN' ? 'Even exchange' : '';
    this.sections = [
      { id: 'sec-lines', label: 'Lines', count: o.lines?.length ?? 0 },
      { id: 'sec-payments', label: o.orderType === 'RETURN' ? 'Refunds' : this.isExchange ? 'Settlement' : 'Payments', count: o.payments?.length ?? 0 },
      ...(o.orderType === 'SALE' ? [{ id: 'sec-returns', label: 'Returns', count: o.returns?.length ?? 0 }] : []),
      { id: 'sec-info', label: 'Details' }
    ];
  }

  // ── Void ──────────────────────────────────────────────────────────────────
  openVoid(): void { this.voidReason = ''; this.voidError = null; this.voidDialog = true; }

  confirmVoid(): void {
    const reason = this.voidReason.trim();
    if (!reason || this.voidBusy) return;
    this.voidBusy = true;
    this.voidError = null;
    this.pos.voidOrder(this.uuid, reason).subscribe({
      next: res => {
        this.voidBusy = false;
        if (res && res.success === false) { this.voidError = res.message || 'The order was not voided.'; return; }
        this.voidDialog = false;
        this.messages.add({ severity: 'success', summary: 'Voided', detail: 'Order voided — stock and payments are reversed.' });
        if (res.result) this.setOrder(res.result); else this.load();
      },
      error: err => { this.voidBusy = false; this.voidError = posError(err, 'The order was not voided.'); }
    });
  }

  // ── Return ────────────────────────────────────────────────────────────────
  openReturn(): void {
    const o = this.order;
    if (!o) return;
    this.returnLines = o.lines.filter(l => (l.returnableQuantity ?? 0) > 0).map(line => ({ line, quantity: 0 }));
    this.returnReason = '';
    this.returnNote = '';
    this.reasonUuid = null;
    this.selectedReason = null;
    this.refundReference = '';
    this.returnError = null;
    this.managerReasons = [];
    const bought = new Date(o.createdAtLocal || o.createdAt).getTime();
    this.daysSincePurchase = isNaN(bought) ? 0 : Math.max(0, Math.floor((Date.now() - bought) / DAY));
    this.setPolicy(this.policyStore === o.storeUuid ? this.policy : null);
    this.recalc();
    this.returnDialog = true;
    this.loadRefundedBefore(o);
    this.loadReturnShifts(o);
    this.loadReasons();
    this.loadPolicy(o);
  }

  /** A39 — the reasons a cashier may pick for a return (active, RETURN or BOTH). */
  private loadReasons(): void {
    if (this.reasonsLoaded) return;
    this.returnsApi.getReasons().subscribe({
      next: res => {
        this.reasons = (res.result ?? []).filter(r => r.isActive !== false && r.appliesTo !== 'EXCHANGE')
          .sort((a, b) => a.sortOrder - b.sortOrder);
        this.reasonOptions = this.reasons.map(r => ({ label: r.name, value: r.uuid }));
        this.reasonsLoaded = this.reasons.length > 0;
      },
      // Without the list the A38 free-text reason is asked instead.
      error: () => { this.reasons = []; this.reasonOptions = []; this.reasonsLoaded = false; }
    });
  }

  /** A39 — the store's effective policy drives the refund methods, the fee and the manager hints. */
  private loadPolicy(o: PosOrder): void {
    if (this.policyStore === o.storeUuid && this.policy) return;
    this.returnsApi.getStorePolicy(o.storeUuid).subscribe({
      next: res => { this.policyStore = o.storeUuid; this.setPolicy(res.result ?? null); this.policyNote = null; this.recalc(); },
      error: err => {
        this.setPolicy(null);
        // e.g. "No return policy configured. Contact your administrator." — the server refuses the return the same way.
        this.policyNote = posError(err, 'The store\'s return policy could not be loaded; the server applies it when the return is sent.');
        this.recalc();
      }
    });
  }

  private setPolicy(p: EffectivePolicy | null): void {
    this.policy = p;
    const o = this.order;
    if (!o) return;
    const methods = allowedRefundMethods(o, p, this.modules.isFeatureEnabled('FEATURE_POS_CREDIT_NOTES'));
    this.refundMethodOptions = methods.map(m => ({ label: flowLabel(m), value: m }));
    const orig = originalRefundMethod(o);
    if (!methods.includes(this.refundMethod) || !this.returnDialog) this.refundMethod = methods.includes(orig) ? orig : methods[0] ?? 'CASH';
  }

  onReasonChange(): void {
    this.selectedReason = this.reasons.find(r => r.uuid === this.reasonUuid) ?? null;
    this.recalc();
  }

  /**
   * POSB — the last remaining units of a line refund exactly what is left of it, so the amounts already refunded per line
   * are needed: summed from the earlier return orders' lines (by originalLineUuid).
   */
  private loadRefundedBefore(o: PosOrder): void {
    this.returnHistoryReady = !(o.returns?.length);
    if (this.returnHistoryReady) return;
    forkJoin(o.returns.map(r => this.pos.getOrder(r.uuid))).subscribe({
      next: results => {
        const before = new Map<string, number>();
        for (const res of results) {
          for (const l of res.result?.lines ?? []) {
            // Exchange orders also carry new (positive) lines without an original line — only returned lines count.
            if (l.originalLineUuid) before.set(l.originalLineUuid, (before.get(l.originalLineUuid) ?? 0) + Math.abs(l.lineTotal));
          }
        }
        for (const rl of this.returnLines) rl.refundedBefore = r2(before.get(rl.line.uuid) ?? 0);
        this.returnHistoryReady = true;
        this.onQuantityChange();
      },
      error: () => {
        // Without the history the last-units refund cannot be known; the server's 400 then names the right figure.
        this.returnHistoryReady = true;
      }
    });
  }

  /** POSB — the return is booked to an open shift of the store, by default the original terminal's. */
  private loadReturnShifts(o: PosOrder): void {
    this.returnShiftOptions = [];
    this.returnShiftUuid = null;
    this.shiftTerminals.clear();
    if (!this.auth.hasPermission('POS_SHIFT_VIEW')) return;
    this.pos.getShifts({ storeUuid: o.storeUuid, status: 'OPEN', page: 1, pageSize: 50 }).subscribe({
      next: res => {
        const shifts = res.result?.data ?? [];
        for (const s of shifts) this.shiftTerminals.set(s.uuid, s.terminalUuid);
        this.returnShiftOptions = shifts.map(s => ({ label: `${s.terminalName} · ${s.shiftNumber}`, value: s.uuid }));
        this.returnShiftUuid = shifts.find(s => s.terminalUuid === o.terminalUuid)?.uuid ?? shifts[0]?.uuid ?? null;
      },
      error: () => { this.returnShiftOptions = []; }
    });
  }

  onQuantityChange(): void { this.recalc(); }

  /** Return total → restocking fee → refund; and what will need a manager. */
  recalc(): void {
    this.returnTotal = estimateReturnTotal(this.returnLines);
    this.feePct = this.selectedReason?.restockingFeeOverride ?? this.policy?.restockingFeePct ?? 0;
    this.restockingFee = restockingFee(this.returnTotal, this.policy?.restockingFeePct, this.selectedReason?.restockingFeeOverride);
    this.refundAmount = r2(this.returnTotal - this.restockingFee);
    const hints: string[] = [];
    if (this.selectedReason?.requiresManager) hints.push(managerNeedLabel('REASON'));
    const max = this.policy?.maxReturnNoAuth;
    if (max != null && this.refundAmount > max) hints.push(managerNeedLabel('AMOUNT'));
    const win = this.policy?.returnWindowDays;
    if (win != null && this.daysSincePurchase > win) hints.push(`${managerNeedLabel('WINDOW')} (${win} days)`);
    this.managerHints = hints;
  }

  returnAll(): void {
    for (const l of this.returnLines) l.quantity = l.line.returnableQuantity;
    this.onQuantityChange();
  }

  /** Why the return cannot be sent yet, or null. */
  get returnProblem(): string | null {
    if (!this.returnLines.some(l => l.quantity > 0)) return 'Enter a quantity to return on at least one line.';
    const over = this.returnLines.find(l => l.quantity > l.line.returnableQuantity);
    if (over) return `At most ${over.line.returnableQuantity} of ${over.line.productName} can be returned.`;
    if (this.reasonsLoaded) {
      if (!this.reasonUuid) return 'Pick a return reason.';
      // Contract §0 — verbatim.
      if (this.selectedReason?.requiresNote && !this.returnNote.trim()) return 'A note is required for this reason.';
    } else if (!this.returnReason.trim()) return 'A reason is required.';
    if (this.policy && !this.refundMethodOptions.length) return 'The store\'s return policy allows no refund method for this sale.';
    if (!this.refundMethod) return 'Pick a refund method.';
    if (!this.returnHistoryReady) return 'Loading the earlier returns…';
    return null;
  }

  confirmReturn(): void {
    if (this.returnProblem || this.returnBusy || !this.order) return;
    this.returnBusy = true;
    this.returnError = null;
    this.managerReasons = [];
    const note = this.returnNote.trim() || null;
    const reasonText = this.reasonsLoaded
      ? `${this.selectedReason?.name ?? ''}${note ? ': ' + note : ''}`
      : this.returnReason.trim();
    const reference = this.refundReference.trim() || null;
    const terminalUuid = (this.returnShiftUuid && this.shiftTerminals.get(this.returnShiftUuid)) || null;
    this.pos.returnOrder(this.uuid, {
      localId: newLocalId(),
      ...(this.returnShiftUuid ? { shiftUuid: this.returnShiftUuid } : {}),
      ...(terminalUuid ? { terminalUuid } : {}),
      reason: reasonText,
      ...(this.reasonsLoaded ? { reasonUuid: this.reasonUuid, note } : {}),
      lines: this.returnLines.filter(l => l.quantity > 0).map(l => ({ originalLineUuid: l.line.uuid, quantity: l.quantity })),
      refundMethod: this.refundMethod,
      refundReference: reference,
      // A38 servers read `refunds`; one row = the refund after the restocking fee.
      refunds: [{ method: this.refundMethod, amount: this.refundAmount, reference }]
    }).subscribe({
      next: res => {
        this.returnBusy = false;
        if (res && res.success === false) { this.returnError = res.message || 'The return was not saved.'; return; }
        this.returnDialog = false;
        const cn = res.result?.storeCredit;
        this.messages.add({ severity: 'success', summary: 'Returned',
          detail: `Return ${res.result?.orderNumber ?? ''} recorded.${cn ? ` Store credit ${cn.number} issued.` : ''}` });
        if (res.result?.uuid) this.router.navigate([POS_ROUTE, 'orders', res.result.uuid]);
        else this.load();
      },
      // e.g. "Return window expired (30 days)." / "Manager authorization required." — verbatim, plus why.
      error: err => {
        this.returnBusy = false;
        this.returnError = posError(err, 'The return was not saved.');
        const m = managerRequired(err);
        if (m.requiresManager) {
          this.managerReasons = m.reasons.length ? m.reasons.map(managerNeedLabel) : ['the store\'s return policy needs a manager here'];
        }
      }
    });
  }

  back(): void { this.router.navigate([POS_ROUTE, 'orders']); }
}
