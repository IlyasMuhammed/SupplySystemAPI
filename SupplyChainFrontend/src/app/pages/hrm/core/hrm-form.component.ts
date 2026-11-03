import { Component, Input, OnDestroy, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TextareaModule } from 'primeng/textarea';
import { Subject, Subscription, debounceTime, map, of, switchMap } from 'rxjs';

import { HrmApi } from './hrm.api';
import { HrmLookups, LookupKey } from './hrm-lookups.service';
import { Opt, WEEK_DAYS, hrmError, uuid } from './hrm.shared';

export type FieldType = 'text' | 'textarea' | 'number' | 'date' | 'month' | 'time' | 'datetime' | 'select' | 'lookup' | 'bool' | 'weekdays' | 'rows' | 'file' | 'info';

export interface Field {
  key: string;
  label: string;
  type?: FieldType;
  required?: boolean;
  options?: Opt[];
  lookup?: LookupKey;
  max?: number;
  min?: number;
  maxLength?: number;
  step?: number;
  hint?: string;
  placeholder?: string;
  /** A whole row of the grid. */
  full?: boolean;
  /** Value on a new form. */
  def?: unknown;
  /** Locked when editing an existing row. */
  lockOnEdit?: boolean;
  visible?: (v: Record<string, any>) => boolean;
  /** Extra check; returns the problem. */
  check?: (value: any, v: Record<string, any>) => string | null;
  /** rows: the columns of one row. */
  rows?: Field[];
  /** file: attachment interface code; documentId comes from `docId` or is generated. */
  interfaceCode?: string;
  docId?: (v: Record<string, any>) => string | null | undefined;
}

function empty(v: unknown): boolean { return v === null || v === undefined || v === '' || (Array.isArray(v) && !v.length); }

/** The first thing wrong with these values, or null. Visible fields only. */
export function formProblem(fields: Field[], v: Record<string, any>): string | null {
  for (const f of fields) {
    if (f.type === 'info' || (f.visible && !f.visible(v))) continue;
    const value = v[f.key];
    if (f.required && empty(value) && f.type !== 'bool') return `${f.label} is required.`;
    if (f.type === 'number' && !empty(value)) {
      if (!Number.isFinite(Number(value))) return `${f.label} must be a number.`;
      if (f.min !== undefined && Number(value) < f.min) return `${f.label} cannot be below ${f.min}.`;
      if (f.max !== undefined && Number(value) > f.max) return `${f.label} cannot be above ${f.max}.`;
    }
    if (f.type === 'rows') {
      for (const [i, row] of ((value ?? []) as any[]).entries()) {
        const p = formProblem(f.rows ?? [], row);
        if (p) return `${f.label} row ${i + 1}: ${p}`;
      }
    }
    const custom = f.check?.(value, v);
    if (custom) return custom;
  }
  return null;
}

/** New-form values from the fields' defaults. */
export function formDefaults(fields: Field[]): Record<string, any> {
  const v: Record<string, any> = {};
  for (const f of fields) {
    if (f.type === 'info') continue;
    v[f.key] = f.def !== undefined ? (typeof f.def === 'object' && f.def !== null ? structuredClone(f.def) : f.def)
      : f.type === 'rows' ? [] : f.type === 'bool' ? false : f.type === 'weekdays' ? 0 : null;
  }
  return v;
}

/** Everything the fields need from the server. */
export function lookupsOf(fields: Field[]): LookupKey[] {
  return fields.flatMap(f => [...(f.lookup ? [f.lookup] : []), ...(f.rows ? lookupsOf(f.rows) : [])]);
}

/** Text values trimmed, blank optional values null: what goes to the API. */
export function cleanBody(fields: Field[], v: Record<string, any>): Record<string, any> {
  const out: Record<string, any> = { ...v };
  for (const f of fields) {
    if (f.type === 'info') continue;
    if (f.visible && !f.visible(v)) { out[f.key] = null; continue; }
    const val = v[f.key];
    if (typeof val === 'string') out[f.key] = val.trim() === '' ? null : val.trim();
    if (f.type === 'number') out[f.key] = empty(val) ? null : Number(val);
    if (f.type === 'rows') out[f.key] = ((val ?? []) as any[]).map(r => cleanBody(f.rows ?? [], r));
  }
  return out;
}

const SAVED_FORMS = new WeakSet<object>();

/** Call on a successful save, before the form closes: its uploads now belong to a row. Without this, closing the form removes them. */
export function markFilesSaved(values: object): void { SAVED_FORMS.add(values); }

/**
 * Files uploaded by this form and not saved yet (contract D-25). Only those: a file a row already held is never in here, so it is
 * never asked to be removed. A removal the server refuses (ATTACHMENT_IN_USE) is the right outcome, so it stays silent.
 */
export class UploadTracker {
  private readonly ids = new Set<string>();
  constructor(private readonly api: HrmApi) {}
  add(id: string): void { this.ids.add(id); }
  drop(id: string | null | undefined): void {
    if (id && this.ids.delete(id)) this.api.removeUnusedAttachment(id).subscribe({ error: () => undefined });
  }
  dropAll(): void { [...this.ids].forEach(id => this.drop(id)); }
}

/** One cell of a `rows` field. */
@Component({
  selector: 'hrm-cell',
  standalone: true,
  imports: [CommonModule, FormsModule, CheckboxModule, InputTextModule, SelectModule],
  template: `
<ng-container [ngSwitch]="field.type || 'text'">
  <input *ngSwitchCase="'number'" pInputText type="number" [(ngModel)]="row[field.key]" [attr.step]="field.step ?? 'any'" [attr.aria-label]="field.label" style="width:100%" />
  <input *ngSwitchCase="'date'" pInputText type="date" [(ngModel)]="row[field.key]" [attr.aria-label]="field.label" style="width:100%" />
  <p-checkbox *ngSwitchCase="'bool'" [binary]="true" [(ngModel)]="row[field.key]" [ariaLabel]="field.label"></p-checkbox>
  <span *ngSwitchCase="'file'"><input type="file" [attr.aria-label]="field.label" (change)="pick($event)" style="max-width:11rem" /><i class="pi pi-check" *ngIf="row[field.key]"></i><span class="sf-sm" *ngIf="problem" role="alert" style="color:var(--sms-danger)">{{ problem }}</span></span>
  <p-select *ngSwitchCase="'select'" [options]="field.options ?? []" optionLabel="label" optionValue="value" [(ngModel)]="row[field.key]" appendTo="body" placeholder="Select" [ariaLabel]="field.label" [style]="{ width: '100%' }"></p-select>
  <p-select *ngSwitchCase="'lookup'" [options]="lookupOptions" optionLabel="label" optionValue="value" [(ngModel)]="row[field.key]" [filter]="true" appendTo="body" placeholder="Select" [ariaLabel]="field.label" [style]="{ width: '100%' }"></p-select>
  <input *ngSwitchDefault pInputText [(ngModel)]="row[field.key]" [attr.aria-label]="field.label" [attr.maxlength]="field.maxLength" style="width:100%" />
</ng-container>`
})
export class HrmCellComponent {
  @Input({ required: true }) field!: Field;
  @Input({ required: true }) row!: Record<string, any>;
  @Input() lookups?: HrmLookups;
  @Input() tracker?: UploadTracker;
  get lookupOptions(): Opt[] { return this.field.lookup ? (this.lookups?.options[this.field.lookup] ?? []) : []; }
  private readonly api = inject(HrmApi);
  problem = '';
  pick(ev: Event): void {
    const file = (ev.target as HTMLInputElement).files?.[0];
    if (!file || !this.field.interfaceCode) return;
    this.problem = '';
    this.api.attach(file, this.field.interfaceCode, this.field.docId?.(this.row) || uuid()).subscribe({
      next: id => { this.tracker?.drop(this.row[this.field.key]); this.tracker?.add(id); this.row[this.field.key] = id; },
      error: err => { this.tracker?.drop(this.row[this.field.key]); this.row[this.field.key] = null; this.problem = hrmError(err, 'The file could not be uploaded.'); }
    });
  }
}

/** Renders Field[] over a plain values object. No save logic: the page owns that. Works inside or outside a dialog. */
@Component({
  selector: 'hrm-form',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, CheckboxModule, InputTextModule, SelectModule, TextareaModule, HrmCellComponent],
  template: `
<div class="sf-fields" data-testid="hrm-form">
  <ng-container *ngFor="let f of fields">
    <div class="sf-fld" *ngIf="shown(f)" [class.hrm-full]="f.full || f.type === 'rows' || f.type === 'textarea' || f.type === 'info'">
      <ng-container *ngIf="f.type !== 'bool' && f.type !== 'info'"><label [attr.for]="id(f)" [class.sf-req]="f.required">{{ f.label }}</label></ng-container>
      <ng-container [ngSwitch]="f.type || 'text'">
        <div *ngSwitchCase="'info'" class="gl-note" [attr.data-testid]="'fld-' + f.key"><i class="pi pi-info-circle"></i><div>{{ f.label }}</div></div>
        <textarea *ngSwitchCase="'textarea'" pTextarea [id]="id(f)" rows="3" [(ngModel)]="values[f.key]" [attr.maxlength]="f.maxLength" [disabled]="locked(f)" [attr.data-testid]="'fld-' + f.key"></textarea>
        <input *ngSwitchCase="'number'" pInputText [id]="id(f)" type="number" [(ngModel)]="values[f.key]" [attr.min]="f.min" [attr.max]="f.max" [attr.step]="f.step ?? 'any'" [disabled]="locked(f)" [attr.data-testid]="'fld-' + f.key" />
        <input *ngSwitchCase="'date'" pInputText [id]="id(f)" type="date" [(ngModel)]="values[f.key]" [disabled]="locked(f)" [attr.data-testid]="'fld-' + f.key" />
        <input *ngSwitchCase="'datetime'" pInputText [id]="id(f)" type="datetime-local" [(ngModel)]="values[f.key]" [disabled]="locked(f)" [attr.data-testid]="'fld-' + f.key" />
        <input *ngSwitchCase="'month'" pInputText [id]="id(f)" type="month" [ngModel]="monthOf(values[f.key])" (ngModelChange)="values[f.key] = $event ? $event + '-01' : null" [disabled]="locked(f)" [attr.data-testid]="'fld-' + f.key" />
        <input *ngSwitchCase="'time'" pInputText [id]="id(f)" type="time" step="60" [ngModel]="(values[f.key] || '').slice(0, 5)" (ngModelChange)="values[f.key] = $event ? $event + ':00' : null" [disabled]="locked(f)" [attr.data-testid]="'fld-' + f.key" />
        <p-select *ngSwitchCase="'select'" [inputId]="id(f)" [options]="f.options ?? []" optionLabel="label" optionValue="value" [(ngModel)]="values[f.key]" [showClear]="!f.required" placeholder="Select" appendTo="body" styleClass="hrm-w100" [disabled]="locked(f)" [attr.data-testid]="'fld-' + f.key"></p-select>
        <p-select *ngSwitchCase="'lookup'" [inputId]="id(f)" [options]="optionsOf(f)" optionLabel="label" optionValue="value" [(ngModel)]="values[f.key]" [showClear]="!f.required" [filter]="true" [placeholder]="f.placeholder ?? 'Select'" appendTo="body" styleClass="hrm-w100" [disabled]="locked(f)"
                  (onFilter)="onFilter(f, $event.filter)" [attr.data-testid]="'fld-' + f.key"></p-select>
        <label *ngSwitchCase="'bool'" class="hrm-reveal"><p-checkbox [binary]="true" [(ngModel)]="values[f.key]" [inputId]="id(f)" [disabled]="locked(f)" [attr.data-testid]="'fld-' + f.key"></p-checkbox> {{ f.label }}</label>
        <div *ngSwitchCase="'weekdays'" class="hrm-weekdays" [attr.data-testid]="'fld-' + f.key">
          <label *ngFor="let d of week" class="hrm-reveal"><p-checkbox [binary]="true" [ngModel]="hasDay(f, d.value)" (ngModelChange)="setDay(f, d.value, $event)" [inputId]="id(f) + d.value"></p-checkbox> {{ d.label }}</label>
        </div>
        <div *ngSwitchCase="'file'">
          <input type="file" [id]="id(f)" (change)="pick(f, $event)" [disabled]="locked(f)" [attr.data-testid]="'fld-' + f.key" />
          <div class="sf-mu sf-sm" *ngIf="uploading[f.key]"><i class="pi pi-spin pi-spinner"></i> Uploading…</div>
          <div class="sf-mu sf-sm" *ngIf="!uploading[f.key] && values[f.key]"><i class="pi pi-check"></i> File attached</div>
          <div class="gl-note er" *ngIf="fileError[f.key]" role="alert"><i class="pi pi-exclamation-triangle"></i><div>{{ fileError[f.key] }}</div></div>
        </div>
        <div *ngSwitchCase="'rows'" [attr.data-testid]="'fld-' + f.key">
          <div class="gl-scroll">
            <table class="hrm-table">
              <thead><tr><th *ngFor="let c of f.rows">{{ c.label }}<span *ngIf="c.required"> *</span></th><th></th></tr></thead>
              <tbody>
                <tr *ngFor="let row of values[f.key]; let i = index">
                  <td *ngFor="let c of f.rows" style="min-width:7rem">
                    <hrm-cell [field]="c" [row]="row" [lookups]="lookups" [tracker]="tracker"></hrm-cell>
                  </td>
                  <td><p-button icon="pi pi-trash" [text]="true" severity="danger" size="small" [ariaLabel]="'Remove row ' + (i + 1)" (onClick)="removeRow(f, i)" [attr.data-testid]="'rm-' + f.key + '-' + i"></p-button></td>
                </tr>
                <tr *ngIf="!values[f.key]?.length"><td [attr.colspan]="(f.rows?.length ?? 0) + 1" class="sf-mu">No rows yet.</td></tr>
              </tbody>
            </table>
          </div>
          <p-button label="Add row" icon="pi pi-plus" [text]="true" size="small" (onClick)="addRow(f)" [attr.data-testid]="'add-' + f.key"></p-button>
        </div>
        <input *ngSwitchDefault pInputText [id]="id(f)" [(ngModel)]="values[f.key]" [attr.maxlength]="f.maxLength" [placeholder]="f.placeholder ?? ''" [disabled]="locked(f)" [attr.data-testid]="'fld-' + f.key" />
      </ng-container>
      <span class="sf-mu sf-sm" *ngIf="f.hint">{{ f.hint }}</span>
    </div>
  </ng-container>
</div>`,
  styleUrls: ['../../finance/gl/gl.scss', './hrm.scss'],
  styles: [`:host { display: block; } input[pInputText], textarea { width: 100%; } :host ::ng-deep .hrm-w100 { width: 100%; }`]
})
export class HrmFormComponent implements OnDestroy {
  private readonly api = inject(HrmApi);

  @Input({ required: true }) fields: Field[] = [];
  @Input({ required: true }) values: Record<string, any> = {};
  @Input() editing = false;
  @Input() lookups?: HrmLookups;
  @Input() idPrefix = 'hf';

  readonly week = WEEK_DAYS;
  uploading: Record<string, boolean> = {};
  fileError: Record<string, string> = {};
  private searched: Record<string, Opt[]> = {};
  private readonly filter$ = new Subject<{ key: string; term: string }>();
  private readonly sub: Subscription = this.filter$.pipe(debounceTime(300), switchMap(x =>
    (this.lookups ? this.lookups.searchEmployees(x.term) : of([] as Opt[])).pipe(map(list => { this.searched[x.key] = list; }))
  )).subscribe();

  readonly tracker = new UploadTracker(this.api);

  ngOnDestroy(): void {
    this.sub.unsubscribe();
    if (!SAVED_FORMS.has(this.values)) this.tracker.dropAll();
  }

  id(f: Field): string { return `${this.idPrefix}-${f.key}`; }
  shown(f: Field): boolean { return !f.visible || f.visible(this.values); }
  locked(f: Field): boolean { return this.editing && !!f.lockOnEdit; }
  monthOf(v: string | null | undefined): string { return v ? v.slice(0, 7) : ''; }

  optionsOf(f: Field): Opt[] {
    const base = f.lookup ? (this.lookups?.options[f.lookup] ?? []) : (f.options ?? []);
    const extra = this.searched[f.key];
    if (!extra?.length) return base;
    const seen = new Set(base.map(o => o.value));
    return [...base, ...extra.filter(o => !seen.has(o.value))];
  }

  /** The employee picker holds the first 200; a term that finds nothing there asks the server. */
  onFilter(f: Field, term: string): void {
    if (f.lookup === 'employees' && term?.trim().length >= 2) this.filter$.next({ key: f.key, term: term.trim() });
  }

  hasDay(f: Field, flag: number): boolean { return (Number(this.values[f.key] ?? 0) & flag) !== 0; }
  setDay(f: Field, flag: number, on: boolean): void {
    const cur = Number(this.values[f.key] ?? 0);
    this.values[f.key] = on ? cur | flag : cur & ~flag;
  }

  addRow(f: Field): void { this.values[f.key] = [...(this.values[f.key] ?? []), formDefaults(f.rows ?? [])]; }
  removeRow(f: Field, i: number): void {
    for (const c of f.rows ?? []) if (c.type === 'file') this.tracker.drop(this.values[f.key][i]?.[c.key]);
    this.values[f.key] = (this.values[f.key] as any[]).filter((_, x) => x !== i);
  }

  pick(f: Field, ev: Event): void {
    const file = (ev.target as HTMLInputElement).files?.[0];
    if (!file || !f.interfaceCode) return;
    this.fileError[f.key] = '';
    this.uploading[f.key] = true;
    this.api.attach(file, f.interfaceCode, f.docId?.(this.values) || uuid()).subscribe({
      next: id => { this.uploading[f.key] = false; this.tracker.drop(this.values[f.key]); this.tracker.add(id); this.values[f.key] = id; },
      error: err => { this.uploading[f.key] = false; this.tracker.drop(this.values[f.key]); this.values[f.key] = null; this.fileError[f.key] = hrmError(err, 'The file could not be uploaded.'); }
    });
  }
}

