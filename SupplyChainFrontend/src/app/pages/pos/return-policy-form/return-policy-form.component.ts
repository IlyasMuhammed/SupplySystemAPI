import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { CheckboxModule } from 'primeng/checkbox';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';

import { ReturnPolicyUpsert } from '../../../services/pos-returns-admin.service';

/**
 * A39 — the fields of a return policy (API-CONTRACT §1 ReturnPolicyUpsert), bound straight to `model`. Used by the store
 * page's Returns tab and the organization default page. Blank numbers mean: windows unlimited / same as returns, no limit
 * without a manager, store credit never expires.
 */
@Component({
  selector: 'app-pos-return-policy-form',
  standalone: true,
  imports: [CommonModule, FormsModule, CheckboxModule, InputNumberModule, InputTextModule],
  template: `
<div class="sf-fields" *ngIf="model" data-testid="policy-form">
  <div class="sf-fld rp-wide">
    <label [for]="id + 'Name'" class="sf-req">Name</label>
    <input pInputText [id]="id + 'Name'" [(ngModel)]="model.name" maxlength="100" [disabled]="disabled" data-testid="pol-name" />
  </div>

  <h3 class="rp-h rp-wide">Windows</h3>
  <div class="sf-fld">
    <label [for]="id + 'Ret'">Return window (days)</label>
    <p-inputnumber [inputId]="id + 'Ret'" [(ngModel)]="model.returnWindowDays" [min]="0" [useGrouping]="false" [disabled]="disabled"
                   placeholder="Unlimited" styleClass="w-full" data-testid="pol-return-days"></p-inputnumber>
    <small class="sf-mu">Counted from the sale. Blank = no limit; 0 = no returns.</small>
  </div>
  <div class="sf-fld">
    <label [for]="id + 'Ex'">Exchange window (days)</label>
    <p-inputnumber [inputId]="id + 'Ex'" [(ngModel)]="model.exchangeWindowDays" [min]="0" [useGrouping]="false" [disabled]="disabled"
                   placeholder="Same as returns" styleClass="w-full" data-testid="pol-exchange-days"></p-inputnumber>
    <small class="sf-mu">Blank = same as the return window; 0 = no exchanges.</small>
  </div>

  <h3 class="rp-h rp-wide">Receipt and other stores</h3>
  <div class="sf-fld rp-check">
    <p-checkbox [inputId]="id + 'Rcpt'" [(ngModel)]="model.requireReceipt" [binary]="true" [disabled]="disabled" data-testid="pol-receipt"></p-checkbox>
    <label [for]="id + 'Rcpt'">Receipt required</label>
  </div>
  <div class="sf-fld rp-check">
    <p-checkbox [inputId]="id + 'NoRcpt'" [(ngModel)]="model.allowNoReceiptExchange" [binary]="true" [disabled]="disabled"
                data-testid="pol-noreceipt-exchange"></p-checkbox>
    <label [for]="id + 'NoRcpt'">Exchanges allowed without a receipt</label>
  </div>
  <div class="sf-fld rp-check">
    <p-checkbox [inputId]="id + 'Xst'" [(ngModel)]="model.allowCrossStoreReturn" [binary]="true" [disabled]="disabled"
                data-testid="pol-cross-store"></p-checkbox>
    <label [for]="id + 'Xst'">Accept returns of sales made at other stores</label>
  </div>
  <div class="sf-fld rp-check">
    <p-checkbox [inputId]="id + 'XstMgr'" [(ngModel)]="model.crossStoreRequiresManager" [binary]="true"
                [disabled]="disabled || !model.allowCrossStoreReturn" data-testid="pol-cross-store-manager"></p-checkbox>
    <label [for]="id + 'XstMgr'">…only with a manager's authorisation</label>
  </div>

  <h3 class="rp-h rp-wide">Money</h3>
  <div class="sf-fld">
    <label [for]="id + 'Fee'">Restocking fee (%)</label>
    <p-inputnumber [inputId]="id + 'Fee'" [(ngModel)]="model.restockingFeePct" [min]="0" [max]="100" [maxFractionDigits]="2"
                   [disabled]="disabled" styleClass="w-full" data-testid="pol-fee"></p-inputnumber>
    <small class="sf-mu">Kept from the refund (0–100). A return reason can override it; 0 on the reason waives it.</small>
  </div>
  <div class="sf-fld">
    <label [for]="id + 'Max'">Largest refund without a manager</label>
    <p-inputnumber [inputId]="id + 'Max'" [(ngModel)]="model.maxReturnNoAuth" [min]="0" mode="decimal" [minFractionDigits]="2"
                   [maxFractionDigits]="2" [disabled]="disabled" placeholder="No limit" styleClass="w-full" data-testid="pol-max"></p-inputnumber>
    <small class="sf-mu">Above this a manager must authorise the return. Blank = no limit.</small>
  </div>

  <h3 class="rp-h rp-wide">Refund methods</h3>
  <div class="sf-fld rp-check rp-wide">
    <p-checkbox [inputId]="id + 'Orig'" [(ngModel)]="model.refundToOriginalMethod" [binary]="true" [disabled]="disabled"
                data-testid="pol-original"></p-checkbox>
    <label [for]="id + 'Orig'">Refund to the original payment method only</label>
  </div>
  <div class="sf-fld rp-check">
    <p-checkbox [inputId]="id + 'Cash'" [(ngModel)]="model.allowCashRefund" [binary]="true" [disabled]="disabled" data-testid="pol-cash"></p-checkbox>
    <label [for]="id + 'Cash'">Cash refunds allowed</label>
  </div>
  <div class="sf-fld rp-check">
    <p-checkbox [inputId]="id + 'Cn'" [(ngModel)]="model.allowCreditNoteRefund" [binary]="true" [disabled]="disabled"
                data-testid="pol-store-credit"></p-checkbox>
    <label [for]="id + 'Cn'">Refund as store credit allowed</label>
  </div>
  <div class="sf-fld">
    <label [for]="id + 'CnExp'">Store credit expires after (days)</label>
    <p-inputnumber [inputId]="id + 'CnExp'" [(ngModel)]="model.creditNoteExpiryDays" [min]="0" [useGrouping]="false"
                   [disabled]="disabled || !model.allowCreditNoteRefund" placeholder="Never" styleClass="w-full"
                   data-testid="pol-credit-expiry"></p-inputnumber>
    <small class="sf-mu">Blank = store credit never expires.</small>
  </div>
  <div class="sf-fld rp-check">
    <p-checkbox [inputId]="id + 'Act'" [(ngModel)]="model.isActive" [binary]="true" [disabled]="disabled" data-testid="pol-active"></p-checkbox>
    <label [for]="id + 'Act'">Active</label>
  </div>
</div>
  `,
  styles: [`
    .rp-wide { grid-column: 1 / -1; }
    .rp-check { flex-direction: row; align-items: center; gap: .5rem; }
    .rp-h { margin: .75rem 0 0; font-size: .95rem; color: var(--sms-text-muted); }
    :host ::ng-deep .w-full { width: 100%; }
  `]
})
export class ReturnPolicyFormComponent {
  @Input() model: ReturnPolicyUpsert | null = null;
  @Input() disabled = false;
  /** Prefix for input ids (two forms can be on one page). */
  @Input() id = 'pol';
}
