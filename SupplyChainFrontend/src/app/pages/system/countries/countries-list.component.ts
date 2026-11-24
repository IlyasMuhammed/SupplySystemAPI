import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { SelectModule } from 'primeng/select';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ConfirmationService, MessageService } from 'primeng/api';
import { CountriesAdminService, CountryAdmin, SaveCountryRequest } from '../../../services/countries-admin.service';
import { FLOW } from '../../../shared/flow';
import { MONTHS, onboardingError } from '../../onboarding/onboarding.shared';

export const COMMON_TIMEZONES = [
  'Asia/Karachi', 'Asia/Dubai', 'Asia/Riyadh', 'America/New_York', 'America/Chicago', 'America/Los_Angeles', 'Europe/London',
  'Europe/Berlin', 'Asia/Kolkata', 'Asia/Qatar', 'Asia/Kuwait', 'Asia/Bahrain', 'Asia/Muscat', 'Asia/Singapore', 'Australia/Sydney', 'UTC'
];

export interface CountryForm {
  code: string;
  name: string;
  currencyCode: string;
  defaultTimezone: string;
  fiscalYearStartMonth: number;
  dateFormat: string;
  phoneCode: string;
  sortOrder: number;
}

const blankForm = (): CountryForm => ({
  code: '', name: '', currencyCode: '', defaultTimezone: '', fiscalYearStartMonth: 1, dateFormat: 'DD/MM/YYYY', phoneCode: '', sortOrder: 0
});

/**
 * A51 P1 - System Administration > Countries (api/system/countries, platform super admin). The country master a new
 * organization starts from: currency, timezone, fiscal year start, date format, phone code. The code is fixed once saved.
 */
@Component({
  selector: 'app-countries-list',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, TableModule, SelectModule, DialogModule, InputTextModule, ToastModule, ConfirmDialogModule, ...FLOW],
  providers: [ConfirmationService],
  styleUrls: ['../../onboarding/onboarding.scss'],
  template: `
<p-toast></p-toast>
<p-confirmDialog key="countries"></p-confirmDialog>
<sf-page [crumbs]="['System Administration', 'Countries']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="Add country" icon="pi pi-plus" (onClick)="openNew()" data-testid="new"></p-button>
    <span class="sf-end"><p-button label="Refresh" icon="pi pi-refresh" [text]="true" [loading]="loading" (onClick)="load()"></p-button></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head">
      <div><h1>Countries <span class="sf-count">{{ rows.length }}</span></h1>
        <div class="sf-sub">The defaults a new organization gets from its country: currency, timezone, fiscal year start and date format.</div></div>
    </div>
    <div class="ob-note er" *ngIf="error" data-testid="error"><i class="pi pi-exclamation-triangle"></i>
      <div class="grow">{{ error }}</div><p-button label="Retry" size="small" [text]="true" (onClick)="load()"></p-button></div>
    <div class="sf-card p0 ob-scroll">
      <p-table [value]="rows" [loading]="loading" dataKey="id" [tableStyle]="{ 'min-width': '52rem' }">
        <ng-template pTemplate="header">
          <tr><th>Code</th><th>Name</th><th>Currency</th><th>Timezone</th><th>Fiscal start</th><th>Status</th><th style="width:15rem"></th></tr>
        </ng-template>
        <ng-template pTemplate="body" let-r>
          <tr [attr.data-testid]="'row-' + r.code">
            <td class="sf-mono">{{ r.code }}</td>
            <td>{{ r.name }}<div class="ob-mu ob-sm" *ngIf="r.phoneCode">{{ r.phoneCode }}</div></td>
            <td class="sf-mono">{{ r.currencyCode || '-' }}</td>
            <td>{{ r.defaultTimezone || '-' }}</td>
            <td>{{ monthName(r.fiscalYearStartMonth) }}</td>
            <td><span class="sf-pill" [ngClass]="r.isActive ? 'ok' : ''" [attr.data-testid]="'status-' + r.code">{{ r.isActive ? 'Active' : 'Inactive' }}</span></td>
            <td>
              <p-button icon="pi pi-pencil" [text]="true" size="small" ariaLabel="Edit" (onClick)="openEdit(r)" [attr.data-testid]="'edit-' + r.code"></p-button>
              <p-button [label]="r.isActive ? 'Deactivate' : 'Activate'" [text]="true" size="small" [loading]="busy === r.id"
                        (onClick)="toggleActive(r)" [attr.data-testid]="'toggle-' + r.code"></p-button>
              <p-button icon="pi pi-trash" [text]="true" size="small" severity="danger" ariaLabel="Delete" (onClick)="confirmDelete(r)"
                        [attr.data-testid]="'delete-' + r.code"></p-button>
            </td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage"><tr><td colspan="7" class="sf-empty" data-testid="empty">No countries yet.</td></tr></ng-template>
      </p-table>
    </div>
  </div>
</sf-page>

<p-dialog [(visible)]="dialogOpen" [modal]="true" [header]="editing ? 'Edit country' : 'Add country'" [style]="{ width: '40rem', maxWidth: '96vw' }">
  <div class="sf-fields" data-testid="dialog">
    <div class="sf-fld">
      <label class="sf-req" for="c-code">Code</label>
      <input pInputText id="c-code" [(ngModel)]="form.code" maxlength="5" [disabled]="!!editing" placeholder="PK" data-testid="f-code" />
      <span class="sf-mu">ISO country code (2-5 letters). Cannot be changed later.</span>
    </div>
    <div class="sf-fld span-2">
      <label class="sf-req" for="c-name">Name</label>
      <input pInputText id="c-name" [(ngModel)]="form.name" maxlength="200" data-testid="f-name" />
    </div>
    <div class="sf-fld">
      <label for="c-cur">Currency code</label>
      <input pInputText id="c-cur" [(ngModel)]="form.currencyCode" maxlength="5" placeholder="PKR" data-testid="f-currency" />
    </div>
    <div class="sf-fld span-2">
      <label for="c-tz">Default timezone</label>
      <input pInputText id="c-tz" [(ngModel)]="form.defaultTimezone" maxlength="50" list="countries-tz" placeholder="Asia/Karachi" data-testid="f-timezone" />
      <datalist id="countries-tz"><option *ngFor="let z of timezones" [value]="z"></option></datalist>
    </div>
    <div class="sf-fld">
      <label for="c-fy">Fiscal year starts</label>
      <p-select inputId="c-fy" [options]="months" optionLabel="label" optionValue="value" [(ngModel)]="form.fiscalYearStartMonth" appendTo="body" data-testid="f-fiscal"></p-select>
    </div>
    <div class="sf-fld">
      <label for="c-df">Date format</label>
      <input pInputText id="c-df" [(ngModel)]="form.dateFormat" maxlength="20" data-testid="f-dateformat" />
    </div>
    <div class="sf-fld">
      <label for="c-ph">Phone code</label>
      <input pInputText id="c-ph" [(ngModel)]="form.phoneCode" maxlength="5" placeholder="+92" data-testid="f-phone" />
    </div>
    <div class="sf-fld">
      <label for="c-so">Sort order</label>
      <input pInputText id="c-so" type="number" [(ngModel)]="form.sortOrder" data-testid="f-sort" />
    </div>
  </div>
  <div class="ob-note er" *ngIf="dialogError" data-testid="dialog-error"><i class="pi pi-times-circle"></i><div>{{ dialogError }}</div></div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" (onClick)="dialogOpen = false"></p-button>
    <p-button label="Save" icon="pi pi-save" [disabled]="!canSave" [loading]="saving" (onClick)="save()" data-testid="save"></p-button>
  </ng-template>
</p-dialog>`
})
export class CountriesListComponent implements OnInit {
  private readonly api = inject(CountriesAdminService);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService, { optional: true });

  readonly months = MONTHS;
  readonly timezones = COMMON_TIMEZONES;

  rows: CountryAdmin[] = [];
  loading = false;
  error = '';
  busy: string | null = null;

  dialogOpen = false;
  editing: CountryAdmin | null = null;
  form: CountryForm = blankForm();
  saving = false;
  dialogError = '';

  get canSave(): boolean {
    return !this.saving && this.form.name.trim().length > 0 && (!!this.editing || this.form.code.trim().length >= 2);
  }

  monthName(m: number): string { return MONTHS.find(x => x.value === m)?.label ?? String(m); }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.error = '';
    this.api.list().subscribe({
      next: r => { this.loading = false; this.rows = r?.result ?? []; },
      error: err => { this.loading = false; this.error = onboardingError(err, 'The countries could not be loaded.'); }
    });
  }

  openNew(): void {
    this.editing = null;
    this.form = blankForm();
    this.dialogError = '';
    this.dialogOpen = true;
  }

  openEdit(r: CountryAdmin): void {
    this.editing = r;
    this.form = {
      code: r.code, name: r.name, currencyCode: r.currencyCode ?? '', defaultTimezone: r.defaultTimezone ?? '',
      fiscalYearStartMonth: r.fiscalYearStartMonth, dateFormat: r.dateFormat, phoneCode: r.phoneCode ?? '', sortOrder: r.sortOrder
    };
    this.dialogError = '';
    this.dialogOpen = true;
  }

  save(): void {
    if (!this.canSave) return;
    const f = this.form;
    const body: SaveCountryRequest = {
      name: f.name.trim(),
      currencyCode: f.currencyCode.trim() || null,
      defaultTimezone: f.defaultTimezone.trim() || null,
      fiscalYearStartMonth: Number(f.fiscalYearStartMonth) || 1,
      dateFormat: f.dateFormat.trim() || 'DD/MM/YYYY',
      phoneCode: f.phoneCode.trim() || null,
      sortOrder: Number(f.sortOrder) || 0
    };
    this.saving = true;
    this.dialogError = '';
    const call = this.editing
      ? this.api.update(this.editing.id, body)
      : this.api.create({ ...body, code: f.code.trim() });
    call.subscribe({
      next: () => {
        this.saving = false;
        this.dialogOpen = false;
        this.messages?.add({ severity: 'success', summary: 'Saved', detail: 'Country saved.' });
        this.load();
      },
      error: err => { this.saving = false; this.dialogError = onboardingError(err, 'The country could not be saved.'); }
    });
  }

  toggleActive(r: CountryAdmin): void {
    this.busy = r.id;
    (r.isActive ? this.api.deactivate(r.id) : this.api.activate(r.id)).subscribe({
      next: res => {
        this.busy = null;
        const updated = res?.result;
        this.rows = this.rows.map(x => x.id === r.id ? { ...x, ...(updated ?? {}), isActive: updated?.isActive ?? !r.isActive } : x);
      },
      error: err => {
        this.busy = null;
        this.messages?.add({ severity: 'error', summary: 'Not changed', detail: onboardingError(err, 'The country could not be changed.') });
      }
    });
  }

  confirmDelete(r: CountryAdmin): void {
    this.confirm.confirm({
      key: 'countries',
      header: 'Delete ' + r.name + '?',
      icon: 'pi pi-exclamation-triangle',
      message: 'A country with cities cannot be deleted. Deactivate it instead if organizations already use it.',
      acceptLabel: 'Delete',
      rejectLabel: 'Keep',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.remove(r)
    });
  }

  remove(r: CountryAdmin): void {
    this.api.delete(r.id).subscribe({
      next: () => {
        this.rows = this.rows.filter(x => x.id !== r.id);
        this.messages?.add({ severity: 'success', summary: 'Deleted', detail: r.name + ' was deleted.' });
      },
      error: err => this.messages?.add({ severity: 'error', summary: 'Not deleted', detail: onboardingError(err, 'The country could not be deleted.') })
    });
  }
}
