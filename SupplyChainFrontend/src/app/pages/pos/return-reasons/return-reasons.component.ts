import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
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
  APPLIES_TO_OPTIONS, PosReturnsAdminService, ReturnReason, ReturnReasonUpsert, appliesToLabel, isValidReasonCode, reasonUpsertFrom
} from '../../../services/pos-returns-admin.service';
import { POS_ROUTE, posError } from '../../../services/pos-admin.service';
import { FLOW } from '../../../shared/flow';

/** A grid row (built in load(), never in a getter). */
export interface ReasonRow extends ReturnReason { appliesLabel: string; feeLabel: string; first: boolean; last: boolean; }

export function reasonRows(reasons: readonly ReturnReason[]): ReasonRow[] {
  const sorted = [...reasons].sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));
  return sorted.map((r, i) => ({
    ...r,
    appliesLabel: appliesToLabel(r.appliesTo),
    feeLabel: r.restockingFeeOverride == null ? 'Policy' : r.restockingFeeOverride === 0 ? 'Waived' : `${r.restockingFeeOverride}%`,
    first: i === 0,
    last: i === sorted.length - 1
  }));
}

/** New sort orders when `row` swaps with its neighbour (equal orders are separated by one). */
export function swapOrders(row: ReturnReason, neighbour: ReturnReason, direction: 'up' | 'down'): { row: number; neighbour: number } {
  if (row.sortOrder !== neighbour.sortOrder) return { row: neighbour.sortOrder, neighbour: row.sortOrder };
  return direction === 'up'
    ? { row: neighbour.sortOrder - 1, neighbour: neighbour.sortOrder }
    : { row: neighbour.sortOrder + 1, neighbour: neighbour.sortOrder };
}

/**
 * A39 — `/portal/pages/pos/return-reasons` (POS_RETURN_REASON_MANAGE): the reasons cashiers pick on a return or exchange
 * (API-CONTRACT §1). Create / edit in a dialog, reorder with up / down (sort order), activate / deactivate.
 */
@Component({
  selector: 'app-pos-return-reasons',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule, ButtonModule, CheckboxModule, DialogModule, DropdownModule, InputNumberModule, InputTextModule,
    TableModule, TextareaModule, ToastModule, TooltipModule, ...FLOW
  ],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Point of Sale', 'Return reasons']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="New reason" icon="pi pi-plus" (onClick)="open(null)" data-testid="new-reason"></p-button>
    <p-button label="Refresh" icon="pi pi-refresh" [text]="true" (onClick)="load()"></p-button>
    <a class="p-button p-button-text" [routerLink]="[posRoute, 'return-policy']"><i class="pi pi-shield"></i>&nbsp;Return policy</a>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head">
      <div>
        <h1>Return reasons <span class="sf-count" data-testid="reason-count">{{ rows.length }}</span></h1>
        <div class="sf-sub">What the cashier picks when taking an item back; the order here is the order on the till</div>
      </div>
    </div>
    <div class="sf-card p0">
      <p-table [value]="rows" [loading]="loading" [tableStyle]="{ 'min-width': '70rem' }" data-testid="reason-table">
        <ng-template pTemplate="header">
          <tr><th>Code</th><th>Name</th><th>Note</th><th>Manager</th><th>Applies to</th><th>Restocking fee</th>
              <th class="text-right">Sort</th><th>Status</th><th></th></tr>
        </ng-template>
        <ng-template pTemplate="body" let-r>
          <tr [attr.data-testid]="'reason-' + r.code">
            <td class="sf-mono">{{ r.code }}</td>
            <td>{{ r.name }}<div class="sf-mu sf-sm" *ngIf="r.description">{{ r.description }}</div></td>
            <td>{{ r.requiresNote ? 'Required' : '—' }}</td>
            <td><span *ngIf="r.requiresManager" class="sf-pill wn">Required</span><span *ngIf="!r.requiresManager">—</span></td>
            <td>{{ r.appliesLabel }}</td>
            <td>{{ r.feeLabel }}</td>
            <td class="text-right">{{ r.sortOrder }}</td>
            <td><span class="sf-pill" [ngClass]="r.isActive ? 'ok' : ''" data-testid="reason-status">{{ r.isActive ? 'Active' : 'Inactive' }}</span></td>
            <td>
              <div class="rr-actions">
                <p-button icon="pi pi-arrow-up" [text]="true" size="small" pTooltip="Move up" [disabled]="r.first || busy"
                          (onClick)="move(r, 'up')" data-testid="reason-up"></p-button>
                <p-button icon="pi pi-arrow-down" [text]="true" size="small" pTooltip="Move down" [disabled]="r.last || busy"
                          (onClick)="move(r, 'down')" data-testid="reason-down"></p-button>
                <p-button icon="pi pi-pencil" [text]="true" size="small" pTooltip="Edit" (onClick)="open(r)" data-testid="reason-edit"></p-button>
                <p-button [icon]="r.isActive ? 'pi pi-ban' : 'pi pi-check-circle'" [severity]="r.isActive ? 'danger' : undefined" [text]="true"
                          size="small" [pTooltip]="r.isActive ? 'Deactivate' : 'Activate'" [disabled]="busy" (onClick)="toggle(r)"
                          data-testid="reason-toggle"></p-button>
              </div>
            </td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage">
          <tr><td colspan="9" class="sf-empty">{{ loadError || 'No return reasons yet.' }}</td></tr>
        </ng-template>
      </p-table>
    </div>
  </div>
</sf-page>

<p-dialog [header]="editing ? 'Edit reason — ' + editing.code : 'New return reason'" [(visible)]="dialog" [modal]="true"
          [style]="{ width: '36rem' }" [breakpoints]="{ '640px': '96vw' }" data-testid="reason-dialog">
  <div class="sf-pill er rr-err" *ngIf="error" role="alert" data-testid="reason-error">{{ error }}</div>
  <div class="sf-fields">
    <div class="sf-fld">
      <label for="rrCode" class="sf-req">Code</label>
      <input pInputText id="rrCode" [ngModel]="model.code" (ngModelChange)="model.code = upper($event)" maxlength="20" data-testid="reason-code" />
      <small class="sf-mu">A–Z, 0–9 and _ only, up to 20.</small>
    </div>
    <div class="sf-fld">
      <label for="rrName" class="sf-req">Name</label>
      <input pInputText id="rrName" [(ngModel)]="model.name" maxlength="100" data-testid="reason-name" />
    </div>
    <div class="sf-fld rr-wide">
      <label for="rrDesc">Description</label>
      <textarea pTextarea id="rrDesc" rows="2" [(ngModel)]="model.description" maxlength="500"></textarea>
    </div>
    <div class="sf-fld">
      <label for="rrApplies">Applies to</label>
      <p-dropdown inputId="rrApplies" [options]="appliesOptions" [(ngModel)]="model.appliesTo" optionLabel="label" optionValue="value"
                  appendTo="body" styleClass="w-full" data-testid="reason-applies"></p-dropdown>
    </div>
    <div class="sf-fld">
      <label for="rrFee">Restocking fee override (%)</label>
      <p-inputnumber inputId="rrFee" [(ngModel)]="model.restockingFeeOverride" [min]="0" [max]="100" [maxFractionDigits]="2"
                     placeholder="Policy's fee" styleClass="w-full" data-testid="reason-fee"></p-inputnumber>
      <small class="sf-mu">Blank = the store policy's fee; 0 waives it.</small>
    </div>
    <div class="sf-fld rr-check">
      <p-checkbox inputId="rrNote" [(ngModel)]="model.requiresNote" [binary]="true" data-testid="reason-requires-note"></p-checkbox>
      <label for="rrNote">The cashier must add a note</label>
    </div>
    <div class="sf-fld rr-check">
      <p-checkbox inputId="rrMgr" [(ngModel)]="model.requiresManager" [binary]="true" data-testid="reason-requires-manager"></p-checkbox>
      <label for="rrMgr">A manager must authorise</label>
    </div>
    <div class="sf-fld">
      <label for="rrSort">Sort order</label>
      <p-inputnumber inputId="rrSort" [(ngModel)]="model.sortOrder" [useGrouping]="false" styleClass="w-full" data-testid="reason-sort"></p-inputnumber>
    </div>
    <div class="sf-fld rr-check">
      <p-checkbox inputId="rrAct" [(ngModel)]="model.isActive" [binary]="true" data-testid="reason-active"></p-checkbox>
      <label for="rrAct">Active</label>
    </div>
  </div>
  <small class="rr-bad" *ngIf="problem as p" data-testid="reason-problem">{{ p }}</small>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" severity="secondary" [text]="true" (onClick)="dialog = false"></p-button>
    <p-button label="Save" icon="pi pi-check" [loading]="busy" [disabled]="!!problem" (onClick)="save()" data-testid="reason-save"></p-button>
  </ng-template>
</p-dialog>
  `,
  styles: [`
    .rr-actions { display: flex; gap: .1rem; justify-content: flex-end; }
    .rr-err { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .rr-wide { grid-column: 1 / -1; }
    .rr-check { flex-direction: row; align-items: center; gap: .5rem; }
    .rr-bad { color: var(--sms-danger); }
    :host ::ng-deep .w-full { width: 100%; }
  `],
  providers: [MessageService]
})
export class PosReturnReasonsComponent implements OnInit {
  private readonly service = inject(PosReturnsAdminService);
  private readonly messages = inject(MessageService);

  readonly posRoute = POS_ROUTE;
  readonly appliesOptions = APPLIES_TO_OPTIONS;

  rows: ReasonRow[] = [];
  loading = true;
  loadError: string | null = null;
  busy = false;

  dialog = false;
  editing: ReasonRow | null = null;
  model: ReturnReasonUpsert = this.blank();
  error: string | null = null;

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.loadError = null;
    this.service.getReasons({ includeInactive: true }).subscribe({
      next: res => { this.loading = false; this.rows = reasonRows(res.result ?? []); },
      error: err => { this.loading = false; this.rows = []; this.loadError = posError(err, 'The return reasons could not be loaded.'); }
    });
  }

  private blank(): ReturnReasonUpsert {
    const max = (this.rows ?? []).reduce((m, r) => Math.max(m, r.sortOrder), 0);
    return { code: '', name: '', description: null, requiresNote: false, requiresManager: false, appliesTo: 'BOTH',
             restockingFeeOverride: null, sortOrder: max + 10, isActive: true };
  }

  upper(v: string): string { return (v ?? '').toUpperCase().replace(/\s+/g, '_'); }

  open(r: ReasonRow | null): void {
    this.editing = r;
    this.model = r ? reasonUpsertFrom(r) : this.blank();
    this.error = null;
    this.dialog = true;
  }

  /** Why the dialog cannot be saved, or null. */
  get problem(): string | null {
    if (!isValidReasonCode(this.model.code)) return 'The code is 1–20 characters of A–Z, 0–9 and _.';
    if (!(this.model.name ?? '').trim()) return 'A name is required.';
    const fee = this.model.restockingFeeOverride;
    if (fee != null && (fee < 0 || fee > 100)) return 'The restocking fee must be between 0 and 100%.';
    return null;
  }

  save(): void {
    if (this.problem || this.busy) return;
    this.busy = true;
    this.error = null;
    const req: ReturnReasonUpsert = {
      ...this.model, code: this.model.code.trim(), name: this.model.name.trim(),
      description: (this.model.description ?? '').trim() || null,
      restockingFeeOverride: this.model.restockingFeeOverride ?? null, sortOrder: this.model.sortOrder ?? 0
    };
    const call = this.editing ? this.service.updateReason(this.editing.uuid, req) : this.service.createReason(req);
    call.subscribe({
      next: res => {
        this.busy = false;
        if (res && res.success === false) { this.error = res.message || 'The reason was not saved.'; return; }
        this.dialog = false;
        this.messages.add({ severity: 'success', summary: 'Saved', detail: this.editing ? 'Reason updated.' : 'Reason created.' });
        this.load();
      },
      // e.g. a duplicate code — verbatim.
      error: err => { this.busy = false; this.error = posError(err, 'The reason was not saved.'); }
    });
  }

  move(r: ReasonRow, direction: 'up' | 'down'): void {
    const i = this.rows.findIndex(x => x.uuid === r.uuid);
    const n = this.rows[direction === 'up' ? i - 1 : i + 1];
    if (i < 0 || !n || this.busy) return;
    const orders = swapOrders(r, n, direction);
    this.busy = true;
    forkJoin([
      this.service.updateReason(r.uuid, { ...reasonUpsertFrom(r), sortOrder: orders.row }),
      this.service.updateReason(n.uuid, { ...reasonUpsertFrom(n), sortOrder: orders.neighbour })
    ]).subscribe({
      next: () => { this.busy = false; this.load(); },
      error: err => { this.busy = false; this.fail(posError(err, 'The order was not changed.')); this.load(); }
    });
  }

  toggle(r: ReasonRow): void {
    if (this.busy) return;
    this.busy = true;
    this.service.setReasonStatus(r.uuid, !r.isActive).subscribe({
      next: res => {
        this.busy = false;
        if (res && res.success === false) { this.fail(res.message); return; }
        this.messages.add({ severity: 'success', summary: 'Done', detail: r.isActive ? `${r.name} deactivated.` : `${r.name} activated.` });
        this.load();
      },
      error: err => { this.busy = false; this.fail(posError(err, 'The status was not changed.')); }
    });
  }

  private fail(message?: string | null): void {
    this.messages.add({ severity: 'error', summary: 'Not done', detail: message || 'The action failed.', life: 8000 });
  }
}
