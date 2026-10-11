import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';

import { PosAdminService, PosStore, PosStoreUpsert, posError, storeUpsertFrom } from '../../../services/pos-admin.service';
import { InventoryService } from '../../../services/inventory.service';
import { FinanceSetupService } from '../../../services/finance-setup.service';

interface Option { label: string; value: string; }

/** Defaults for a new store — the server's own defaults (API-CONTRACT "Details fixed by the backend"). */
export const NEW_STORE_DEFAULTS = { offlineCreditTolerancePct: 0, autoCloseShiftHours: 12, varianceThreshold: 0 };

export function browserTimezone(): string {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch { return 'UTC'; }
}

/**
 * A38 §17.7.1 — "New store" and the store's General edit (API-CONTRACT §2). Warehouse picker = the inventory warehouses
 * list; tax code picker = Finance sales tax codes (usage SALES or BOTH). The server's 400 text is shown verbatim.
 */
@Component({
  selector: 'app-pos-store-form-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, CheckboxModule, DialogModule, DropdownModule, InputNumberModule, InputTextModule],
  template: `
<p-dialog [visible]="visible" (visibleChange)="close($event)" [modal]="true" [draggable]="false" [style]="{ width: '44rem' }"
          [breakpoints]="{ '760px': '96vw' }" [header]="store ? 'Edit store' : 'New store'" data-testid="store-dialog">
  <ng-container *ngIf="visible">
    <div class="sf-pill er sfd-error" *ngIf="error" role="alert" data-testid="store-form-error">{{ error }}</div>
    <div class="sf-fields">
      <div class="sf-fld">
        <label for="stCode" class="sf-req">Code</label>
        <input pInputText id="stCode" [(ngModel)]="model.code" maxlength="20" data-testid="store-code" />
        <small class="sf-mu">Letters, digits, - and _; used in order and shift numbers.</small>
      </div>
      <div class="sf-fld">
        <label for="stName" class="sf-req">Name</label>
        <input pInputText id="stName" [(ngModel)]="model.name" maxlength="200" data-testid="store-name" />
      </div>
      <div class="sf-fld">
        <label for="stWh" class="sf-req">Warehouse</label>
        <p-dropdown inputId="stWh" [options]="warehouseOptions" [(ngModel)]="model.warehouseUuid" optionLabel="label" optionValue="value"
                    [filter]="true" filterBy="label" placeholder="Pick the store's stock location" appendTo="body" styleClass="w-full"
                    data-testid="store-warehouse"></p-dropdown>
        <small class="sf-mu">Each warehouse backs one active store only, and cannot be changed once the store has shifts.</small>
      </div>
      <div class="sf-fld">
        <label for="stTax">Default sales tax code</label>
        <p-dropdown inputId="stTax" [options]="taxOptions" [(ngModel)]="model.defaultTaxCodeUuid" optionLabel="label" optionValue="value"
                    [showClear]="true" placeholder="Finance's default sales tax code" appendTo="body" styleClass="w-full" data-testid="store-tax"></p-dropdown>
      </div>
      <div class="sf-fld">
        <label for="stTz" class="sf-req">Timezone</label>
        <input pInputText id="stTz" [(ngModel)]="model.timezone" maxlength="64" data-testid="store-timezone" />
      </div>
      <div class="sf-fld sfd-check">
        <p-checkbox inputId="stIncl" [(ngModel)]="model.taxInclusivePricing" [binary]="true"></p-checkbox>
        <label for="stIncl">Shelf prices include tax</label>
      </div>
      <div class="sf-fld sfd-wide">
        <label for="stAddr1">Address</label>
        <input pInputText id="stAddr1" [(ngModel)]="model.addressLine1" placeholder="Line 1" maxlength="200" />
        <input pInputText [(ngModel)]="model.addressLine2" placeholder="Line 2" maxlength="200" class="sfd-mt" />
      </div>
      <div class="sf-fld">
        <label for="stCity">City</label>
        <input pInputText id="stCity" [(ngModel)]="model.city" maxlength="100" data-testid="store-city" />
      </div>
      <div class="sf-fld">
        <label for="stState">Province / state</label>
        <input pInputText id="stState" [(ngModel)]="model.stateProvince" maxlength="100" />
      </div>
      <div class="sf-fld">
        <label for="stPostal">Postal code</label>
        <input pInputText id="stPostal" [(ngModel)]="model.postalCode" maxlength="20" />
      </div>
      <div class="sf-fld">
        <label for="stCountry">Country</label>
        <input pInputText id="stCountry" [(ngModel)]="model.country" maxlength="100" />
      </div>
      <div class="sf-fld">
        <label for="stPhone">Phone</label>
        <input pInputText id="stPhone" [(ngModel)]="model.phone" maxlength="50" />
      </div>
      <div class="sf-fld">
        <label for="stEmail">Email</label>
        <input pInputText id="stEmail" type="email" [(ngModel)]="model.email" maxlength="200" />
      </div>
    </div>
  </ng-container>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="close(false)"></p-button>
    <p-button [label]="store ? 'Save' : 'Create store'" icon="pi pi-check" [loading]="saving" [disabled]="!canSave"
              (onClick)="save()" data-testid="store-save"></p-button>
  </ng-template>
</p-dialog>
  `,
  styles: [`
    .sfd-error { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .sfd-wide { grid-column: 1 / -1; }
    .sfd-mt { margin-top: .4rem; }
    .sfd-check { flex-direction: row; align-items: center; gap: .5rem; align-self: end; }
    :host ::ng-deep .w-full { width: 100%; }
  `]
})
export class StoreFormDialogComponent implements OnChanges {
  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();
  /** null = new store. */
  @Input() store: PosStore | null = null;
  /** The saved store. */
  @Output() saved = new EventEmitter<PosStore>();

  model: PosStoreUpsert = this.blank();
  warehouseOptions: Option[] = [];
  taxOptions: Option[] = [];
  saving = false;
  error: string | null = null;
  private lookupsLoaded = false;

  constructor(private pos: PosAdminService, private inventory: InventoryService, private finance: FinanceSetupService) {}

  get canSave(): boolean {
    return !this.saving && !!this.model.code?.trim() && !!this.model.name?.trim() && !!this.model.warehouseUuid && !!this.model.timezone?.trim();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if ((changes['visible'] || changes['store']) && this.visible) this.reset();
  }

  private blank(): PosStoreUpsert {
    return {
      code: '', name: '', warehouseUuid: '', timezone: browserTimezone(), taxInclusivePricing: false, defaultTaxCodeUuid: null,
      addressLine1: null, addressLine2: null, city: null, stateProvince: null, postalCode: null, country: null, phone: null, email: null,
      receiptHeader: null, receiptFooter: null, isActive: true, ...NEW_STORE_DEFAULTS
    };
  }

  reset(): void {
    this.error = null;
    this.saving = false;
    this.model = this.store ? storeUpsertFrom(this.store) : this.blank();
    this.loadLookups();
  }

  private loadLookups(): void {
    if (this.lookupsLoaded) return;
    this.lookupsLoaded = true;
    this.inventory.getWarehouses().subscribe({
      next: res => {
        this.warehouseOptions = (res.result ?? [])
          .filter(w => w.isActive || w.uuid === this.model.warehouseUuid)
          .map(w => ({ label: `${w.name} (${w.code})`, value: w.uuid }));
      },
      error: () => { this.warehouseOptions = []; }
    });
    this.finance.getTaxCodes('SALES').subscribe({
      next: res => {
        this.taxOptions = (res.result ?? [])
          .filter(t => t.usage === 'SALES' || t.usage === 'BOTH')
          .map(t => ({ label: `${t.code} — ${t.name} (${t.ratePercent}%)`, value: t.uuid }));
      },
      error: () => { this.taxOptions = []; }
    });
  }

  close(visible: boolean): void {
    if (visible) return;
    this.visible = false;
    this.visibleChange.emit(false);
  }

  save(): void {
    if (!this.canSave) return;
    this.saving = true;
    this.error = null;
    const trim = (v: string | null | undefined) => (v ?? '').trim() || null;
    const req: PosStoreUpsert = {
      ...this.model,
      code: this.model.code.trim().toUpperCase(), name: this.model.name.trim(), timezone: this.model.timezone.trim(),
      addressLine1: trim(this.model.addressLine1), addressLine2: trim(this.model.addressLine2), city: trim(this.model.city),
      stateProvince: trim(this.model.stateProvince), postalCode: trim(this.model.postalCode), country: trim(this.model.country),
      phone: trim(this.model.phone), email: trim(this.model.email)
    };
    const call = this.store ? this.pos.updateStore(this.store.uuid, req) : this.pos.createStore(req);
    call.subscribe({
      next: res => {
        this.saving = false;
        if (res && res.success === false) { this.error = res.message || 'The store was not saved.'; return; }
        this.saved.emit(res.result);
        this.close(false);
      },
      error: err => {
        this.saving = false;
        this.error = posError(err, err?.status === 409 ? 'Someone else changed this store. Reload and try again.' : 'The store was not saved.');
      }
    });
  }
}
