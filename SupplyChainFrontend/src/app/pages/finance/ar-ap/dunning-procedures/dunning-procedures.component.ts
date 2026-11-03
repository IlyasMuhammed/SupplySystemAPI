import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { DialogModule } from 'primeng/dialog';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import {
  AR_AP_PERMS, ArApService, DUNNING_ACTION_TYPES, DUNNING_RUNS_ROUTE, DunningProcedure, DunningProcedureSave
} from '../../../../services/ar-ap.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { DUNNING_PLACEHOLDERS, MAX_DUNNING_LEVELS, arApError, blankLevel, money, procedureProblem, renumber } from '../ar-ap.shared';

export function blankProcedure(): DunningProcedureSave {
  return { code: '', name: '', description: '', isDefault: false, isActive: true, graceDays: 0, minimumAmount: 0, blockSalesAtLevel: null, levels: [blankLevel(1)] };
}

/**
 * A48 §8.6, tasks 577–579 — `/finance/ar/dunning/procedures`: each procedure as a card (grace days, minimum amount,
 * block-sales level) with its levels table (days after, action, charge, subject) expanding; create / edit in a dialog
 * with the level rows (1…4, never skipping — BR-DN-004), checked here as the server does. Writes need
 * FINANCE_AR_AP_ADMIN (contract §3); a PUT replaces the levels as a set.
 */
@Component({
  selector: 'app-dunning-procedures',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, CheckboxModule, DialogModule, InputNumberModule, InputTextModule, SelectModule,
    TextareaModule, ToastModule, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Finance', 'Receivables', 'Dunning procedures']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canEdit" label="New procedure" icon="pi pi-plus" (onClick)="openNew()" data-testid="new"></p-button>
    <a class="p-button p-button-text" [routerLink]="runsRoute" data-testid="go-runs"><i class="pi pi-bell"></i>&nbsp;Dunning runs</a>
    <span class="sf-end"><p-button label="Refresh" icon="pi pi-refresh" [text]="true" (onClick)="load()"></p-button></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Dunning procedures <span class="sf-count">{{ procedures.length }}</span></h1>
      <div class="sf-sub">How overdue customers are reminded: the levels, when each one is sent and what it does</div></div></div>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>
    <div class="sf-card sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
    <div class="sf-card sf-empty" *ngIf="!loading && !loadError && !procedures.length" data-testid="empty">No dunning procedure yet.</div>

    <section class="sf-card p0" *ngFor="let p of procedures" [attr.data-testid]="'proc-' + p.code">
      <div class="sf-ch sf-pad">
        <button type="button" class="gl-toggle" (click)="toggle(p)" [attr.aria-label]="isOpen(p) ? 'Collapse' : 'Expand'" [attr.data-testid]="'toggle-' + p.code">
          <i class="pi" [ngClass]="isOpen(p) ? 'pi-chevron-down' : 'pi-chevron-right'"></i></button>
        <h2><span class="sf-mono">{{ p.code }}</span> — {{ p.name }}<span class="sf-mu" *ngIf="p.isDefault"> (Default)</span></h2>
        <span class="sf-pill" [ngClass]="p.isActive ? 'ok' : ''">{{ p.isActive ? 'Active' : 'Inactive' }}</span>
        <span class="sf-end"><p-button *ngIf="canEdit" label="Edit" icon="pi pi-pencil" [text]="true" size="small" (onClick)="openEdit(p)" [attr.data-testid]="'edit-' + p.code"></p-button></span>
      </div>
      <div class="sf-pad sf-mu sf-sm" style="padding-top:0">Grace days: {{ p.graceDays }} · Min amount: {{ amount(p.minimumAmount) }} ·
        Block sales: {{ p.blockSalesAtLevel ? 'Level ' + p.blockSalesAtLevel : 'Never' }} · {{ p.levels.length }} level{{ p.levels.length === 1 ? '' : 's' }}</div>
      <div class="gl-scroll" *ngIf="isOpen(p)">
        <table class="aa-table" style="min-width:40rem" [attr.data-testid]="'levels-' + p.code">
          <thead><tr><th>Level</th><th class="gl-num">Days after</th><th>Action</th><th class="gl-num">Charge</th><th>Subject</th><th>Attachments</th></tr></thead>
          <tbody><tr *ngFor="let l of p.levels">
            <td>{{ l.levelNumber }}</td><td class="gl-num">{{ l.daysAfterPrevious }}</td><td>{{ l.actionType }}</td>
            <td class="gl-num">{{ l.chargeAmount ? amount(l.chargeAmount) : '—' }}</td><td>{{ l.subjectTemplate }}</td>
            <td class="sf-sm">{{ attachments(l.includeInvoiceList, l.includeStatement) }}</td></tr></tbody>
        </table>
      </div>
    </section>
  </div>
</sf-page>

<!-- Task 579 — create / edit -->
<p-dialog [(visible)]="formOpen" [modal]="true" [header]="editingUuid ? 'Edit dunning procedure' : 'New dunning procedure'" [style]="{ width: '56rem', maxWidth: '98vw' }"
          styleClass="sdlg" [maximizable]="true">
  <div class="aa-dlg-body" *ngIf="draft as d">
    <div class="sf-fields">
      <div class="sf-fld"><label for="dp-code" class="sf-req">Code</label><input pInputText id="dp-code" [(ngModel)]="d.code" maxlength="20" data-testid="f-code" /></div>
      <div class="sf-fld"><label for="dp-name" class="sf-req">Name</label><input pInputText id="dp-name" [(ngModel)]="d.name" maxlength="100" data-testid="f-name" /></div>
      <div class="sf-fld"><label for="dp-grace">Grace days</label><p-inputNumber inputId="dp-grace" [(ngModel)]="d.graceDays" [min]="0" [useGrouping]="false" data-testid="f-grace"></p-inputNumber></div>
      <div class="sf-fld"><label for="dp-min">Minimum overdue</label>
        <p-inputNumber inputId="dp-min" [(ngModel)]="d.minimumAmount" mode="decimal" [minFractionDigits]="2" [maxFractionDigits]="2" [min]="0" data-testid="f-min"></p-inputNumber></div>
      <div class="sf-fld"><label for="dp-block">Block sales at</label>
        <p-select inputId="dp-block" [options]="blockOptions" [(ngModel)]="d.blockSalesAtLevel" optionLabel="label" optionValue="value" appendTo="body" data-testid="f-block"></p-select></div>
      <div class="sf-fld"><label>&nbsp;</label><div class="aa-actions">
        <label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="d.isActive"></p-checkbox> Active</label>
        <label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="d.isDefault" data-testid="f-default"></p-checkbox> Default</label></div></div>
    </div>
    <div class="sf-fld"><label for="dp-desc">Description</label><input pInputText id="dp-desc" [(ngModel)]="d.description" maxlength="500" /></div>

    <div class="sf-ch"><h2>Levels</h2>
      <span class="sf-end"><p-button label="Add level" icon="pi pi-plus" [text]="true" size="small" [disabled]="d.levels.length >= maxLevels" (onClick)="addLevel()" data-testid="add-level"></p-button></span></div>
    <div class="sf-card" *ngFor="let l of d.levels; let i = index" [attr.data-testid]="'f-level-' + l.levelNumber">
      <div class="sf-ch"><h2>Level {{ l.levelNumber }}</h2>
        <span class="sf-end"><p-button icon="pi pi-trash" [text]="true" size="small" severity="danger" [disabled]="d.levels.length === 1" (onClick)="removeLevel(i)"
                                       ariaLabel="Remove level" [attr.data-testid]="'remove-level-' + l.levelNumber"></p-button></span></div>
      <div class="sf-fields">
        <div class="sf-fld"><label [for]="'dl-days' + i">{{ l.levelNumber === 1 ? 'Days after due date' : 'Days after previous level' }}</label>
          <p-inputNumber [inputId]="'dl-days' + i" [(ngModel)]="l.daysAfterPrevious" [min]="0" [useGrouping]="false"></p-inputNumber></div>
        <div class="sf-fld"><label [for]="'dl-act' + i">Action</label>
          <p-select [inputId]="'dl-act' + i" [options]="actionTypes" [(ngModel)]="l.actionType" appendTo="body"></p-select></div>
        <div class="sf-fld"><label [for]="'dl-chg' + i">Charge (base currency)</label>
          <p-inputNumber [inputId]="'dl-chg' + i" [(ngModel)]="l.chargeAmount" mode="decimal" [minFractionDigits]="2" [maxFractionDigits]="2" [min]="0" placeholder="None"></p-inputNumber></div>
        <div class="sf-fld"><label>&nbsp;</label><div class="aa-actions">
          <label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="l.includeInvoiceList"></p-checkbox> Invoice list</label>
          <label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="l.includeStatement"></p-checkbox> Attach statement</label></div></div>
      </div>
      <div class="sf-fld"><label [for]="'dl-subj' + i" class="sf-req">Subject</label><input pInputText [id]="'dl-subj' + i" [(ngModel)]="l.subjectTemplate" maxlength="200" /></div>
      <div class="sf-fld"><label [for]="'dl-body' + i" class="sf-req">Message</label><textarea pTextarea [id]="'dl-body' + i" rows="4" [(ngModel)]="l.bodyTemplate"></textarea></div>
      <div class="sf-fld"><label [for]="'dl-note' + i">Escalation note (internal)</label><input pInputText [id]="'dl-note' + i" [(ngModel)]="l.escalationNote" maxlength="500" /></div>
    </div>
    <div class="gl-help"><h3>Placeholders</h3><p>{{ placeholders }}</p></div>
    <div class="gl-note er" *ngIf="formError" data-testid="form-error"><i class="pi pi-exclamation-triangle"></i><div>{{ formError }}</div></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="formOpen = false"></p-button>
    <p-button label="Save" icon="pi pi-check" [disabled]="saving" [loading]="saving" (onClick)="save()" data-testid="save"></p-button>
  </ng-template>
</p-dialog>`
})
export class DunningProceduresComponent implements OnInit {
  private readonly api = inject(ArApService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);

  readonly runsRoute = DUNNING_RUNS_ROUTE;
  readonly actionTypes = DUNNING_ACTION_TYPES;
  readonly maxLevels = MAX_DUNNING_LEVELS;
  readonly placeholders = DUNNING_PLACEHOLDERS.join('  ');

  procedures: DunningProcedure[] = [];
  loading = false;
  loadError = '';
  private open = new Set<string>();

  formOpen = false;
  draft: DunningProcedureSave | null = null;
  editingUuid: string | null = null;
  formError = '';
  saving = false;

  /** Contract §3 — procedure writes need FINANCE_AR_AP_ADMIN. */
  get canEdit(): boolean { return this.auth.hasPermission(AR_AP_PERMS.ADMIN); }

  get blockOptions(): { label: string; value: number | null }[] {
    return [{ label: 'Never', value: null }, ...(this.draft?.levels ?? []).map(l => ({ label: `Level ${l.levelNumber}`, value: l.levelNumber }))];
  }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.loadError = '';
    this.api.getProcedures().subscribe({
      next: list => {
        this.loading = false;
        this.procedures = [...list].sort((a, b) => Number(b.isDefault) - Number(a.isDefault) || a.code.localeCompare(b.code));
        if (!this.open.size && this.procedures.length) this.open.add(this.procedures[0].uuid);
      },
      error: err => { this.loading = false; this.procedures = []; this.loadError = arApError(err, 'The dunning procedures could not be loaded.'); }
    });
  }

  isOpen(p: DunningProcedure): boolean { return this.open.has(p.uuid); }
  toggle(p: DunningProcedure): void { if (this.open.has(p.uuid)) this.open.delete(p.uuid); else this.open.add(p.uuid); }

  openNew(): void { this.editingUuid = null; this.draft = blankProcedure(); this.formError = ''; this.formOpen = true; }

  openEdit(p: DunningProcedure): void {
    this.editingUuid = p.uuid;
    this.draft = {
      code: p.code, name: p.name, description: p.description ?? '', isDefault: p.isDefault, isActive: p.isActive, graceDays: p.graceDays,
      minimumAmount: p.minimumAmount, blockSalesAtLevel: p.blockSalesAtLevel ?? null, levels: p.levels.map(l => ({ ...l }))
    };
    this.formError = '';
    this.formOpen = true;
  }

  addLevel(): void {
    if (!this.draft || this.draft.levels.length >= MAX_DUNNING_LEVELS) return;
    this.draft.levels = [...this.draft.levels, blankLevel(this.draft.levels.length + 1)];
  }

  removeLevel(i: number): void {
    if (!this.draft || this.draft.levels.length === 1) return;
    this.draft.levels = renumber(this.draft.levels.filter((_, j) => j !== i));
    if (this.draft.blockSalesAtLevel && this.draft.blockSalesAtLevel > this.draft.levels.length) this.draft.blockSalesAtLevel = null;
  }

  save(): void {
    const d = this.draft;
    if (!d) return;
    const body: DunningProcedureSave = {
      ...d, code: d.code.trim().toUpperCase(), name: d.name.trim(), description: (d.description ?? '').trim() || null,
      levels: d.levels.map(l => ({ ...l, chargeAmount: l.chargeAmount || null, escalationNote: (l.escalationNote ?? '').trim() || null }))
    };
    this.formError = procedureProblem(body) ?? '';
    if (this.formError) return;
    this.saving = true;
    const call = this.editingUuid ? this.api.updateProcedure(this.editingUuid, body) : this.api.createProcedure(body);
    call.subscribe({
      next: p => { this.saving = false; this.formOpen = false; this.toast(`${p?.code ?? body.code} is saved.`); if (p?.uuid) this.open.add(p.uuid); this.load(); },
      error: err => { this.saving = false; this.formError = arApError(err, 'The procedure could not be saved.'); }
    });
  }

  attachments(list: boolean, statement: boolean): string {
    return [list ? 'invoice list' : '', statement ? 'statement PDF' : ''].filter(Boolean).join(' + ') || '—';
  }

  amount(n: number | null | undefined): string { return money(n); }

  private toast(detail: string): void { this.messages.add({ severity: 'success', summary: 'Done', detail, life: 3000 }); }
}
