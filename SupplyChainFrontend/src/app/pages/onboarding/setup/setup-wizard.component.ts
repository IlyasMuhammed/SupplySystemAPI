import { Component, HostListener, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ToastModule } from 'primeng/toast';
import { SelectModule } from 'primeng/select';
import { MultiSelectModule } from 'primeng/multiselect';
import { InputTextModule } from 'primeng/inputtext';
import { CheckboxModule } from 'primeng/checkbox';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { ProgressBarModule } from 'primeng/progressbar';
import { ConfirmationService, MessageService } from 'primeng/api';
import { catchError, forkJoin, of } from 'rxjs';

import {
  OnboardingSetupService, SetupApplyResponse, SetupCategoryPreview, SetupCountry, SetupIndustry, SetupState
} from '../../../services/onboarding-setup.service';
import { ModuleAdminService, ModuleService } from '../../../services/module.service';
import { ModuleCard } from '../../../services/module.models';
import { CurrenciesService } from '../../../services/currencies.service';
import { QuickBooksIntegrationService } from '../../../services/quickbooks-integration.service';
import { AuthService } from '../../service/auth.service';
import { TenantService } from '../../service/tenant.service';
import { FLOW } from '../../../shared/flow';
import { CoaTreeEditorComponent } from '../editors/coa-tree-editor.component';
import { TaxTableEditorComponent } from '../editors/tax-table-editor.component';
import {
  DEFAULT_DATA_ROUTE, InviteRow, LAST_STEP, MONTHS, WIZARD_STEPS, WarehouseInput, categoryLabel, clone, coaProblem, inviteProblem,
  onboardingError, payloadCount, resultLabel, resultTone, roleAvailable, setupStatusLabel, setupStatusTone, taxProblem, warehouseProblem
} from '../onboarding.shared';

export interface CurrencyOption { code: string; name: string; symbol?: string | null; }

/** The quick-start links on the success screen (task 140). */
export const QUICK_START_LINKS = [
  { label: 'Add your first customer', icon: 'pi pi-users', route: '/portal/pages/customers', module: 'MODULE_CUSTOMERS' },
  { label: 'Add products', icon: 'pi pi-box', route: '/portal/pages/inventory/products', module: 'MODULE_INVENTORY' },
  { label: 'Add a supplier', icon: 'pi pi-truck', route: '/portal/pages/suppliers/supplier-create', module: 'MODULE_SUPPLIERS' },
  { label: 'Create a sale order', icon: 'pi pi-shopping-bag', route: '/portal/pages/sales/orders', module: 'MODULE_DEMAND' },
  { label: 'Create a purchase order', icon: 'pi pi-shopping-cart', route: '/portal/pages/demand/purchase-orders', module: 'MODULE_DEMAND' },
  { label: 'Import your data', icon: 'pi pi-file-import', route: '/portal/pages/settings/import', module: 'MODULE_DATA_IMPORT' },
  { label: 'Review default data', icon: 'pi pi-database', route: DEFAULT_DATA_ROUTE, module: null },
  { label: 'Go to the dashboard', icon: 'pi pi-home', route: '/portal/dashboard', module: null }
];

/**
 * A50 P1-K (tasks 123–146) — `/portal/pages/onboarding/setup`: the tenant setup wizard (SETUP_WIZARD_RUN).
 *
 * Eight screens: company + industry, modules, chart of accounts, tax codes, currency + fiscal year, warehouses,
 * users + roles (+ optional QuickBooks), review + launch. The defaults come from `POST setup/preview` for the chosen
 * country / industry; the admin's edits are sent as `payloadOverrides` (only for categories they changed), Step 6
 * as `wizardInputs.WAREHOUSE`, Step 7 as `invites`. Every move between steps saves that step's draft to
 * `POST setup/state/{step}`; reopening resumes from `GET setup/state` (BR-50-002). "Skip setup" applies the GLOBAL
 * defaults (BR-50-003); "Reset to default" reloads the step from the preview.
 */
@Component({
  selector: 'app-setup-wizard',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, ConfirmDialogModule, ToastModule, SelectModule, MultiSelectModule,
    InputTextModule, CheckboxModule, ToggleSwitchModule, ProgressBarModule, ...FLOW, CoaTreeEditorComponent, TaxTableEditorComponent],
  providers: [ConfirmationService],
  styleUrls: ['../onboarding.scss'],
  templateUrl: './setup-wizard.component.html'
})
export class SetupWizardComponent implements OnInit {
  private readonly api = inject(OnboardingSetupService);
  private readonly moduleAdmin = inject(ModuleAdminService);
  private readonly modules = inject(ModuleService);
  private readonly currenciesApi = inject(CurrenciesService);
  private readonly qboApi = inject(QuickBooksIntegrationService);
  private readonly auth = inject(AuthService);
  private readonly tenant = inject(TenantService);
  private readonly router = inject(Router);
  private readonly routeInfo = inject(ActivatedRoute);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService, { optional: true });

  readonly steps = WIZARD_STEPS;
  readonly lastStep = LAST_STEP;
  readonly months = MONTHS;
  readonly periodTypes = [{ label: 'Monthly (12 periods)', value: 'Monthly' }];
  readonly quickLinks = QUICK_START_LINKS;
  readonly catLabel = categoryLabel;
  readonly statusLabel = setupStatusLabel;
  readonly statusTone = setupStatusTone;
  readonly rTone = resultTone;
  readonly rLabel = resultLabel;

  step = 1;
  maxStep = 1;
  loading = false;
  loadError = '';
  state: SetupState | null = null;

  countries: SetupCountry[] = [];
  industries: SetupIndustry[] = [];
  countryCode: string | null = null;
  industryType: string | null = null;

  previewLoading = false;
  previewError = '';
  previews: SetupCategoryPreview[] = [];
  /** The defaults as previewed (for "Reset to default"). */
  original: Record<string, any> = {};
  /** What will be applied: the defaults, or the admin's edit. */
  payloads: Record<string, any> = {};
  /** Categories the admin edited (sent as payloadOverrides). */
  dirty = new Set<string>();

  moduleCards: ModuleCard[] = [];
  modulesLoading = false;
  modulesError = '';
  moduleBusy: string | null = null;

  currencyCatalog: CurrencyOption[] = [];

  warehouses: WarehouseInput[] = [];
  invites: InviteRow[] = [];
  skip = new Set<string>();

  qboStatus: { isConnected: boolean; companyName?: string | null; status?: string } | null = null;
  qboLoading = false;
  qboError = '';

  saving = false;
  applying = false;
  applyError = '';
  applyResult: SetupApplyResponse | null = null;
  skipping = false;

  get orgName(): string { return this.tenant.tenant()?.orgName ?? ''; }
  get canManageModules(): boolean { return this.auth.hasPermission('MODULES_MANAGE'); }
  get isRerun(): boolean { return this.state?.setupStatus === 'Completed' || this.state?.setupStatus === 'Skipped'; }
  get stepInfo() { return this.steps[this.step - 1]; }
  get countryName(): string { return this.countries.find(c => c.code === this.countryCode)?.name ?? this.countryCode ?? ''; }
  get industryName(): string { return this.industries.find(c => c.code === this.industryType)?.name ?? this.industryType ?? ''; }
  get launched(): boolean { return !!this.applyResult?.completed; }

  preview(cat: string): SetupCategoryPreview | undefined { return this.previews.find(p => p.category === cat); }

  // ── Lifecycle ─────────────────────────────────────────────────────────────

  ngOnInit(): void {
    this.loading = true;
    forkJoin({
      state: this.api.getState().pipe(catchError(err => { this.loadError = onboardingError(err, 'The setup could not be loaded.'); return of(null); })),
      countries: this.api.countries().pipe(catchError(() => of(null))),
      catalog: this.currenciesApi.getAll().pipe(catchError(() => of(null)))
    }).subscribe(r => {
      this.loading = false;
      if (!r.state) return;
      this.state = r.state.result ?? null;
      this.countries = r.countries?.result ?? [];
      this.currencyCatalog = (r.catalog?.result ?? []).filter(c => !!c.code).map(c => ({ code: c.code!, name: c.name, symbol: c.symbol }));
      const data = this.state?.stepData ?? {};
      const s1 = data['1'] ?? {};
      this.countryCode = this.state?.countryCode ?? s1.countryCode ?? this.countries.find(c => c.code !== 'GLOBAL')?.code ?? this.countries[0]?.code ?? 'GLOBAL';
      this.industryType = this.state?.industryType ?? s1.industryType ?? null;
      const requested = Number(this.routeInfo.snapshot?.queryParamMap?.get('step'));
      const resume = this.isRerun ? 1 : Math.max(1, Math.min(requested || this.state?.setupStep || 1, LAST_STEP));
      this.loadIndustries(true, () => {
        this.restoreDrafts(data);
        this.step = resume;
        this.maxStep = Math.max(resume, this.maxStep);
        this.onEnter(resume);
      });
    });
  }

  /** Step 1 — the industries of the chosen country; then the defaults. */
  loadIndustries(keepIndustry = false, then?: () => void): void {
    if (!this.countryCode) return;
    this.api.industries(this.countryCode).subscribe({
      next: r => {
        this.industries = r?.result ?? [];
        const ok = keepIndustry && this.industries.some(i => i.code === this.industryType);
        if (!ok) this.industryType = this.industries.find(i => i.code === 'Trading')?.code ?? this.industries[0]?.code ?? 'GLOBAL';
        this.loadPreview(then);
      },
      error: err => { this.previewError = onboardingError(err, 'The industries could not be loaded.'); }
    });
  }

  onCountryChange(): void {
    this.discardEdits();
    this.loadIndustries(false);
  }

  onIndustryChange(): void {
    this.discardEdits();
    this.loadPreview();
  }

  private discardEdits(): void {
    this.dirty.clear();
  }

  /** POST setup/preview for the selection; every category not edited takes the new defaults. */
  loadPreview(then?: () => void): void {
    if (!this.countryCode || !this.industryType) return;
    this.previewLoading = true;
    this.previewError = '';
    this.api.preview({ countryCode: this.countryCode, industryType: this.industryType, categories: null }).subscribe({
      next: r => {
        this.previewLoading = false;
        this.previews = r?.result ?? [];
        this.original = {};
        for (const p of this.previews) {
          this.original[p.category] = clone(p.payload);
          if (!this.dirty.has(p.category)) this.payloads[p.category] = clone(p.payload);
        }
        if (!this.warehouses.length) this.warehouses = this.defaultWarehouses();
        then?.();
      },
      error: err => { this.previewLoading = false; this.previewError = onboardingError(err, 'The defaults could not be loaded.'); }
    });
  }

  /** BR-50-002 — puts the saved drafts back over the defaults. */
  private restoreDrafts(data: Record<string, any>): void {
    const put = (cat: string, payload: any) => { if (payload) { this.payloads[cat] = clone(payload); this.dirty.add(cat); } };
    put('COA', data['3']?.payload);
    put('TAX', data['4']?.payload);
    put('CURRENCY', data['5']?.CURRENCY);
    put('FISCAL', data['5']?.FISCAL);
    if (Array.isArray(data['6']?.warehouses)) this.warehouses = clone(data['6'].warehouses);
    if (Array.isArray(data['7']?.invites)) this.invites = clone(data['7'].invites);
    if (Array.isArray(data['8']?.skip)) this.skip = new Set(data['8'].skip);
  }

  // ── Navigation & progress ─────────────────────────────────────────────────

  /** The problem that stops Continue on this step (null = may continue). */
  get stepProblem(): string | null {
    switch (this.step) {
      case 1: return !this.countryCode || !this.industryType ? 'Choose the country and industry.' : this.previewLoading ? 'Loading the defaults…' : this.previewError || null;
      case 3: return this.skip.has('COA') ? null : coaProblem(this.payloads['COA']?.accounts ?? []);
      case 4: return this.skip.has('TAX') ? null : taxProblem(this.payloads['TAX']?.taxCodes ?? []);
      case 5: return this.payloads['CURRENCY']?.base?.code ? null : 'Choose the base currency.';
      case 6: return warehouseProblem(this.warehouses);
      case 7: return inviteProblem(this.invites);
      default: return null;
    }
  }

  get canContinue(): boolean { return !this.stepProblem && this.step < LAST_STEP; }

  go(n: number): void {
    if (n < 1 || n > LAST_STEP || n === this.step || this.applying) return;
    if (n > this.step && this.stepProblem) return;
    if (n > this.maxStep + 1) return;
    this.saveStep(this.step);
    this.step = n;
    this.maxStep = Math.max(this.maxStep, n);
    this.onEnter(n);
  }

  next(): void { if (this.canContinue) this.go(this.step + 1); }
  back(): void { if (this.step > 1) this.go(this.step - 1); }

  private onEnter(n: number): void {
    if (n === 2 && !this.moduleCards.length) this.loadModules();
    if (n === 7) this.loadQbo();
  }

  /** The draft of a step, as saved to state/{step}. */
  stepData(n: number): any {
    switch (n) {
      case 1: return { countryCode: this.countryCode, industryType: this.industryType };
      case 2: return { modules: this.moduleCards.filter(m => m.isEnabled).map(m => m.code) };
      case 3: return this.dirty.has('COA') ? { payload: this.payloads['COA'] } : {};
      case 4: return this.dirty.has('TAX') ? { payload: this.payloads['TAX'] } : {};
      case 5: return {
        CURRENCY: this.dirty.has('CURRENCY') ? this.payloads['CURRENCY'] : null,
        FISCAL: this.dirty.has('FISCAL') ? this.payloads['FISCAL'] : null
      };
      case 6: return { warehouses: this.warehouses };
      case 7: return { invites: this.invites };
      case 8: return { skip: [...this.skip] };
      default: return {};
    }
  }

  saveStep(n: number): void {
    if (this.launched) return;
    this.saving = true;
    this.api.saveState(n, this.stepData(n), this.countryCode, this.industryType).subscribe({
      next: r => { this.saving = false; if (r?.result) this.state = { ...r.result, stepData: r.result.stepData ?? {} }; },
      error: err => {
        this.saving = false;
        this.messages?.add({ severity: 'warn', summary: 'Progress not saved', detail: onboardingError(err, 'Your progress on this step could not be saved.'), life: 5000 });
      }
    });
  }

  /** "Reset to default" — the step's categories go back to the previewed defaults (task 145). */
  resetStep(): void {
    for (const c of this.stepInfo.categories) {
      this.payloads[c] = clone(this.original[c]);
      this.dirty.delete(c);
    }
    if (this.step === 6) this.warehouses = this.defaultWarehouses();
    if (this.step === 7) this.invites = [];
    if (this.step === 1) { this.loadPreview(); }
    this.messages?.add({ severity: 'info', summary: 'Reset', detail: `${this.stepInfo.title} is back to the defaults.`, life: 3000 });
  }

  confirmReset(): void {
    this.confirm.confirm({
      key: 'ob-wiz', header: 'Reset to default', icon: 'pi pi-refresh', acceptLabel: 'Reset', rejectLabel: 'Cancel',
      message: `Undo your changes on "${this.stepInfo.title}" and take the defaults again?`, accept: () => this.resetStep()
    });
  }

  confirmSkip(): void {
    this.confirm.confirm({
      key: 'ob-wiz', header: 'Skip setup', icon: 'pi pi-forward', acceptLabel: 'Skip setup', rejectLabel: 'Keep going',
      message: 'Skip the wizard and load the general (GLOBAL) defaults instead? You can review or reset each category later under Settings › Organization › Default Data.',
      accept: () => this.skipSetup()
    });
  }

  skipSetup(): void {
    this.skipping = true;
    this.api.skip().subscribe({
      next: () => {
        this.skipping = false;
        this.messages?.add({ severity: 'success', summary: 'Setup skipped', detail: 'The general defaults were loaded.', life: 4000 });
        this.router.navigate(['/portal/dashboard']);
      },
      error: err => {
        this.skipping = false;
        this.messages?.add({ severity: 'error', summary: 'Not skipped', detail: onboardingError(err, 'The setup could not be skipped.'), life: 6000 });
      }
    });
  }

  // ── Step 2: modules ───────────────────────────────────────────────────────

  loadModules(): void {
    this.modulesLoading = true;
    this.modulesError = '';
    this.moduleAdmin.list().subscribe({
      next: r => { this.modulesLoading = false; this.moduleCards = (r?.result ?? []).filter(m => m.status !== 'COMING_SOON'); },
      error: err => { this.modulesLoading = false; this.modulesError = onboardingError(err, 'The modules could not be loaded.'); }
    });
  }

  moduleSwitchable(m: ModuleCard): boolean {
    return this.canManageModules && !m.isAlwaysOn && m.isLicensed && m.isAvailable && this.moduleBusy === null;
  }

  moduleNote(m: ModuleCard): string {
    if (m.isAlwaysOn) return 'Always on';
    if (!m.isLicensed) return 'Not in your plan';
    if (!m.isAvailable) return 'Not available';
    return m.isEnabled ? 'On' : 'Off';
  }

  toggleModule(m: ModuleCard, on: boolean): void {
    if (!this.moduleSwitchable(m)) return;
    this.moduleBusy = m.code;
    const call = on ? this.moduleAdmin.enable(m.code, m.rowVersion) : this.moduleAdmin.disable(m.code, { rowVersion: m.rowVersion });
    call.subscribe({
      next: r => {
        this.moduleBusy = null;
        if (r?.result) this.moduleCards = this.moduleCards.map(x => x.code === m.code ? r.result : x);
        this.modules.refresh();
        this.loadPreview();
      },
      error: err => {
        this.moduleBusy = null;
        this.moduleCards = [...this.moduleCards];
        this.messages?.add({ severity: 'error', summary: on ? 'Not switched on' : 'Not switched off', detail: onboardingError(err, 'The module could not be changed.'), life: 6000 });
        this.loadModules();
      }
    });
  }

  // ── Steps 3–5: payload edits ──────────────────────────────────────────────

  setPayload(cat: string, payload: any): void {
    this.payloads[cat] = payload;
    this.dirty.add(cat);
  }

  toggleSkip(cat: string, include: boolean): void {
    if (include) this.skip.delete(cat); else this.skip.add(cat);
    this.skip = new Set(this.skip);
  }

  /** Base currency options: the catalog, else the pack's own currencies. */
  get currencyOptions(): CurrencyOption[] {
    const pack = this.payloads['CURRENCY'];
    const fromPack: CurrencyOption[] = [pack?.base, ...(pack?.additional ?? [])].filter(Boolean);
    const all = [...this.currencyCatalog];
    for (const c of fromPack) if (!all.some(a => a.code === c.code)) all.push(c);
    return all.map(c => ({ ...c, label: `${c.code} — ${c.name}` } as any));
  }

  get baseCurrency(): string | null { return this.payloads['CURRENCY']?.base?.code ?? null; }
  get additionalCurrencies(): string[] { return (this.payloads['CURRENCY']?.additional ?? []).map((c: any) => c.code); }

  setBaseCurrency(code: string): void {
    const opt = this.currencyOptions.find(c => c.code === code);
    const cur = clone(this.payloads['CURRENCY'] ?? {});
    cur.base = { ...(cur.base ?? {}), code, name: opt?.name ?? code, symbol: opt?.symbol ?? cur.base?.symbol ?? null };
    cur.additional = (cur.additional ?? []).filter((a: any) => a.code !== code);
    this.setPayload('CURRENCY', cur);
  }

  setAdditionalCurrencies(codes: string[]): void {
    const cur = clone(this.payloads['CURRENCY'] ?? {});
    const prev: any[] = cur.additional ?? [];
    cur.additional = (codes ?? []).filter(c => c !== cur.base?.code).map(code => {
      const known = prev.find(p => p.code === code);
      if (known) return known;
      const opt = this.currencyOptions.find(c => c.code === code);
      return { code, name: opt?.name ?? code, symbol: opt?.symbol ?? null };
    });
    this.setPayload('CURRENCY', cur);
  }

  get fiscal(): any { return this.payloads['FISCAL'] ?? {}; }

  setFiscal(field: string, value: any): void {
    this.setPayload('FISCAL', { ...clone(this.payloads['FISCAL'] ?? {}), [field]: value });
  }

  // ── Step 6: warehouses ────────────────────────────────────────────────────

  get zoneTypes(): { code: string; name: string; description?: string; default?: boolean }[] {
    return this.payloads['WAREHOUSE']?.zoneTypes ?? this.original['WAREHOUSE']?.zoneTypes ?? [];
  }

  private defaultZones(): string[] {
    const z = this.zoneTypes;
    const marked = z.filter(x => x.default).map(x => x.code);
    return marked.length ? marked : z.map(x => x.code);
  }

  defaultWarehouses(): WarehouseInput[] {
    const d = this.original['WAREHOUSE']?.defaultWarehouse;
    return d ? [{ code: d.code, name: d.name, address: '', zones: this.defaultZones() }] : [];
  }

  addWarehouse(): void {
    const n = this.warehouses.length + 1;
    this.warehouses = [...this.warehouses, { code: `WH${String(n).padStart(2, '0')}`, name: '', address: '', zones: this.defaultZones() }];
  }

  removeWarehouse(i: number): void { this.warehouses = this.warehouses.filter((_, x) => x !== i); }

  hasZone(w: WarehouseInput, code: string): boolean { return (w.zones ?? []).includes(code); }

  setZone(w: WarehouseInput, code: string, on: boolean): void {
    const z = new Set(w.zones ?? []);
    if (on) z.add(code); else z.delete(code);
    w.zones = this.zoneTypes.map(t => t.code).filter(c => z.has(c));
  }

  // ── Step 7: invites & QuickBooks ──────────────────────────────────────────

  /** Task 136 — the pack's roles whose modules are all enabled. */
  get roleOptions(): { label: string; value: string }[] {
    const roles: any[] = this.payloads['ROLES']?.roles ?? [];
    const enabled = (code: string) => {
      const card = this.moduleCards.find(m => m.code === code);
      return card ? card.isEnabled : this.modules.isEnabled(code);
    };
    return roles.filter(r => roleAvailable(r, enabled)).map(r => ({ label: r.name ?? r.code, value: r.code }));
  }

  addInvite(): void {
    this.invites = [...this.invites, { email: '', firstName: '', lastName: '', roleRef: this.roleOptions[1]?.value ?? this.roleOptions[0]?.value ?? '' }];
  }

  removeInvite(i: number): void { this.invites = this.invites.filter((_, x) => x !== i); }

  get qboAvailable(): boolean {
    return this.modules.isEnabled('MODULE_INTEGRATION') && this.auth.hasAnyPermission('INTEGRATION_VIEW', 'INTEGRATION_MANAGE');
  }

  loadQbo(): void {
    if (!this.qboAvailable) return;
    this.qboLoading = true;
    this.qboError = '';
    this.qboApi.getConnection().subscribe({
      next: c => { this.qboLoading = false; this.qboStatus = { isConnected: !!c?.isConnected, companyName: c?.companyName, status: c?.status }; },
      error: err => { this.qboLoading = false; this.qboError = onboardingError(err, 'The QuickBooks status could not be read.'); }
    });
  }

  /** D-23 — the existing interactive connect flow, in a new tab; the wizard never sees Intuit credentials. */
  openQbo(): void {
    window.open('/portal/pages/integrations/quickbooks', '_blank', 'noopener');
  }

  /** Coming back from the QuickBooks tab refreshes its status. */
  @HostListener('window:focus')
  onFocus(): void { if (this.step === 7 && this.qboAvailable && !this.qboLoading) this.loadQbo(); }

  // ── Step 8: review & launch ───────────────────────────────────────────────

  /** Will this category be applied? (Not skipped, module on, defaults and a seeder exist.) */
  willApply(p: SetupCategoryPreview): boolean {
    return !this.skip.has(p.category) && p.status !== 'ModuleDisabled' && p.status !== 'NoDefaults' && p.status !== 'NoSeeder';
  }

  reviewCount(cat: string): number { return payloadCount(cat, this.payloads[cat]); }

  reviewNote(p: SetupCategoryPreview): string {
    if (p.status === 'ModuleDisabled') return 'Module off — not applied';
    if (p.status === 'NoDefaults') return 'No defaults for this country';
    if (p.status === 'NoSeeder') return 'Not available yet';
    if (this.skip.has(p.category)) return 'Skipped';
    if (p.category === 'WAREHOUSE') {
      const zones = this.warehouses.reduce((n, w) => n + (w.zones?.length ?? 0), 0);
      return `${this.warehouses.length} warehouse(s), ${zones} zone(s)`;
    }
    const present = p.preview?.alreadyPresent ?? 0;
    return (this.dirty.has(p.category) ? 'Edited · ' : '') + (present ? `${present} already present` : 'all new');
  }

  /** Everything that stops Launch, in step order. */
  get launchProblems(): string[] {
    const out: string[] = [];
    const add = (step: number, p: string | null) => { if (p) out.push(`Step ${step}: ${p}`); };
    if (!this.skip.has('COA')) add(3, coaProblem(this.payloads['COA']?.accounts ?? []));
    if (!this.skip.has('TAX')) add(4, taxProblem(this.payloads['TAX']?.taxCodes ?? []));
    add(6, warehouseProblem(this.warehouses));
    add(7, inviteProblem(this.invites));
    for (const p of this.previews) if (p.status === 'Invalid') out.push(`${categoryLabel(p.category)}: ${(p.errors ?? []).join('; ') || 'invalid defaults'}`);
    return out;
  }

  confirmLaunch(): void {
    if (this.launchProblems.length || this.applying) return;
    this.confirm.confirm({
      key: 'ob-wiz', header: 'Launch my system', icon: 'pi pi-rocket', acceptLabel: 'Launch', rejectLabel: 'Not yet',
      message: 'Create the selected defaults in your organization now? Existing records are never changed; only what is missing is added.',
      accept: () => this.launch()
    });
  }

  launch(): void {
    if (!this.countryCode || !this.industryType) return;
    this.saveStep(8);
    this.applying = true;
    this.applyError = '';
    this.applyResult = null;
    const payloadOverrides: Record<string, any> = {};
    for (const c of this.dirty) if (!this.skip.has(c)) payloadOverrides[c] = this.payloads[c];
    const wizardInputs: Record<string, any> = {};
    if (!this.skip.has('WAREHOUSE') && this.warehouses.length) {
      wizardInputs['WAREHOUSE'] = {
        warehouses: this.warehouses.map(w => ({ code: w.code.trim().toUpperCase(), name: w.name.trim(), address: w.address?.trim() || null, zones: w.zones }))
      };
    }
    this.api.apply({
      countryCode: this.countryCode,
      industryType: this.industryType,
      payloadOverrides,
      wizardInputs,
      skipCategories: [...this.skip],
      invites: this.invites.map(i => ({
        email: i.email.trim(), firstName: i.firstName?.trim() || null, lastName: i.lastName?.trim() || null, roleRef: i.roleRef
      }))
    }).subscribe({
      next: r => {
        this.applying = false;
        this.applyResult = r?.result ?? null;
        if (this.applyResult?.completed) {
          this.modules.refresh();
          this.api.loadStatus(true).subscribe();
        }
      },
      error: err => { this.applying = false; this.applyError = onboardingError(err, 'The setup could not be applied.'); }
    });
  }

  get failedResult() { return this.applyResult?.results.find(r => r.status === 'Failed' || r.status === 'Invalid') ?? null; }

  inviteTone(outcome: string): string { return outcome === 'INVITED' ? 'ok' : outcome === 'EXISTS' ? 'wn' : 'er'; }

  linkVisible(l: { module: string | null }): boolean { return !l.module || this.modules.isEnabled(l.module); }
}
