import { Component, Input, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';

import {
  DocumentOverrideModel, ProviderDefaultModel, ProviderDefaultsModel, QuickBooksIntegrationService, ReferenceDataModel,
  RemoteEntityModel, SyncControlDocumentType, qboErrorMessage
} from '../../../../services/quickbooks-integration.service';
import { Option, termOptions } from '../quickbooks.shared';
import {
  DEFAULT_KEYS, OVERRIDE_KEYS, OverrideDraft, PAYMENT_METHODS, PAYMENT_METHOD_LABEL, PaymentMethodCode, SYNC_CONTROL_LABEL,
  bankAccountOptions, canonicalKey, defaultsRequest, methodKey, overridesFrom, overridesRequest, refOptions, valuesByKey
} from '../a44.shared';

/** Document types an override can target: the ones that become QuickBooks transactions. */
export const OVERRIDE_DOCUMENT_TYPES: SyncControlDocumentType[] = [
  'SalesInvoice', 'CreditNote', 'Payment', 'POSSale', 'POSReturn', 'PurchaseBill', 'BillPayment', 'VendorCredit'
];

const KEY_LABEL: Record<string, string> = {
  [DEFAULT_KEYS.ClassRef]: 'Class',
  [DEFAULT_KEYS.DepartmentRef]: 'Location',
  [DEFAULT_KEYS.SalesTermRef]: 'Sales terms'
};

/**
 * A44 D-15 — what QuickBooks transactions are stamped with that SCM documents do not carry: class and location
 * (only when the company tracks them), sales terms, the payment method and deposit account for each way a customer
 * pays, the bank account each way a supplier is paid from, the refund account and the POS walk-in customer — plus
 * per-document-type overrides, which win over the defaults.
 */
@Component({
  selector: 'app-qbo-defaults-tab',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, InputTextModule, SelectModule, TooltipModule],
  templateUrl: './defaults-tab.component.html',
  styleUrls: ['../quickbooks-tab.scss', './defaults-tab.component.scss']
})
export class DefaultsTabComponent implements OnInit {
  @Input() canManage = false;

  readonly methods = PAYMENT_METHODS;
  readonly methodLabel = PAYMENT_METHOD_LABEL;
  readonly keys = DEFAULT_KEYS;
  readonly methodKey = methodKey;

  isLoading = true;
  loadFailed = false;
  loadError = '';
  isRefreshing = false;
  isSaving = false;
  isSavingOverrides = false;

  reference: ReferenceDataModel | null = null;
  /** The server's tracking flags and document types (GET provider-defaults); the reference snapshot is the fallback. */
  model: ProviderDefaultsModel | null = null;
  values: Record<string, string | null> = {};
  /** Per-key warnings from the server (value gone / inactive / not sent while tracking is off). */
  warnings: Record<string, string> = {};
  private baseline = '{}';
  overrides: OverrideDraft[] = [];
  private overridesBaseline = '[]';
  customers: RemoteEntityModel[] = [];

  classOpts: Option[] = [];
  deptOpts: Option[] = [];
  termOpts: Option[] = [];
  methodOpts: Option[] = [];
  /** Deposit to: Bank / Other Current Asset. */
  depositOpts: Option[] = [];
  /** Bill payments paid from: Bank / Credit Card. */
  bankOpts: Option[] = [];
  /** Refunds from: Bank / Other Current Asset / Credit Card. */
  refundOpts: Option[] = [];
  customerOpts: Option[] = [];

  documentTypeOpts: Option[] = OVERRIDE_DOCUMENT_TYPES.map(t => ({ label: SYNC_CONTROL_LABEL[t], value: t }));

  constructor(private service: QuickBooksIntegrationService, private messages: MessageService) {}

  ngOnInit(): void { this.load(); }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    forkJoin({
      reference: this.service.getReference().pipe(catchError(() => of(null))),
      defaults: this.service.getProviderDefaults(),
      overrides: this.service.getDocumentOverrides().pipe(catchError(() => of([] as DocumentOverrideModel[]))),
      customers: this.service.getRemote('Customer', null).pipe(catchError(() => of([] as RemoteEntityModel[])))
    }).subscribe({
      next: (r) => {
        this.isLoading = false;
        this.reference = r.reference;
        this.customers = r.customers ?? [];
        this.applyModel(r.defaults);
        this.applyOverrides(r.overrides ?? []);
      },
      error: (err) => {
        this.isLoading = false;
        this.loadFailed = true;
        this.loadError = qboErrorMessage(err, 'The QuickBooks defaults could not be loaded.');
      }
    });
  }

  // ── Tracking ────────────────────────────────────────────────────────────────

  private get classTracking(): boolean | null {
    const m = this.model?.classTrackingEnabled;
    if (m !== undefined && m !== null) return m;
    const t = this.reference?.tracking;
    if (!t || (t.classTrackingPerTransaction == null && t.classTrackingPerLine == null)) return null;
    return !!t.classTrackingPerTransaction || !!t.classTrackingPerLine;
  }

  private get departmentTracking(): boolean | null {
    const m = this.model?.departmentTrackingEnabled;
    if (m !== undefined && m !== null) return m;
    return this.reference?.tracking?.departmentTracking ?? null;
  }

  /** The company tracks classes / locations (QuickBooks preferences). Unknown = not shown. */
  get tracksClasses(): boolean { return this.classTracking === true; }
  get tracksDepartments(): boolean { return this.departmentTracking === true; }

  /** What the company calls locations (Location, Department, Division…). */
  get departmentLabel(): string {
    return this.model?.departmentTerminology || this.reference?.tracking?.departmentTerminology || 'Location';
  }

  /** Why the class / location pickers are missing; empty when both are shown. */
  get trackingOffText(): string {
    if (this.referenceIncomplete) return '';
    const loc = this.departmentLabel.toLowerCase();
    const off = [!this.tracksClasses ? 'class' : '', !this.tracksDepartments ? loc : ''].filter(Boolean);
    if (!off.length) return '';
    const what = off.join(' and ');
    return `${what.charAt(0).toUpperCase() + what.slice(1)} tracking is off in this QuickBooks company, so nothing is sent for it.`;
  }

  /** The snapshot predates A44 (tracking unknown): classes, locations and payment methods need reading again. */
  get referenceIncomplete(): boolean {
    return this.classTracking === null || this.departmentTracking === null;
  }

  refreshReference(): void {
    if (!this.canManage || this.isRefreshing) return;
    this.isRefreshing = true;
    this.service.refreshReference().subscribe({
      next: (ref) => {
        this.isRefreshing = false;
        this.reference = ref;
        // The server's tracking flags come from this snapshot: take the fresh ones.
        if (this.model) this.model = { ...this.model, classTrackingEnabled: null, departmentTrackingEnabled: null };
        this.rebuildOptions();
        this.messages.add({ severity: 'success', summary: 'Refreshed', detail: 'Classes, locations, payment methods and accounts were read from QuickBooks.' });
      },
      error: (err) => {
        this.isRefreshing = false;
        this.messages.add({ severity: 'error', summary: 'Not refreshed', detail: qboErrorMessage(err, 'QuickBooks could not be read.') });
      }
    });
  }

  // ── Defaults ────────────────────────────────────────────────────────────────

  /** Every key this page edits, in display order. */
  get editedKeys(): string[] {
    const keys: string[] = [];
    if (this.tracksClasses) keys.push(DEFAULT_KEYS.ClassRef);
    if (this.tracksDepartments) keys.push(DEFAULT_KEYS.DepartmentRef);
    keys.push(DEFAULT_KEYS.SalesTermRef);
    for (const m of PAYMENT_METHODS) keys.push(methodKey(DEFAULT_KEYS.PaymentMethodRef, m), methodKey(DEFAULT_KEYS.DepositToAccountRef, m));
    for (const m of PAYMENT_METHODS) keys.push(methodKey(DEFAULT_KEYS.BankAccountRef, m));
    keys.push(DEFAULT_KEYS.RefundFromAccountRef);
    // D-35 — per method; the builder looks up method key → base key → that method's deposit account.
    for (const m of PAYMENT_METHODS) keys.push(methodKey(DEFAULT_KEYS.RefundFromAccountRef, m));
    keys.push(DEFAULT_KEYS.PosDefaultCustomerRef);
    return keys;
  }

  private applyModel(model: ProviderDefaultsModel | null): void {
    this.model = model;
    const types = (model?.documentTypes ?? []).filter(Boolean);
    if (types.length) {
      this.documentTypeOpts = types.map(t => ({ label: SYNC_CONTROL_LABEL[t as SyncControlDocumentType] ?? t, value: t }));
    }
    this.warnings = {};
    for (const d of model?.defaults ?? []) if (d?.warning) this.warnings[canonicalKey(d.settingKey)] = d.warning;
    this.applyDefaults(model?.defaults ?? []);
  }

  private applyDefaults(rows: ProviderDefaultModel[]): void {
    this.values = valuesByKey(rows);
    this.baseline = this.snapshot();
    this.rebuildOptions();
  }

  warningFor(key: string): string | null { return this.warnings[key] ?? null; }

  /**
   * D-35 — the account a cash refund by this method comes out of, as the builder resolves it: the method's own refund
   * account, else the refund account above, else the method's deposit account. Null when none is set (GL fallback).
   */
  effectiveRefund(m: PaymentMethodCode): { id: string; from: 'method' | 'base' | 'deposit' } | null {
    const own = this.values[methodKey(DEFAULT_KEYS.RefundFromAccountRef, m)];
    if (own) return { id: own, from: 'method' };
    const base = this.values[DEFAULT_KEYS.RefundFromAccountRef];
    if (base) return { id: base, from: 'base' };
    const deposit = this.values[methodKey(DEFAULT_KEYS.DepositToAccountRef, m)];
    return deposit ? { id: deposit, from: 'deposit' } : null;
  }

  /** The placeholder of a method's refund picker: what is used while it is empty. */
  refundPlaceholder(m: PaymentMethodCode): string {
    const base = this.values[DEFAULT_KEYS.RefundFromAccountRef];
    if (base) return `The refund account (${this.optionLabel(this.refundOpts, base)})`;
    const deposit = this.values[methodKey(DEFAULT_KEYS.DepositToAccountRef, m)];
    if (deposit) return `${PAYMENT_METHOD_LABEL[m]} deposit account (${this.optionLabel(this.depositOpts, deposit)})`;
    return 'Cash / bank account mapping';
  }

  private optionLabel(opts: Option[], id: string): string {
    return opts.find(o => o.value === id)?.label ?? `QuickBooks id ${id}`;
  }

  private rebuildOptions(): void {
    const r = this.reference;
    const v = this.values;
    const keepOverride = (key: string) => this.overrides.filter(o => o.settingKey === key).map(o => o.settingValue);
    this.classOpts = refOptions(r?.classes, [v[DEFAULT_KEYS.ClassRef], ...keepOverride(DEFAULT_KEYS.ClassRef)]);
    this.deptOpts = refOptions(r?.departments, [v[DEFAULT_KEYS.DepartmentRef], ...keepOverride(DEFAULT_KEYS.DepartmentRef)]);
    this.termOpts = termOptions(r?.terms, v[DEFAULT_KEYS.SalesTermRef]);
    for (const id of keepOverride(DEFAULT_KEYS.SalesTermRef)) {
      if (id && !this.termOpts.some(o => o.value === id)) this.termOpts = [{ label: `QuickBooks id ${id}`, value: id }, ...this.termOpts];
    }
    this.methodOpts = refOptions(r?.paymentMethods, PAYMENT_METHODS.map(m => v[methodKey(DEFAULT_KEYS.PaymentMethodRef, m)]));
    this.depositOpts = bankAccountOptions(r?.accounts, PAYMENT_METHODS.map(m => v[methodKey(DEFAULT_KEYS.DepositToAccountRef, m)]), 'deposit');
    this.bankOpts = bankAccountOptions(r?.accounts, PAYMENT_METHODS.map(m => v[methodKey(DEFAULT_KEYS.BankAccountRef, m)]), 'bank');
    this.refundOpts = bankAccountOptions(r?.accounts,
      [v[DEFAULT_KEYS.RefundFromAccountRef], ...PAYMENT_METHODS.map(m => v[methodKey(DEFAULT_KEYS.RefundFromAccountRef, m)])], 'refund');
    const custId = v[DEFAULT_KEYS.PosDefaultCustomerRef];
    this.customerOpts = this.customers
      .map(c => ({ label: c.name || `QuickBooks id ${c.remoteId}`, value: c.remoteId }))
      .sort((a, b) => a.label.localeCompare(b.label));
    if (custId && !this.customerOpts.some(o => o.value === custId)) {
      this.customerOpts.unshift({ label: `QuickBooks id ${custId}`, value: custId });
    }
  }

  private snapshot(): string {
    return JSON.stringify(this.editedKeys.map(k => [k, (this.values[k] ?? '') || null]));
  }

  get dirty(): boolean { return this.snapshot() !== this.baseline; }

  /** Enabled payment methods with no deposit account: their payments fall back to the GL mapping of cash / bank (D-16). */
  get methodsWithoutDeposit(): PaymentMethodCode[] {
    return PAYMENT_METHODS.filter(m => !this.values[methodKey(DEFAULT_KEYS.DepositToAccountRef, m)]);
  }

  save(): void {
    if (!this.canManage || this.isSaving) return;
    this.isSaving = true;
    const values = defaultsRequest(this.editedKeys, this.values);
    this.service.saveProviderDefaults({ values }).subscribe({
      next: (model) => {
        this.isSaving = false;
        if (model?.defaults?.length) this.applyModel({ ...this.model, ...model });
        else this.applyDefaults(values);
        this.messages.add({ severity: 'success', summary: 'Saved', detail: 'The QuickBooks defaults are saved.' });
      },
      error: (err) => {
        this.isSaving = false;
        this.messages.add({ severity: 'error', summary: 'Not saved', detail: qboErrorMessage(err, 'The defaults could not be saved.') });
      }
    });
  }

  discard(): void {
    const saved = JSON.parse(this.baseline) as [string, string | null][];
    this.values = Object.fromEntries(saved);
  }

  // ── Overrides ───────────────────────────────────────────────────────────────

  /** The keys an override may set, as far as the company tracks them. */
  get overrideKeyOpts(): Option[] {
    return OVERRIDE_KEYS
      .filter(k => (k !== DEFAULT_KEYS.ClassRef || this.tracksClasses) && (k !== DEFAULT_KEYS.DepartmentRef || this.tracksDepartments))
      .map(k => ({ label: this.keyLabel(k), value: k }));
  }

  valueOpts(key: string): Option[] {
    if (key === DEFAULT_KEYS.ClassRef) return this.classOpts;
    if (key === DEFAULT_KEYS.DepartmentRef) return this.deptOpts;
    if (key === DEFAULT_KEYS.SalesTermRef) return this.termOpts;
    return [];
  }

  keyLabel(key: string): string { return key === DEFAULT_KEYS.DepartmentRef ? this.departmentLabel : (KEY_LABEL[key] ?? key); }

  private applyOverrides(rows: DocumentOverrideModel[]): void {
    this.overrides = overridesFrom(rows);
    this.overridesBaseline = JSON.stringify(overridesRequest(this.overrides));
    this.rebuildOptions();
  }

  get overridesDirty(): boolean { return JSON.stringify(overridesRequest(this.overrides)) !== this.overridesBaseline; }

  /** Two rows for the same type and setting: only the last is kept on save. */
  isDuplicate(row: OverrideDraft): boolean {
    return this.overrides.filter(o => o.documentType === row.documentType && o.settingKey === row.settingKey).length > 1;
  }

  addOverride(): void {
    if (!this.canManage) return;
    const key = this.overrideKeyOpts[0]?.value ?? DEFAULT_KEYS.SalesTermRef;
    this.overrides = [...this.overrides, { documentType: OVERRIDE_DOCUMENT_TYPES[0], settingKey: key, settingValue: null }];
  }

  removeOverride(index: number): void {
    if (!this.canManage) return;
    this.overrides = this.overrides.filter((_, i) => i !== index);
  }

  onOverrideKey(row: OverrideDraft): void { row.settingValue = null; }

  saveOverrides(): void {
    if (!this.canManage || this.isSavingOverrides) return;
    this.isSavingOverrides = true;
    this.service.saveDocumentOverrides({ overrides: overridesRequest(this.overrides) }).subscribe({
      next: (rows) => {
        this.isSavingOverrides = false;
        this.applyOverrides(rows ?? overridesRequest(this.overrides));
        this.messages.add({ severity: 'success', summary: 'Saved', detail: 'The document overrides are saved.' });
      },
      error: (err) => {
        this.isSavingOverrides = false;
        this.messages.add({ severity: 'error', summary: 'Not saved', detail: qboErrorMessage(err, 'The overrides could not be saved.') });
      }
    });
  }
}
