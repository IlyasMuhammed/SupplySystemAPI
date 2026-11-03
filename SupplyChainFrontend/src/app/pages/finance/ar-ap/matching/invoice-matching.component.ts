import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable } from 'rxjs';

import { AR_AP_PERMS, ArApService, MATCHING_ROUTE, MatchDetail, MatchLine } from '../../../../services/ar-ap.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import {
  arApError, canApproveMatch, canOverrideMatch, matchLabel, matchState, matchTone, money, priceVariance, reasonProblem, round2, signedMoney
} from '../ar-ap.shared';

export interface MatchTotals { po: number; invoiceLines: number; variance: number; percent: number | null; }

/** The comparison's totals: PO and invoice lines summed (pre-tax, as compared); variance and % as the server judged them. */
export function matchTotals(m: MatchDetail | null): MatchTotals {
  const po = round2((m?.lines ?? []).reduce((s, l) => s + Number(l.poLineTotal || 0), 0));
  const inv = round2((m?.lines ?? []).reduce((s, l) => s + Number(l.invoiceLineTotal || 0), 0));
  const variance = m?.varianceAmount ?? round2(inv - po);
  const percent = m?.variancePercent ?? (po ? Math.round((variance / po) * 10000) / 100 : null);
  return { po, invoiceLines: inv, variance, percent };
}

/**
 * A48 §8.5, tasks 571–575 / 582 — `/finance/ap/matching/:invoiceUuid` (contract §6): PO vs GRN vs invoice per line with
 * the quantity and price variances highlighted (BR-MT-001..004) and each line's status, the totals variance and the
 * tolerance rule that applied; run the match again (MATCH_EXECUTE, 3-way or 2-way), override a variance with a reason
 * (MATCH_OVERRIDE, BR-MT-006) and approve (MATCH_APPROVE) — the existing approval path: the invoice becomes payable and
 * is posted through the A44 gateway (D-7); reject with a reason (MATCH_APPROVE).
 */
@Component({
  selector: 'app-invoice-matching',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, ConfirmDialogModule, DialogModule, TextareaModule, ToastModule, ...FLOW],
  providers: [MessageService, ConfirmationService],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<p-toast></p-toast>
<p-confirmDialog key="im" [style]="{ width: '30rem', maxWidth: '96vw' }"></p-confirmDialog>
<sf-page [crumbs]="['Finance', 'Payables', 'Invoice matching', m?.invoiceNumber]">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <ng-container *ngIf="m">
      <p-button *ngIf="canApprove && approvable" label="Approve &amp; post" icon="pi pi-check" [disabled]="busy" [loading]="busy" (onClick)="confirmApprove()" data-testid="approve"></p-button>
      <p-button *ngIf="canOverride && overridable" label="Override with reason" icon="pi pi-unlock" [text]="true" (onClick)="openOverride()" data-testid="override"></p-button>
      <ng-container *ngIf="canExecute && rematchable">
        <p-button label="Match 3-way" icon="pi pi-sync" [text]="true" [disabled]="busy" (onClick)="rematch('ThreeWay')" data-testid="rematch"></p-button>
        <p-button *ngIf="twoWayAllowed" label="Match 2-way" [text]="true" [disabled]="busy" (onClick)="rematch('TwoWay')" data-testid="rematch-2way"></p-button>
      </ng-container>
      <ng-container *ngIf="canApprove && rejectable">
        <span class="sf-sep"></span>
        <p-button label="Reject" icon="pi pi-times" severity="danger" [text]="true" (onClick)="openReason('reject')" data-testid="reject"></p-button>
      </ng-container>
    </ng-container>
    <span class="sf-end">
      <a *ngIf="m" class="p-button p-button-text" [routerLink]="['/portal/pages/finance/invoices', m.invoiceUuid]" data-testid="invoice-link"><i class="pi pi-file"></i>&nbsp;Invoice</a>
      <a class="p-button p-button-text" [routerLink]="listRoute" data-testid="back"><i class="pi pi-arrow-left"></i>&nbsp;Back</a>
    </span>
  </div>

  <div class="sf-body" *ngIf="loading || loadError">
    <div class="sf-card sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>
  </div>

  <ng-container *ngIf="!loading && m">
    <header class="sf-hdr">
      <div class="sf-t"><div>
        <div class="sf-kicker">Invoice matching · {{ m.vendorName || '' }}<ng-container *ngIf="m.matchType"> · {{ label(m.matchType) }}</ng-container></div>
        <h1><span class="sf-mono">{{ m.invoiceNumber }}</span>
          <span class="sf-pill" [ngClass]="tone(state)" data-testid="status">{{ label(state) }}</span>
          <span class="sf-pill ok" *ngIf="m.autoApproved">Auto-approved</span></h1>
        <div class="sf-meta">Vendor invoice {{ m.supplierInvoiceNo || '—' }} · PO <span class="sf-mono">{{ m.poNumber || '—' }}</span>
          · GRN <span class="sf-mono">{{ m.grnNumber || '—' }}</span><ng-container *ngIf="m.matchedAt"> · matched {{ m.matchedAt | date:'dd MMM yyyy, HH:mm' }}
          <ng-container *ngIf="m.matchedByName"> by {{ m.matchedByName }}</ng-container></ng-container></div>
      </div></div>
      <div class="sf-facts">
        <div class="sf-kpi"><span class="l">PO total</span><span class="v sm" data-testid="po-total">{{ amount(totals.po) }}</span></div>
        <div class="sf-kpi" data-testid="inv-total"><span class="l">Invoice lines</span><span class="v sm">{{ amount(totals.invoiceLines) }}</span>
          <span class="s">total {{ amount(m.totalAmount) }} {{ m.currencyCode }}</span></div>
        <div class="sf-kpi"><span class="l">Variance</span><span class="v sm" [class.er]="m.withinTolerance === false" data-testid="variance">{{ signed(totals.variance) }}</span>
          <span class="s">{{ totals.percent !== null ? totals.percent + '%' : '' }}</span></div>
      </div>
    </header>

    <div class="sf-body">
      <div class="gl-note" *ngIf="m.overrideReason" data-testid="override-note"><i class="pi pi-unlock"></i>
        <div>Overridden<ng-container *ngIf="m.overriddenByName"> by {{ m.overriddenByName }}</ng-container><ng-container *ngIf="m.overriddenAt">
          on {{ m.overriddenAt | date:'dd MMM yyyy' }}</ng-container>: {{ m.overrideReason }}</div></div>
      <div class="gl-note wn" *ngIf="m.autoApproveMessage" data-testid="auto-approve-message"><i class="pi pi-info-circle"></i>
        <div>Within tolerance, but the automatic approval was refused: {{ m.autoApproveMessage }}</div></div>
      <div class="gl-note ok" *ngIf="m.matchStatus === 'Approved'" data-testid="approved-note"><i class="pi pi-check"></i><div>Approved — the invoice is payable.</div></div>
      <div class="gl-note" *ngIf="!m.lineMatchResult && m.matchStatus !== 'Approved'" data-testid="not-matched"><i class="pi pi-info-circle"></i>
        <div>No line-by-line match has been run for this invoice yet.<ng-container *ngIf="canExecute"> Use Match 3-way (or 2-way when no goods receipt is needed).</ng-container></div></div>

      <!-- Tasks 572 / 573 / 582 — the comparison scrolls sideways on a phone -->
      <section class="sf-card p0">
        <div class="sf-ch sf-pad"><h2>Lines <span class="sf-count">{{ m.lines.length }}</span></h2>
          <span class="sf-end sf-mu sf-sm" *ngIf="m.toleranceRule" data-testid="tolerance">Tolerance rule {{ m.toleranceRule.name }}
            · {{ m.withinTolerance === true ? 'within tolerance' : m.withinTolerance === false ? 'EXCEEDED' : '' }}</span></div>
        <div class="gl-scroll">
          <table class="aa-table" style="min-width:56rem" data-testid="lines">
            <thead><tr><th>Line</th><th>Item</th><th class="gl-num">PO qty × price</th><th class="gl-num">GRN qty</th><th class="gl-num">Invoice qty × price</th>
              <th class="gl-num">Variance</th><th>Status</th></tr></thead>
            <tbody>
              <tr *ngFor="let l of m.lines" [attr.data-testid]="'line-' + l.lineNo">
                <td>{{ l.lineNo }}</td>
                <td>{{ l.itemDescription }}</td>
                <td class="gl-num"><ng-container *ngIf="l.poQuantity !== null && l.poQuantity !== undefined; else noPo">{{ qty(l.poQuantity) }} × {{ price(l.poUnitPrice) }}
                  <div class="sf-mu sf-sm">{{ amount(l.poLineTotal) }}</div></ng-container><ng-template #noPo><span class="sf-mu">no PO line</span></ng-template></td>
                <td class="gl-num" [class.aa-bad]="qtyOff(l)">{{ l.grnQuantity === null || l.grnQuantity === undefined ? '—' : qty(l.grnQuantity) }}</td>
                <td class="gl-num"><span [class.aa-bad]="qtyOff(l)">{{ qty(l.invoiceQuantity) }}</span> × <span [class.aa-bad]="priceOff(l)">{{ price(l.invoiceUnitPrice) }}</span>
                  <div class="sf-mu sf-sm">{{ amount(l.invoiceLineTotal) }}</div></td>
                <td class="gl-num" [attr.data-testid]="'var-' + l.lineNo">
                  <div *ngIf="priceOff(l)" class="aa-warn">{{ priceText(l) }}</div>
                  <div *ngIf="qtyOff(l)" class="aa-warn">{{ qtyText(l) }}</div>
                  <div class="sf-mu sf-sm" *ngIf="l.totalVariance">{{ signed(l.totalVariance) }}</div></td>
                <td><span class="sf-pill" [ngClass]="tone(l.lineStatus)" [attr.data-testid]="'line-status-' + l.lineNo">
                  <i class="pi" [ngClass]="l.lineStatus === 'Matched' ? 'pi-check' : l.lineStatus === 'Overridden' ? 'pi-unlock' : 'pi-exclamation-triangle'"></i> {{ label(l.lineStatus) }}</span></td>
              </tr>
              <tr *ngIf="!m.lines.length"><td colspan="7" class="sf-empty">No lines compared yet.</td></tr>
            </tbody>
            <tfoot *ngIf="m.lines.length"><tr data-testid="totals">
              <td colspan="2">TOTAL</td><td class="gl-num">{{ amount(totals.po) }}</td><td></td>
              <td class="gl-num">{{ amount(totals.invoiceLines) }}</td>
              <td class="gl-num">{{ signed(totals.variance) }}<span class="sf-mu sf-sm" *ngIf="totals.percent !== null"> ({{ totals.percent }}%)</span></td>
              <td>{{ m.withinTolerance === true ? 'within tolerance' : m.withinTolerance === false ? 'EXCEEDED' : '' }}</td></tr></tfoot>
          </table>
        </div>
      </section>
    </div>
  </ng-container>
</sf-page>

<!-- Task 574 (override) and Reject (§6, MATCH_APPROVE) -->
<p-dialog [(visible)]="overrideOpen" [modal]="true" [draggable]="false" [resizable]="false" [style]="{ width: '30rem', maxWidth: '96vw' }" styleClass="sdlg">
  <ng-template pTemplate="header">
    <div class="sdlg-header">
      <div class="sdlg-header-icon" [ngClass]="reasonFor === 'reject' ? 'sdlg-icon-red' : 'sdlg-icon-amber'"><i class="pi" [ngClass]="reasonFor === 'reject' ? 'pi-times-circle' : 'pi-unlock'"></i></div>
      <div class="sdlg-header-text">
        <div class="sdlg-title">{{ reasonFor === 'reject' ? 'Reject invoice' : 'Override match variance' }}</div>
        <div class="sdlg-subtitle" *ngIf="m">{{ m.invoiceNumber }}</div>
      </div>
    </div>
  </ng-template>
  <div class="aa-dlg-body">
    <div class="sf-mu" *ngIf="reasonFor === 'override'">The mismatched lines are accepted as they are; the invoice can then be approved.</div>
    <div class="sf-mu" *ngIf="reasonFor === 'reject'">The invoice is rejected and will not be paid; the vendor has to send a corrected one.</div>
    <div class="sf-fld"><label for="im-reason" class="sf-req">Reason</label><textarea pTextarea id="im-reason" rows="3" [(ngModel)]="reason" [attr.maxlength]="reasonFor === 'reject' ? 300 : 500" data-testid="reason"></textarea></div>
    <div class="gl-note er" *ngIf="dialogError" data-testid="dialog-error"><i class="pi pi-exclamation-triangle"></i><div>{{ dialogError }}</div></div>
  </div>
  <ng-template pTemplate="footer">
    <div class="sdlg-footer">
      <div class="sdlg-footer-btns">
        <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="overrideOpen = false"></p-button>
        <p-button [label]="reasonFor === 'reject' ? 'Reject invoice' : 'Override'" [icon]="reasonFor === 'reject' ? 'pi pi-times-circle' : 'pi pi-unlock'" [severity]="reasonFor === 'reject' ? 'danger' : undefined" [disabled]="busy" [loading]="busy"
                  (onClick)="submitOverride()" data-testid="confirm-reason"></p-button>
      </div>
    </div>
  </ng-template>
</p-dialog>`
})
export class InvoiceMatchingComponent implements OnInit {
  private readonly api = inject(ArApService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService);
  private readonly route = inject(ActivatedRoute, { optional: true });

  readonly listRoute = MATCHING_ROUTE;

  invoiceUuid = '';
  m: MatchDetail | null = null;
  loading = false;
  loadError = '';
  busy = false;
  overrideOpen = false;
  reason = '';
  dialogError = '';

  get canExecute(): boolean { return this.auth.hasPermission(AR_AP_PERMS.MATCH_EXECUTE); }
  get canOverride(): boolean { return this.auth.hasPermission(AR_AP_PERMS.MATCH_OVERRIDE); }
  get canApprove(): boolean { return this.auth.hasPermission(AR_AP_PERMS.MATCH_APPROVE); }
  get state(): string { return matchState(this.m); }
  get overridable(): boolean { return canOverrideMatch(this.m); }
  get approvable(): boolean { return canApproveMatch(this.m); }
  /** §6 — only a pending, matched or variance invoice can be matched. */
  get rematchable(): boolean { return !!this.m && ['Pending', 'Matched', 'Variance'].includes(this.m.matchStatus); }
  /** REV (§6) — a PO with goods received is matched three-way only (400 otherwise). */
  get twoWayAllowed(): boolean {
    return !!this.m && !this.m.grnUuid && !this.m.lines.some(l => Number(l.grnQuantity || 0) > 0);
  }
  get totals(): MatchTotals { return matchTotals(this.m); }

  ngOnInit(): void {
    this.invoiceUuid = this.route?.snapshot?.paramMap?.get('uuid') ?? '';
    if (this.invoiceUuid) this.load();
  }

  load(): void {
    this.loading = !this.m;
    this.loadError = '';
    this.api.getMatch(this.invoiceUuid).subscribe({
      next: m => { this.loading = false; this.m = m; },
      error: err => { this.loading = false; this.loadError = arApError(err, 'The match could not be loaded.'); }
    });
  }

  private apply(call: Observable<MatchDetail>, done: string, fallback: string, inDialog = false): void {
    this.busy = true;
    call.subscribe({
      next: m => {
        this.busy = false; this.overrideOpen = false;
        if (m?.invoiceUuid) this.m = m; else this.load();
        this.messages.add({ severity: 'success', summary: 'Done', detail: done, life: 3000 });
      },
      error: err => {
        this.busy = false;
        const msg = arApError(err, fallback);
        if (inDialog) this.dialogError = msg; else this.messages.add({ severity: 'error', summary: 'Error', detail: msg, life: 8000 });
      }
    });
  }

  rematch(type: 'ThreeWay' | 'TwoWay'): void {
    this.apply(this.api.match(this.invoiceUuid, type), 'The invoice was matched line by line.', 'The invoice could not be matched.');
  }

  confirmApprove(): void {
    const m = this.m;
    if (!m) return;
    this.confirm.confirm({
      key: 'im', header: 'Approve invoice', icon: 'pi pi-check', acceptLabel: 'Approve', rejectLabel: 'Cancel',
      message: `Approve ${m.invoiceNumber} for ${money(m.totalAmount)} ${m.currencyCode}? It becomes payable and is posted.`,
      accept: () => this.apply(this.api.approveMatch(m.invoiceUuid), `${m.invoiceNumber} is approved.`, 'The invoice could not be approved.')
    });
  }

  reasonFor: 'override' | 'reject' = 'override';

  openOverride(): void { this.openReason('override'); }
  openReason(kind: 'override' | 'reject'): void { this.reasonFor = kind; this.reason = ''; this.dialogError = ''; this.overrideOpen = true; }

  /** Not once approved, rejected or reversed. */
  get rejectable(): boolean { return !!this.m && !['Approved', 'Rejected', 'Reversed'].includes(this.m.matchStatus); }

  submitOverride(): void {
    const reject = this.reasonFor === 'reject';
    this.dialogError = (reject
      ? (!this.reason.trim() ? 'Say why the invoice is being rejected.' : this.reason.trim().length > 300 ? 'The reason can be at most 300 characters.' : '')
      : reasonProblem(this.reason, 'An override needs a reason.')) ?? '';
    if (this.dialogError) return;
    const r = this.reason.trim();
    if (reject) this.apply(this.api.rejectMatch(this.invoiceUuid, r), `${this.m?.invoiceNumber ?? 'The invoice'} is rejected.`, 'The invoice could not be rejected.', true);
    else this.apply(this.api.overrideMatch(this.invoiceUuid, r), 'The variance is overridden.', 'The variance could not be overridden.', true);
  }

  priceOff(l: MatchLine): boolean {
    if (l.lineStatus === 'PriceMismatch') return true;
    return l.lineStatus !== 'Matched' && l.poUnitPrice !== null && l.poUnitPrice !== undefined && Math.abs(l.invoiceUnitPrice - l.poUnitPrice) >= 0.00005;
  }

  qtyOff(l: MatchLine): boolean {
    if (l.lineStatus === 'QuantityMismatch') return true;
    const against = l.grnQuantity ?? l.poQuantity;
    return l.lineStatus !== 'Matched' && against !== null && against !== undefined && l.invoiceQuantity > against;
  }

  priceText(l: MatchLine): string {
    const v = priceVariance(l.poUnitPrice ?? 0, l.invoiceUnitPrice);
    const amount = l.priceVariance ?? v.amount;
    const pct = l.priceVariancePercent ?? v.percent;
    return `Price ${amount > 0 ? '+' : ''}${this.price(amount)}/unit${pct !== null ? ` (${pct > 0 ? '+' : ''}${pct}%)` : ''}`;
  }

  qtyText(l: MatchLine): string {
    const received = l.grnQuantity !== null && l.grnQuantity !== undefined;
    const v = l.quantityVariance ?? (l.invoiceQuantity - (l.grnQuantity ?? l.poQuantity ?? 0));
    return `Qty ${v > 0 ? '+' : ''}${this.qty(v)} vs ${received ? 'received' : 'ordered'}`;
  }

  tone(s: string | null | undefined): string { return matchTone(s); }
  label(s: string | null | undefined): string { return matchLabel(s); }
  amount(n: number | null | undefined): string { return money(n); }
  signed(n: number | null | undefined): string { return signedMoney(n); }
  qty(n: number | null | undefined): string { return Number(n ?? 0).toLocaleString('en-US', { maximumFractionDigits: 4 }); }
  price(n: number | null | undefined): string { return Number(n ?? 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 }); }
}
