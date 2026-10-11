import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { catchError, map, of } from 'rxjs';

import { PosReturnsAdminService, StoreCredit } from '../../../services/pos-returns-admin.service';
import { PosAdminService, newLocalId, posError } from '../../../services/pos-admin.service';
import { CustomerService } from '../../../services/customer.service';
import { AuthService } from '../../service/auth.service';
import { ymd } from '../../services/service-orders/service-order-shared';
import { storeOptions$ } from '../shift-list/shift-list.component';

/**
 * A39 — issue a standalone store credit (`POST api/pos/credit-notes`, POS_CREDIT_NOTE_ISSUE + FEATURE_POS_CREDIT_NOTES):
 * store, optional customer (blank = bearer), amount, optional expiry, note.
 */
@Component({
  selector: 'app-pos-store-credit-issue-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, CalendarModule, DialogModule, DropdownModule, InputNumberModule, TextareaModule],
  template: `
<p-dialog header="Issue store credit" [visible]="visible" (visibleChange)="close($event)" [modal]="true" [style]="{ width: '32rem' }"
          [breakpoints]="{ '640px': '96vw' }" data-testid="issue-dialog">
  <p class="dialog-lead">A store credit (CN-…) the customer spends at any store's till. Not a Finance credit note.</p>
  <div class="sf-pill er sci-err" *ngIf="error" role="alert" data-testid="issue-error">{{ error }}</div>
  <div class="sf-fields">
    <div class="sf-fld sci-wide">
      <label for="sciStore" class="sf-req">Store</label>
      <p-dropdown inputId="sciStore" [options]="storeOptions" [(ngModel)]="storeUuid" optionLabel="label" optionValue="value"
                  placeholder="Issuing store" appendTo="body" styleClass="w-full" data-testid="issue-store"></p-dropdown>
    </div>
    <div class="sf-fld sci-wide">
      <label for="sciCust">Customer</label>
      <p-dropdown inputId="sciCust" [options]="customerOptions" [(ngModel)]="customerUuid" optionLabel="label" optionValue="value"
                  [filter]="true" filterBy="label" [showClear]="true" placeholder="Bearer — anyone holding the number"
                  appendTo="body" styleClass="w-full" data-testid="issue-customer"></p-dropdown>
    </div>
    <div class="sf-fld">
      <label for="sciAmt" class="sf-req">Amount</label>
      <p-inputnumber inputId="sciAmt" [(ngModel)]="amount" mode="decimal" [minFractionDigits]="2" [maxFractionDigits]="2" [min]="0"
                     styleClass="w-full" data-testid="issue-amount"></p-inputnumber>
    </div>
    <div class="sf-fld">
      <label for="sciExp">Expires</label>
      <p-calendar inputId="sciExp" [(ngModel)]="expiresAt" [minDate]="today" [showIcon]="true" [showButtonBar]="true" dateFormat="dd M yy"
                  placeholder="Store policy's expiry" appendTo="body" styleClass="w-full" data-testid="issue-expiry"></p-calendar>
      <small class="sf-mu">Blank = the store's return policy decides (store credit expiry days; none = never).</small>
    </div>
    <div class="sf-fld sci-wide">
      <label for="sciNote">Note</label>
      <textarea pTextarea id="sciNote" rows="2" [(ngModel)]="note" maxlength="500" placeholder="Why it is issued" data-testid="issue-note"></textarea>
    </div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" severity="secondary" [text]="true" (onClick)="close(false)"></p-button>
    <p-button label="Issue" icon="pi pi-check" [loading]="busy" [disabled]="!canSave" (onClick)="save()" data-testid="issue-save"></p-button>
  </ng-template>
</p-dialog>
  `,
  styles: [`
    .sci-err { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .sci-wide { grid-column: 1 / -1; }
    :host ::ng-deep .w-full { width: 100%; }
  `]
})
export class StoreCreditIssueDialogComponent implements OnChanges {
  private readonly service = inject(PosReturnsAdminService);
  private readonly pos = inject(PosAdminService);
  private readonly customers = inject(CustomerService);
  private readonly auth = inject(AuthService);

  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();
  /** Preselected customer / store (e.g. from a filter). */
  @Input() customerPreset: string | null = null;
  @Input() storePreset: string | null = null;
  @Output() issued = new EventEmitter<StoreCredit>();

  readonly today = new Date();
  storeOptions: { label: string; value: string }[] = [];
  customerOptions: { label: string; value: string }[] = [];
  private optionsLoaded = false;

  storeUuid: string | null = null;
  customerUuid: string | null = null;
  amount: number | null = null;
  expiresAt: Date | null = null;
  note = '';
  busy = false;
  error: string | null = null;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['visible'] && this.visible) this.reset();
  }

  private reset(): void {
    this.storeUuid = this.storePreset;
    this.customerUuid = this.customerPreset;
    this.amount = null;
    this.expiresAt = null;
    this.note = '';
    this.error = null;
    if (this.optionsLoaded) return;
    this.optionsLoaded = true;
    storeOptions$(this.pos).subscribe(o => this.storeOptions = o);
    if (this.auth.hasPermission('CUSTOMER_VIEW')) {
      this.customers.getCustomers({ status: 'ACTIVE', page: 1, pageSize: 200 }).pipe(
        map(res => (res.result?.data ?? []).map(c => ({ label: `${c.name} (${c.code})`, value: c.uuid }))),
        catchError(() => of([] as { label: string; value: string }[]))
      ).subscribe(o => this.customerOptions = o);
    }
  }

  get canSave(): boolean { return !this.busy && !!this.storeUuid && (this.amount ?? 0) > 0; }

  save(): void {
    if (!this.canSave || !this.storeUuid) return;
    this.busy = true;
    this.error = null;
    this.service.issueStoreCredit({
      storeUuid: this.storeUuid, customerUuid: this.customerUuid || null, amount: this.amount!,
      expiresAt: this.expiresAt ? ymd(this.expiresAt) ?? null : null, note: this.note.trim() || null, localId: newLocalId()
    }).subscribe({
      next: res => {
        this.busy = false;
        if (res && res.success === false) { this.error = res.message || 'The store credit was not issued.'; return; }
        if (res.result) this.issued.emit(res.result);
        this.close(false);
      },
      error: err => { this.busy = false; this.error = posError(err, 'The store credit was not issued.'); }
    });
  }

  close(v: boolean): void {
    if (v) return;
    this.visible = false;
    this.visibleChange.emit(false);
  }
}
