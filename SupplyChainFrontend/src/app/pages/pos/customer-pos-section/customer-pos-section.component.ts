import { Component, Input, OnChanges, SimpleChanges, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { MessageService } from 'primeng/api';

import { CustomerPosSettings, DEFAULT_CUSTOMER_POS_SETTINGS, PosAdminService, posError } from '../../../services/pos-admin.service';

/**
 * A38 §17.7.6 — the customer's POS settings (API-CONTRACT §6): allow credit at the till, offline credit limit override, max
 * single credit order, ID required, notes. The walk-in customer can never buy on credit (D-12): the switch is disabled and
 * always sent false. Rendered by the customer page inside its own section card.
 */
@Component({
  selector: 'app-customer-pos-section',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, CheckboxModule, InputNumberModule, TextareaModule],
  template: `
<div *ngIf="loading" class="sf-empty"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
<div *ngIf="loadError" class="sf-empty" data-testid="cpos-load-error">{{ loadError }}</div>
<ng-container *ngIf="!loading && !loadError">
  <div class="sf-pill er cp-err" *ngIf="error" role="alert" data-testid="cpos-error">{{ error }}</div>
  <div class="sf-fields">
    <div class="sf-fld cp-check">
      <p-checkbox inputId="cpAllow" [(ngModel)]="settings.allowPosCredit" [binary]="true" [disabled]="!canEdit || isWalkIn"
                  data-testid="cpos-allow"></p-checkbox>
      <label for="cpAllow">Allow credit (on account) at the till</label>
    </div>
    <small class="sf-mu cp-wide" *ngIf="isWalkIn" data-testid="cpos-walkin-note">Walk-in customers cannot buy on credit.</small>
    <small class="sf-mu cp-wide" *ngIf="!isWalkIn && !creditSalesEnabled" data-testid="cpos-feature-note">
      POS credit sales are switched off for the organization, so this has no effect until they are turned on.</small>
    <div class="sf-fld">
      <label for="cpOffline">Offline credit limit</label>
      <p-inputnumber inputId="cpOffline" [(ngModel)]="settings.offlineCreditLimitOverride" mode="decimal" [minFractionDigits]="2"
                     [maxFractionDigits]="2" [min]="0" [disabled]="!canEdit || !settings.allowPosCredit" placeholder="Credit limit"
                     styleClass="w-full" data-testid="cpos-offline"></p-inputnumber>
      <small class="sf-mu">Used instead of the credit limit while a till is offline. Empty = the credit limit.</small>
    </div>
    <div class="sf-fld">
      <label for="cpMax">Largest single credit order</label>
      <p-inputnumber inputId="cpMax" [(ngModel)]="settings.maxSingleCreditOrder" mode="decimal" [minFractionDigits]="2"
                     [maxFractionDigits]="2" [min]="0" [disabled]="!canEdit || !settings.allowPosCredit" placeholder="No cap"
                     styleClass="w-full" data-testid="cpos-max"></p-inputnumber>
    </div>
    <div class="sf-fld cp-check">
      <p-checkbox inputId="cpId" [(ngModel)]="settings.requiresIdForCredit" [binary]="true" [disabled]="!canEdit || !settings.allowPosCredit"
                  data-testid="cpos-id"></p-checkbox>
      <label for="cpId">Ask for ID before a credit sale</label>
    </div>
    <div class="sf-fld cp-wide">
      <label for="cpNotes">Notes for the cashier</label>
      <textarea pTextarea id="cpNotes" rows="2" [(ngModel)]="settings.notes" maxlength="500" [disabled]="!canEdit" data-testid="cpos-notes"></textarea>
    </div>
  </div>
  <div class="sf-fbar cp-bar" *ngIf="canEdit">
    <span class="sf-mu sf-sm">Tills pick changes up on their next customer sync.</span>
    <span class="sf-end"><p-button label="Save POS settings" icon="pi pi-check" [loading]="saving" (onClick)="save()" data-testid="cpos-save"></p-button></span>
  </div>
</ng-container>
  `,
  styles: [`
    .cp-err { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .cp-wide { grid-column: 1 / -1; }
    .cp-check { flex-direction: row; align-items: center; gap: .5rem; }
    .cp-bar { margin-top: 1rem; }
    :host ::ng-deep .w-full { width: 100%; }
  `]
})
export class CustomerPosSectionComponent implements OnChanges {
  private readonly pos = inject(PosAdminService);
  private readonly messages = inject(MessageService, { optional: true });

  @Input() customerUuid = '';
  @Input() isWalkIn = false;
  @Input() canEdit = true;
  /** FEATURE_POS_CREDIT_SALES. */
  @Input() creditSalesEnabled = true;

  settings: CustomerPosSettings = { ...DEFAULT_CUSTOMER_POS_SETTINGS };
  loading = false;
  loadError: string | null = null;
  saving = false;
  error: string | null = null;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['customerUuid'] && this.customerUuid) this.load();
  }

  load(): void {
    this.loading = true;
    this.loadError = null;
    this.pos.getCustomerSettings(this.customerUuid).subscribe({
      next: res => {
        this.loading = false;
        this.settings = { ...DEFAULT_CUSTOMER_POS_SETTINGS, ...(res.result ?? {}) };
        if (this.isWalkIn) this.settings.allowPosCredit = false;
      },
      error: err => { this.loading = false; this.loadError = posError(err, 'The POS settings could not be loaded.'); }
    });
  }

  save(): void {
    if (this.saving) return;
    this.saving = true;
    this.error = null;
    const allow = !this.isWalkIn && !!this.settings.allowPosCredit;
    const req: CustomerPosSettings = {
      allowPosCredit: allow,
      offlineCreditLimitOverride: allow ? (this.settings.offlineCreditLimitOverride ?? null) : null,
      maxSingleCreditOrder: allow ? (this.settings.maxSingleCreditOrder ?? null) : null,
      requiresIdForCredit: allow && !!this.settings.requiresIdForCredit,
      notes: (this.settings.notes ?? '').trim() || null
    };
    this.pos.saveCustomerSettings(this.customerUuid, req).subscribe({
      next: res => {
        this.saving = false;
        if (res && res.success === false) { this.error = res.message || 'Not saved.'; return; }
        this.settings = { ...DEFAULT_CUSTOMER_POS_SETTINGS, ...(res.result ?? req) };
        this.messages?.add({ severity: 'success', summary: 'Saved', detail: 'POS settings saved.' });
      },
      error: err => { this.saving = false; this.error = posError(err, 'Not saved.'); }
    });
  }
}
