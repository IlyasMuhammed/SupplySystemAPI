import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { DialogModule } from 'primeng/dialog';
import { CheckboxModule } from 'primeng/checkbox';
import { InputTextModule } from 'primeng/inputtext';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { catchError, forkJoin, of } from 'rxjs';
import { CountriesAdminService, CountryAdmin } from '../../../services/countries-admin.service';
import { Industry, IndustriesService, SaveIndustryRequest } from '../../../services/industries.service';
import { FLOW } from '../../../shared/flow';
import { onboardingError } from '../../onboarding/onboarding.shared';

export interface IndustryForm {
  code: string;
  name: string;
  description: string;
  sortOrder: number;
  isActive: boolean;
  countryCodes: string[];
}

const blankForm = (): IndustryForm => ({ code: '', name: '', description: '', sortOrder: 0, isActive: true, countryCodes: [] });

/**
 * A51 P1 - System Administration > Industries (api/system/industries, platform super admin). The industries an
 * organization can be created in, and which countries offer each. The code is fixed once saved.
 */
@Component({
  selector: 'app-industries-list',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, TableModule, DialogModule, CheckboxModule, InputTextModule, ToastModule, ...FLOW],
  styleUrls: ['../../onboarding/onboarding.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['System Administration', 'Industries']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="Add industry" icon="pi pi-plus" (onClick)="openNew()" data-testid="new"></p-button>
    <span class="sf-end"><p-button label="Refresh" icon="pi pi-refresh" [text]="true" [loading]="loading" (onClick)="load()"></p-button></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head">
      <div><h1>Industries <span class="sf-count">{{ rows.length }}</span></h1>
        <div class="sf-sub">The industries a new organization can choose, and the countries that offer each one.</div></div>
    </div>
    <div class="ob-note er" *ngIf="error" data-testid="error"><i class="pi pi-exclamation-triangle"></i>
      <div class="grow">{{ error }}</div><p-button label="Retry" size="small" [text]="true" (onClick)="load()"></p-button></div>
    <div class="sf-card p0 ob-scroll">
      <p-table [value]="rows" [loading]="loading" dataKey="id" [tableStyle]="{ 'min-width': '48rem' }">
        <ng-template pTemplate="header">
          <tr><th>Code</th><th>Name</th><th>Countries</th><th>Status</th><th style="width:8rem"></th></tr>
        </ng-template>
        <ng-template pTemplate="body" let-r>
          <tr [attr.data-testid]="'row-' + r.code">
            <td class="sf-mono">{{ r.code }}</td>
            <td>{{ r.name }}<div class="ob-mu ob-sm" *ngIf="r.description">{{ r.description }}</div></td>
            <td [attr.data-testid]="'countries-' + r.code">
              <span class="sf-pill ok" *ngFor="let c of r.countryCodes" style="margin-right:.25rem">{{ c }}</span>
              <span class="ob-mu" *ngIf="!r.countryCodes.length">None</span>
            </td>
            <td><span class="sf-pill" [ngClass]="r.isActive ? 'ok' : ''" [attr.data-testid]="'status-' + r.code">{{ r.isActive ? 'Active' : 'Inactive' }}</span></td>
            <td><p-button icon="pi pi-pencil" [text]="true" size="small" ariaLabel="Edit" (onClick)="openEdit(r)" [attr.data-testid]="'edit-' + r.code"></p-button></td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage"><tr><td colspan="5" class="sf-empty" data-testid="empty">No industries yet.</td></tr></ng-template>
      </p-table>
    </div>
  </div>
</sf-page>

<p-dialog [(visible)]="dialogOpen" [modal]="true" [header]="editing ? 'Edit industry' : 'Add industry'" [style]="{ width: '40rem', maxWidth: '96vw' }">
  <div class="sf-fields" data-testid="dialog">
    <div class="sf-fld">
      <label class="sf-req" for="i-code">Code</label>
      <input pInputText id="i-code" [(ngModel)]="form.code" maxlength="30" [disabled]="!!editing" placeholder="TRADING" data-testid="f-code" />
      <span class="sf-mu">Letters, digits and underscore. Cannot be changed later.</span>
    </div>
    <div class="sf-fld span-2">
      <label class="sf-req" for="i-name">Name</label>
      <input pInputText id="i-name" [(ngModel)]="form.name" maxlength="100" data-testid="f-name" />
    </div>
    <div class="sf-fld span-3">
      <label for="i-desc">Description</label>
      <input pInputText id="i-desc" [(ngModel)]="form.description" maxlength="500" data-testid="f-description" />
    </div>
    <div class="sf-fld">
      <label for="i-so">Sort order</label>
      <input pInputText id="i-so" type="number" [(ngModel)]="form.sortOrder" data-testid="f-sort" />
    </div>
    <div class="sf-fld">
      <label>Status</label>
      <label class="ob-row"><p-checkbox [binary]="true" [(ngModel)]="form.isActive" inputId="i-active" data-testid="f-active"></p-checkbox> Active</label>
    </div>
    <div class="sf-fld span-3" data-testid="f-countries">
      <label>Offered in</label>
      <div class="ob-row">
        <label class="ob-row" *ngFor="let c of countries" style="margin-right:1rem">
          <p-checkbox [value]="c.code" [(ngModel)]="form.countryCodes" [inputId]="'ic-' + c.code" [attr.data-testid]="'country-' + c.code"></p-checkbox>
          {{ c.name }} ({{ c.code }})
        </label>
        <span class="ob-mu" *ngIf="!countries.length">No countries yet. Add them under Countries first.</span>
      </div>
    </div>
  </div>
  <div class="ob-note er" *ngIf="dialogError" data-testid="dialog-error"><i class="pi pi-times-circle"></i><div>{{ dialogError }}</div></div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" (onClick)="dialogOpen = false"></p-button>
    <p-button label="Save" icon="pi pi-save" [disabled]="!canSave" [loading]="saving" (onClick)="save()" data-testid="save"></p-button>
  </ng-template>
</p-dialog>`
})
export class IndustriesListComponent implements OnInit {
  private readonly api = inject(IndustriesService);
  private readonly countriesApi = inject(CountriesAdminService);
  private readonly messages = inject(MessageService, { optional: true });

  rows: Industry[] = [];
  countries: CountryAdmin[] = [];
  loading = false;
  error = '';

  dialogOpen = false;
  editing: Industry | null = null;
  form: IndustryForm = blankForm();
  saving = false;
  dialogError = '';

  get canSave(): boolean {
    return !this.saving && this.form.name.trim().length > 0 && (!!this.editing || /^[A-Za-z0-9_]{1,30}$/.test(this.form.code.trim()));
  }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.error = '';
    forkJoin({
      list: this.api.list(),
      countries: this.countriesApi.list().pipe(catchError(() => of(null)))
    }).subscribe({
      next: r => { this.loading = false; this.rows = r.list?.result ?? []; this.countries = r.countries?.result ?? []; },
      error: err => { this.loading = false; this.error = onboardingError(err, 'The industries could not be loaded.'); }
    });
  }

  openNew(): void {
    this.editing = null;
    this.form = blankForm();
    this.dialogError = '';
    this.dialogOpen = true;
  }

  openEdit(r: Industry): void {
    this.editing = r;
    this.form = { code: r.code, name: r.name, description: r.description ?? '', sortOrder: r.sortOrder, isActive: r.isActive, countryCodes: [...r.countryCodes] };
    this.dialogError = '';
    this.dialogOpen = true;
  }

  save(): void {
    if (!this.canSave) return;
    const f = this.form;
    const body: SaveIndustryRequest = {
      name: f.name.trim(),
      description: f.description.trim() || null,
      sortOrder: Number(f.sortOrder) || 0,
      isActive: f.isActive,
      countryCodes: [...f.countryCodes]
    };
    this.saving = true;
    this.dialogError = '';
    const call = this.editing ? this.api.update(this.editing.id, body) : this.api.create({ ...body, code: f.code.trim() });
    call.subscribe({
      next: () => {
        this.saving = false;
        this.dialogOpen = false;
        this.messages?.add({ severity: 'success', summary: 'Saved', detail: 'Industry saved.' });
        this.load();
      },
      error: err => { this.saving = false; this.dialogError = onboardingError(err, 'The industry could not be saved.'); }
    });
  }
}
