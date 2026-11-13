import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable, Subject, debounceTime, of, switchMap } from 'rxjs';

import { saveDownload } from '../../../services/import.service';
import { FLOW } from '../../../shared/flow';
import { AuthService } from '../../service/auth.service';
import { HrmApi, Page, Params, rowsOf } from './hrm.api';
import { HrmDialogComponent } from './hrm-dialog.component';
import { Field, HrmFormComponent, cleanBody, formDefaults, formProblem, lookupsOf, markFilesSaved } from './hrm-form.component';
import { HrmLookups, LookupKey } from './hrm-lookups.service';
import { HrmPdfViewerComponent } from './hrm-pdf-viewer.component';
import { Opt, hrmError, hrmErrorAsync, hrmLabel, hrmTone, money, utcInstant } from './hrm.shared';

export type ColumnKind = 'text' | 'mono' | 'money' | 'num' | 'date' | 'datetime' | 'pill' | 'bool';

export interface Column {
  key: string;
  label: string;
  kind?: ColumnKind;
  value?: (r: any) => unknown;
  sub?: (r: any) => string | null | undefined;
  /** A link on the value. */
  link?: (r: any) => any[];
  /** S2/S3: an empty value here is hidden from this caller, not absent. */
  masked?: (r: any) => boolean;
  /** Hide this column without the permission (S2 columns). */
  perm?: string[];
}

export interface Filter {
  key: string;
  label: string;
  type: 'search' | 'select' | 'lookup' | 'date' | 'number';
  options?: Opt[];
  lookup?: LookupKey;
  def?: unknown;
  required?: boolean;
  /** Shown only with one of these (exact CNIC search). */
  perm?: string[];
}

export interface ActionBase {
  id: string;
  label: string;
  icon?: string;
  danger?: boolean;
  primary?: boolean;
  /** Any one of these. */
  perm?: string[];
}

export interface RowAction extends ActionBase {
  when?: (r: any) => boolean;
  method?: 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  path?: (r: any) => string;
  /** Asked for first, in a dialog; the values are the body unless `body` says otherwise. */
  fields?: Field[];
  body?: (r: any, v: Record<string, any>, extra?: any) => unknown;
  /** Values the prompt opens with. */
  prefill?: (r: any) => Record<string, any>;
  /** A read the body depends on, made when the user saves; its answer is `extra`, its refusal stops the save. */
  resolve?: (api: HrmApi, can: (codes: string[]) => boolean) => Observable<any>;
  confirm?: (r: any) => string;
  done?: string | ((res: any) => string);
  /** Shows a server PDF in the viewer dialog (task 271). */
  preview?: (r: any) => string;
  /** A file (PDF/CSV) from the server. */
  download?: (r: any) => string;
  fileName?: (r: any) => string;
  nav?: (r: any) => any[];
  /** Opens this config's read-only detail. */
  view?: boolean;
}

export interface Command extends ActionBase {
  method?: 'POST' | 'PUT';
  path?: string;
  fields?: Field[];
  body?: (v: Record<string, any>) => unknown;
  /** Multipart CSV to this path. */
  upload?: boolean;
  accept?: string;
  done?: string | ((res: any) => string);
  nav?: any[];
}

export interface RecordsConfig {
  id: string;
  crumbs: string[];
  title: string;
  sub?: string;
  /** A standing warning the page must say plainly. */
  notice?: string;
  listPath: string;
  listParams?: Params;
  /** The list is POSTed to `path` (filters as the body) whenever one of these filters is filled, so the value stays out of the URL. */
  bodySearch?: { path: string; keys: string[] };
  /** Without these the list is not asked for (a route open to several codes). */
  columns: Column[];
  filters?: Filter[];
  /** Status chips. */
  chips?: { key: string; options: Opt[]; def?: string };
  empty?: string;
  rowLink?: (r: any) => any[];
  tiles?: (rows: any[]) => { l: string; v: string | number; s?: string }[];
  /** Tiles from their own read (balances, summary) instead of from the rows. */
  tilesFrom?: { path: string; params?: () => Params; map: (res: any) => { l: string; v: string | number; s?: string }[] };
  /** New / edit. */
  form?: {
    noun: string;
    fields: Field[];
    createPerm?: string[];
    editPerm?: string[];
    /** Default: POST listPath, PUT listPath/{uuid}. */
    createPath?: string;
    updatePath?: (r: any) => string;
    canEdit?: (r: any) => boolean;
    /** Read the full row first (`GET` this) when the list holds less. */
    load?: (r: any) => string;
    fromRow?: (r: any) => Record<string, any>;
    toBody?: (v: Record<string, any>, r: any | null, lookups: HrmLookups) => unknown;
    wide?: boolean;
    icon?: string;
  };
  /** PATCH {uuid}/active with this permission. */
  /** `clientSide`: the list route has no `includeInactive` and answers every row, so inactive rows are hidden here (REV49B-11). */
  active?: { perm: string[]; path?: (r: any) => string; clientSide?: boolean };
  remove?: { perm: string[]; path?: (r: any) => string; when?: (r: any) => boolean; confirm?: (r: any) => string };
  actions?: RowAction[];
  commands?: Command[];
  /** Read-only detail dialog. */
  view?: {
    title: (r: any) => string;
    load?: (r: any) => string;
    kv: (r: any) => [string, unknown][];
    table?: { title: string; rows: (r: any) => any[]; columns: Column[]; action?: RowAction };
    wide?: boolean;
  };
}

/**
 * A49 — one engine for the master-data and queue pages: list report, chips, filters, paging, new / edit dialog, row actions with a
 * prompt or a confirmation, downloads with the server's refusal read out of the blob, deactivate. The pages are data in `configs/`.
 * The route hands over the config (`data.config`). Paged and plain answers are both understood (contract §0).
 */
@Component({
  selector: 'hrm-records',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, ConfirmDialogModule, InputTextModule, SelectModule, ToastModule, HrmDialogComponent, HrmFormComponent, HrmPdfViewerComponent, ...FLOW],
  providers: [MessageService, ConfirmationService, HrmLookups],
  styleUrls: ['../../finance/gl/gl.scss', './hrm.scss'],
  templateUrl: './hrm-records.component.html'
})
export class HrmRecordsComponent implements OnInit {
  private readonly api = inject(HrmApi);
  private readonly auth = inject(AuthService);
  private readonly confirmer = inject(ConfirmationService);
  private readonly messages = inject(MessageService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  readonly lookups = inject(HrmLookups);

  cfg!: RecordsConfig;
  rows: any[] = [];
  tiles: { l: string; v: string | number; s?: string }[] = [];
  total = 0;
  page = 1;
  totalPages = 1;
  loading = false;
  loadError = '';
  q: Record<string, any> = {};
  chip: string | null = null;
  includeInactive = false;
  private readonly search$ = new Subject<void>();

  // Form dialog
  formOpen = false;
  formRow: any | null = null;
  formValues: Record<string, any> = {};
  formError = '';
  formBusy = false;

  // Action prompt dialog
  promptOpen = false;
  prompt: RowAction | Command | null = null;
  promptRow: any | null = null;
  promptValues: Record<string, any> = {};
  promptError = '';
  promptBusy = false;

  // Detail dialog
  viewOpen = false;
  viewRow: any | null = null;
  viewBusy = false;

  previewOpen = false;
  previewPath = '';
  previewName = '';

  busyRow: string | null = null;

  readonly hrmLabel = hrmLabel;
  readonly utc = utcInstant;
  readonly hrmTone = hrmTone;

  ngOnInit(): void {
    this.cfg = this.route.snapshot.data['config'];
    for (const f of this.cfg.filters ?? []) if (f.def !== undefined) this.q[f.key] = f.def;
    this.chip = this.cfg.chips?.def ?? null;
    const lookups = (this.cfg.filters ?? []).filter(f => f.lookup).map(f => f.lookup!);
    if (lookups.length) this.lookups.need(lookups).subscribe();
    this.search$.pipe(debounceTime(300)).subscribe(() => { this.page = 1; this.load(); });
    this.loadTiles();
    this.load();
  }

  can(perm: string[] | undefined): boolean { return !perm?.length || this.auth.hasAnyPermission(...perm); }

  get columns(): Column[] { return this.cfg.columns.filter(c => this.can(c.perm)); }
  get commands(): Command[] { return (this.cfg.commands ?? []).filter(c => this.can(c.perm)); }
  get canCreate(): boolean { return !!this.cfg.form && !!this.cfg.form.createPerm && this.can(this.cfg.form.createPerm); }

  actionsFor(r: any): RowAction[] { return (this.cfg.actions ?? []).filter(a => this.can(a.perm) && (!a.when || a.when(r))); }
  canEdit(r: any): boolean { return !!this.cfg.form?.editPerm && this.can(this.cfg.form.editPerm) && (!this.cfg.form.canEdit || this.cfg.form.canEdit(r)); }
  canActive(): boolean { return !!this.cfg.active && this.can(this.cfg.active.perm); }
  canRemove(r: any): boolean { return !!this.cfg.remove && this.can(this.cfg.remove.perm) && (!this.cfg.remove.when || this.cfg.remove.when(r)); }
  hasRowActions(): boolean { return !!(this.cfg.actions?.length || this.cfg.form?.editPerm || this.cfg.active || this.cfg.remove); }

  optionsOf(f: Filter): Opt[] { return f.lookup ? (this.lookups.options[f.lookup] ?? []) : (f.options ?? []); }

  // ── List ───────────────────────────────────────────────────────────────

  load(): void {
    const params: Params = { ...this.cfg.listParams, ...this.q, page: this.page };
    if (this.cfg.chips && this.chip) params[this.cfg.chips.key] = this.chip;
    if (this.cfg.active && !this.cfg.active.clientSide && this.includeInactive) params['includeInactive'] = true;
    this.loading = !this.rows.length;
    this.loadError = '';
    // REV49-12: a sensitive filter (the exact CNIC) goes in a POST body, never in the URL.
    const viaBody = !!this.cfg.bodySearch && this.cfg.bodySearch.keys.some(k => !!params[k]);
    const request$ = viaBody ? this.api.post<any>(this.cfg.bodySearch!.path, params) : this.api.get<any>(this.cfg.listPath, params);
    request$.subscribe({
      next: (res: any) => {
        this.loading = false;
        this.rows = rowsOf(res);
        if (this.cfg.active?.clientSide && !this.includeInactive) this.rows = this.rows.filter(r => r?.isActive !== false);
        const p = res as Page<any>;
        this.total = Array.isArray(res) ? this.rows.length : (p?.totalRecords ?? this.rows.length);
        this.totalPages = Array.isArray(res) ? 1 : (p?.totalPages ?? 1);
        if (!this.cfg.tilesFrom) this.tiles = this.cfg.tiles && this.rows.length ? this.cfg.tiles(this.rows) : [];
      },
      error: err => { this.loading = false; this.rows = []; this.total = 0; this.loadError = hrmError(err, `${this.cfg.title} could not be loaded.`); }
    });
  }

  private loadTiles(): void {
    const from = this.cfg.tilesFrom;
    if (!from) return;
    this.api.get<any>(from.path, from.params?.()).subscribe({ next: res => { this.tiles = from.map(res); }, error: () => { this.tiles = []; } });
  }

  get filters(): Filter[] { return (this.cfg.filters ?? []).filter(f => this.can(f.perm)); }

  onFilter(f: Filter): void {
    if (f.type === 'search') this.search$.next(); else { this.page = 1; this.load(); }
  }

  setChip(v: string | null): void { this.chip = v; this.page = 1; this.load(); }
  go(delta: number): void { this.page = Math.min(Math.max(1, this.page + delta), this.totalPages); this.load(); }

  // ── Cells ──────────────────────────────────────────────────────────────

  cell(c: Column, r: any): unknown { return c.value ? c.value(r) : r[c.key]; }

  text(c: Column, r: any): string {
    const v = this.cell(c, r);
    if ((v === null || v === undefined || v === '') && c.masked?.(r)) return 'Hidden';
    if (v === null || v === undefined || v === '') return '—';
    switch (c.kind) {
      case 'money': return money(v as number);
      case 'num': return Number(v).toLocaleString('en-US', { maximumFractionDigits: 2 });
      case 'bool': return v ? 'Yes' : 'No';
      case 'pill': return hrmLabel(String(v));
      default: return String(v);
    }
  }

  isNum(c: Column): boolean { return c.kind === 'money' || c.kind === 'num'; }
  isOff(r: any): boolean { return r?.isActive === false; }

  // ── New / edit ─────────────────────────────────────────────────────────

  openNew(): void {
    const f = this.cfg.form!;
    this.formRow = null;
    this.formValues = formDefaults(f.fields);
    this.openForm();
  }

  openEdit(r: any): void {
    const f = this.cfg.form!;
    this.formRow = r;
    if (f.load) {
      this.busyRow = r.uuid;
      this.api.get<any>(f.load(r)).subscribe({
        next: full => { this.busyRow = null; this.formRow = full; this.formValues = this.valuesFrom(full); this.openForm(); },
        error: err => { this.busyRow = null; this.toastError(hrmError(err, 'The record could not be opened.')); }
      });
      return;
    }
    this.formValues = this.valuesFrom(r);
    this.openForm();
  }

  private valuesFrom(r: any): Record<string, any> {
    const f = this.cfg.form!;
    return { ...formDefaults(f.fields), ...structuredClone(f.fromRow ? f.fromRow(r) : r) };
  }

  private openForm(): void {
    this.formError = '';
    this.lookups.need(lookupsOf(this.cfg.form!.fields)).subscribe(() => { this.formOpen = true; });
  }

  saveForm(): void {
    const f = this.cfg.form!;
    this.formError = formProblem(f.fields, this.formValues) ?? '';
    if (this.formError) return;
    const clean = cleanBody(f.fields, this.formValues);
    const body = f.toBody ? f.toBody(clean, this.formRow, this.lookups) : clean;
    const call: Observable<any> = this.formRow
      ? this.api.put((f.updatePath ?? (r => `${this.cfg.listPath}/${r.uuid}`))(this.formRow), body)
      : this.api.post(f.createPath ?? this.cfg.listPath, body);
    this.formBusy = true;
    call.subscribe({
      next: () => {
        markFilesSaved(this.formValues);
        this.formBusy = false;
        this.formOpen = false;
        this.lookups.reset();
        this.toastOk(`${f.noun} saved.`);
        this.load();
      },
      error: err => { this.formBusy = false; this.formError = hrmError(err, `The ${f.noun.toLowerCase()} could not be saved.`); }
    });
  }

  // ── Active / delete ────────────────────────────────────────────────────

  toggleActive(r: any): void {
    const on = !r.isActive;
    const path = this.cfg.active!.path ?? (x => `${this.cfg.listPath}/${x.uuid}/active`);
    const go = () => this.api.patch(path(r), { isActive: on }).subscribe({
      next: () => { this.lookups.reset(); this.toastOk(on ? 'Activated.' : 'Deactivated.'); this.load(); },
      error: err => this.toastError(hrmError(err, 'The change was refused.'))
    });
    if (on) { go(); return; }
    this.ask('Deactivate', `Deactivate ${r.name ?? r.code ?? 'this record'}? It stays in history but can no longer be chosen.`, 'pi pi-ban', go);
  }

  removeRow(r: any): void {
    const del = this.cfg.remove!;
    const msg = del.confirm?.(r) ?? 'Delete this record? This cannot be undone.';
    this.ask('Delete', msg, 'pi pi-trash', () => this.api.delete((del.path ?? (x => `${this.cfg.listPath}/${x.uuid}`))(r)).subscribe({
      next: () => { this.toastOk('Deleted.'); this.load(); },
      error: err => this.toastError(hrmError(err, 'It could not be deleted.'))
    }));
  }

  // ── Actions ────────────────────────────────────────────────────────────

  runRow(a: RowAction, r: any): void {
    if (a.nav) { this.router.navigate(a.nav(r)); return; }
    if (a.view) { this.openView(r); return; }
    if (a.preview) { this.previewPath = a.preview(r); this.previewName = a.fileName?.(r) ?? 'document.pdf'; this.previewOpen = true; return; }
    if (a.download) { this.download(a, r); return; }
    if (a.fields) { this.openPrompt(a, r); return; }
    const send = () => this.sendRow(a, r, {}, false);
    if (a.confirm) this.ask(a.label, a.confirm(r), a.icon ?? 'pi pi-check', send); else send();
  }

  private download(a: RowAction, r: any): void {
    this.busyRow = r.uuid;
    this.api.blob(a.download!(r)).subscribe({
      next: res => { this.busyRow = null; saveDownload(res, a.fileName?.(r) ?? 'document.pdf'); },
      error: async err => { this.busyRow = null; this.toastError(await hrmErrorAsync(err, 'The file could not be downloaded.')); }
    });
  }

  private sendRow(a: RowAction, r: any, v: Record<string, any>, inDialog: boolean): void {
    if (inDialog) this.promptBusy = true; else this.busyRow = r.uuid;
    const first = a.resolve ? a.resolve(this.api, codes => this.can(codes)) : of(undefined);
    first.pipe(switchMap(extra => this.api.send(a.method ?? 'POST', a.path!(r), a.body ? a.body(r, v, extra) : v))).subscribe({
      next: res => { this.afterAction(a, res); },
      error: err => {
        this.promptBusy = false; this.busyRow = null;
        const msg = hrmError(err, `${a.label} was refused.`);
        if (inDialog) this.promptError = msg; else this.toastError(msg);
      }
    });
  }

  private afterAction(a: RowAction | Command, res: any): void {
    markFilesSaved(this.promptValues);
    this.promptBusy = false; this.busyRow = null; this.promptOpen = false;
    this.toastOk(typeof a.done === 'function' ? a.done(res) : (a.done ?? 'Done.'));
    this.load();
  }

  openPrompt(a: RowAction | Command, r: any | null): void {
    this.prompt = a;
    this.promptRow = r;
    this.promptValues = { ...formDefaults(a.fields ?? []), ...((a as RowAction).prefill?.(r) ?? {}) };
    this.promptError = '';
    this.lookups.need(lookupsOf(a.fields ?? [])).subscribe(() => { this.promptOpen = true; });
  }

  submitPrompt(): void {
    const a = this.prompt!;
    const fields = a.fields ?? [];
    this.promptError = formProblem(fields, this.promptValues) ?? '';
    if (this.promptError) return;
    const clean = cleanBody(fields, this.promptValues);
    if (this.promptRow) { this.sendRow(a as RowAction, this.promptRow, clean, true); return; }
    const c = a as Command;
    this.promptBusy = true;
    this.api.send(c.method ?? 'POST', c.path!, c.body ? c.body(clean) : clean).subscribe({
      next: res => this.afterAction(c, res),
      error: err => { this.promptBusy = false; this.promptError = hrmError(err, `${c.label} was refused.`); }
    });
  }

  runCommand(c: Command, input?: HTMLInputElement): void {
    if (c.nav) { this.router.navigate(c.nav); return; }
    if (c.upload) { input?.click(); return; }
    if (c.fields) { this.openPrompt(c, null); return; }
    this.api.send(c.method ?? 'POST', c.path!, c.body ? c.body({}) : {}).subscribe({
      next: res => this.afterAction(c, res),
      error: err => this.toastError(hrmError(err, `${c.label} was refused.`))
    });
  }

  uploadFile(c: Command, ev: Event): void {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    this.api.upload(c.path!, file).subscribe({
      next: res => { input.value = ''; this.afterAction(c, res); },
      error: err => { input.value = ''; this.toastError(hrmError(err, 'The file was refused.')); }
    });
  }

  // ── Detail ─────────────────────────────────────────────────────────────

  openView(r: any): void {
    const v = this.cfg.view!;
    if (v.load) {
      this.busyRow = r.uuid;
      this.api.get<any>(v.load(r)).subscribe({
        next: full => { this.busyRow = null; this.viewRow = full; this.viewOpen = true; },
        error: err => { this.busyRow = null; this.toastError(hrmError(err, 'The record could not be opened.')); }
      });
      return;
    }
    this.viewRow = r;
    this.viewOpen = true;
  }

  kvText(v: unknown): string {
    if (v === null || v === undefined || v === '') return '—';
    if (typeof v === 'boolean') return v ? 'Yes' : 'No';
    return String(v);
  }

  // ── Plumbing ───────────────────────────────────────────────────────────

  private ask(header: string, message: string, icon: string, accept: () => void): void {
    this.confirmer.confirm({ key: 'hrm-rec', header, message, icon, acceptLabel: header, rejectLabel: 'Cancel', rejectButtonStyleClass: 'p-button-text', accept });
  }

  private toastOk(detail: string): void { this.messages.add({ severity: 'success', summary: 'Done', detail, life: 3500 }); }
  private toastError(detail: string): void { this.messages.add({ severity: 'error', summary: 'Not done', detail, life: 8000 }); }
}

