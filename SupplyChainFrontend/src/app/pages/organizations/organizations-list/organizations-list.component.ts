import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FLOW } from '../../../shared/flow';
import { Router } from '@angular/router';
import { FormsModule, ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { DropdownModule } from 'primeng/dropdown';
import { DialogModule } from 'primeng/dialog';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService, ConfirmationService } from 'primeng/api';
import {
  OrganizationsService, OrganizationListItemModel, OrgPlan, OrgUserSummary, ORG_ADMIN_ROLE_ID, baseCurrencyChange, planLabel,
  CreateOrganizationResult, OrgDefaultsCategoryResult, OrgDefaultsStatus, DefaultsRunStatus, ResendInvitationResult,
  packLevelLabel, setupStatusLabel, setupStatusSeverity
} from '../../../services/organizations.service';
import { CountriesAdminService } from '../../../services/countries-admin.service';
import { IndustriesService } from '../../../services/industries.service';
import { FeaturesAdminService } from '../../../services/features-admin.service';
import { TimezoneOption, timezoneOptions } from '../../../shared/timezones';
import { CountriesService } from '../../../services/countries.service';
import { CurrenciesService } from '../../../services/currencies.service';
import { UserService } from '../../../services/user.service';
import { FeaturesService, FeatureDefinitionModel } from '../../../services/features.service';
import { CheckboxModule } from 'primeng/checkbox';
import { forkJoin, Observable, of, Subscription, timer } from 'rxjs';
import { catchError, switchMap, tap } from 'rxjs/operators';
import { PasswordModule } from 'primeng/password';
import { isStrongPassword, passwordChecks, PasswordCheck } from '../../../shared/password-rule';

/** What the create-result dialog shows of the default-data apply: the create call's answer, overlaid by the polled status (A51 P7). */
export interface DefaultsView {
  /** Queued or running in the background: show "Applying default data…" and the per-category checklist. */
  running: boolean;
  status?: DefaultsRunStatus;
  attempted: boolean;
  completed: boolean;
  failed: boolean;
  packLevel: string;
  fallbackUsed: boolean;
  message?: string | null;
  categories: OrgDefaultsCategoryResult[];
}

@Component({
  selector: 'app-organizations-list',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ReactiveFormsModule,
    TableModule, ButtonModule, InputTextModule, InputIconModule, IconFieldModule, DropdownModule,
    DialogModule, TagModule, ToastModule, ConfirmDialogModule, TooltipModule, PasswordModule, CheckboxModule, ...FLOW
  ],
  templateUrl: './organizations-list.component.html',
  styleUrls: ['./organizations-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class OrganizationsListComponent implements OnInit, OnDestroy {
  /** A51 P7: how often the open create-result dialog asks for progress while the default data is being applied. */
  static readonly POLL_MS = 3000;
  static readonly POLL_FIRST_MS = 1500;
  /** After this many failed reads in a row the dialog stops asking and says so. */
  static readonly POLL_MAX_FAILURES = 5;

  orgs: OrganizationListItemModel[] = [];
  isLoading = true;
  showDialog = false;
  isEditing = false;
  isSaving = false;
  editId: string | null = null;
  form!: FormGroup;

  // ── Organization admin (view/change) ─────────────────────────────────────
  showAdminDialog = false;
  isLoadingAdmin = false;
  isSavingAdmin = false;
  adminOrg: OrganizationListItemModel | null = null;
  orgUsers: OrgUserSummary[] = [];
  currentAdmin: OrgUserSummary | null = null;
  selectedNewAdminId: number | null = null;

  // Resend invite (only while the admin's invite is pending or the account is not active)
  /** A51 P7: what the last resend did - whether the mail went out and, only when it did not, the link to copy. */
  resendResult: ResendInvitationResult | null = null;
  reinviteEmail = '';
  isReinviting = false;
  reinviteError = '';

  // Set password for the admin (platform super admin; the page itself is super-admin only)
  newPassword = '';
  confirmPassword = '';
  isSettingPassword = false;
  setPasswordError = '';

  // Replaced by the Plans master (active plans) once it loads; BASIC is always shown as "Default".
  planOptions: { label: string; value: OrgPlan | string }[] = [
    { label: 'Default',    value: 'BASIC' },
    { label: 'Standard',   value: 'STANDARD' },
    { label: 'Enterprise', value: 'ENTERPRISE' }
  ];

  // ── A51 P3: country, industry and timezone chosen at creation ────────────
  // Active countries of the P1 master (value = alpha-2 code). Industries are those the chosen country offers.
  newCountryOptions: { label: string; value: string; timezone: string | null }[] = [];
  industryOptions: { label: string; value: string }[] = [];
  isLoadingIndustries = false;
  industriesError = '';
  /** The zone the chosen country defaults to - offered first in the timezone list. */
  countryTimezone: string | null = null;
  /** What the server did right after creating the organization (shown in the result dialog). */
  showResultDialog = false;
  createResult: CreateOrganizationResult | null = null;
  createdOrgName = '';
  readonly packLevelLabel = packLevelLabel;
  readonly setupStatusLabel = setupStatusLabel;
  readonly setupStatusSeverity = setupStatusSeverity;

  // A51 P7: the background apply of the default data - the latest polled status and the poll itself.
  defaultsStatus: OrgDefaultsStatus | null = null;
  pollStopped = false;
  private poll: Subscription | null = null;
  private pollFailures = 0;
  inviteCopied = false;

  // A51 P7: read-only facts shown in the edit form (not editable here; changing them later does not re-run default data).
  editIndustry: string | null = null;
  editSetupStatus: string | null = null;
  editCountryCode: string | null = null;

  // Sourced from the same managed Countries list every other address form in the app uses
  // (Suppliers, Warehouses) — value is the country name itself, matching this form's existing
  // plain-string `country` field, so no id/name translation is needed on save.
  countryOptions: { label: string; value: string }[] = [];

  // The Lookups currency catalog; the value is the currency id the organization stores. Finance converts
  // foreign-currency documents into this currency (by its code), so only currencies with a code are offered.
  currencyOptions: { label: string; value: string }[] = [];
  // The base currency the organization had when the edit form opened — so emptying the field is sent as
  // an explicit clear, and leaving it empty sends nothing (the server keeps what it has).
  originalBaseCurrency: string | null = null;

  constructor(
    private fb: FormBuilder,
    private service: OrganizationsService,
    private userService: UserService,
    private countriesService: CountriesService,
    private countriesAdmin: CountriesAdminService,
    private industriesService: IndustriesService,
    private featuresAdmin: FeaturesAdminService,
    private currenciesService: CurrenciesService,
    private featuresService: FeaturesService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    private router: Router
  ) {}

  ngOnInit() {
    this.form = this.fb.group({
      orgCode:        ['', [Validators.required, Validators.maxLength(30)]],
      orgName:        ['', [Validators.required, Validators.maxLength(200)]],
      plan:           ['BASIC', Validators.required],
      contactEmail:   ['', [Validators.maxLength(150)]],
      contactPhone:   ['', [Validators.maxLength(30)]],
      address:        ['', [Validators.maxLength(500)]],
      country:        ['', [Validators.maxLength(100)]],   // edit only (the organization's country name)
      countryCode:    [null as string | null],              // create only
      industryType:   [null as string | null],              // create only; needs a country
      timeZone:       ['', [Validators.maxLength(50)]],
      baseCurrency:   [null as string | null],
      // Initial Admin — only collected at creation; the org+admin are created atomically and
      // the admin gets an email invitation to set their own password.
      adminFirstName: ['', [Validators.required, Validators.maxLength(100)]],
      adminLastName:  ['', [Validators.maxLength(100)]],
      adminEmail:     ['', [Validators.required, Validators.email, Validators.maxLength(150)]]
    });
    this.form.get('countryCode')!.valueChanges.subscribe(code => this.onCountryChanged(code));
    this.loadCountries();
    this.loadNewCountries();
    this.loadPlans();
    this.loadCurrencies();
    this.load();
  }

  private loadNewCountries() {
    this.countriesAdmin.list().subscribe({
      next: (res) => {
        this.newCountryOptions = (res.result ?? [])
          .filter(c => c.isActive && !!c.code?.trim())
          .sort((a, b) => (a.sortOrder - b.sortOrder) || a.name.localeCompare(b.name))
          .map(c => ({ label: c.name, value: c.code.trim().toUpperCase(), timezone: c.defaultTimezone?.trim() || null }));
      },
      error: () => {}
    });
  }

  private loadPlans() {
    this.featuresAdmin.listPlans().subscribe({
      next: (res) => {
        const plans = (res.result ?? []).filter(p => p.isActive).sort((a, b) => (a.sortOrder - b.sortOrder) || a.code.localeCompare(b.code));
        if (plans.length > 0)
          this.planOptions = plans.map(p => ({ label: p.code.toUpperCase() === 'BASIC' ? 'Default' : p.name, value: p.code }));
      },
      error: () => {}
    });
  }

  /** Changing the country empties the industry (its list depends on the country) and refills the timezone from the country's default. */
  private onCountryChanged(code: string | null) {
    this.form.patchValue({ industryType: null }, { emitEvent: false });
    this.industryOptions = [];
    this.industriesError = '';
    const country = code ? this.newCountryOptions.find(c => c.value === code) : undefined;
    this.countryTimezone = country?.timezone ?? null;
    this.form.patchValue({ timeZone: this.countryTimezone ?? '' }, { emitEvent: false });
    if (!code) return;

    this.isLoadingIndustries = true;
    this.industriesService.byCountry(code).subscribe({
      next: (res) => {
        // A late answer for a country that is no longer the chosen one is ignored.
        if (this.form.get('countryCode')!.value !== code) return;
        this.isLoadingIndustries = false;
        this.industryOptions = (res.result ?? []).filter(i => i.isActive).map(i => ({ label: i.name, value: i.code }));
      },
      error: () => {
        if (this.form.get('countryCode')!.value !== code) return;
        this.isLoadingIndustries = false;
        this.industriesError = 'The industries for this country could not be loaded.';
      }
    });
  }

  /** Timezone dropdown: the country's default first, then common zones, plus whatever is already chosen. */
  get timezoneChoices(): TimezoneOption[] {
    return timezoneOptions(this.countryTimezone, this.form?.get('timeZone')?.value as string | null);
  }

  /** The create call's default-data answer overlaid with the polled status; null when no country was chosen. */
  get defaultsView(): DefaultsView | null {
    const d = this.createResult?.defaults;
    if (!d) return null;
    const s = this.defaultsStatus;
    const status: DefaultsRunStatus | undefined = s?.status ?? d.status;
    return {
      running: status === 'Running',
      status,
      attempted: d.attempted,
      completed: s ? s.status === 'Completed' : d.completed,
      failed: status === 'Failed',
      packLevel: s?.packLevel ?? d.packLevel,
      fallbackUsed: s?.fallbackUsed ?? d.fallbackUsed,
      message: s?.message ?? d.message,
      categories: s?.categories ?? d.categories
    };
  }

  /** Categories that did not apply (none while it is still running). */
  get categoryProblems(): OrgDefaultsCategoryResult[] {
    const v = this.defaultsView;
    return v && !v.running ? v.categories.filter(c => !c.ok) : [];
  }

  categoryTone(c: OrgDefaultsCategoryResult): string {
    return c.ok ? (c.status === 'Applied' ? 'ok' : '') : 'er';
  }

  /** The checklist icon of a category while the apply runs: done, failed, in turn (the first pending one) or waiting. */
  checklistIcon(c: OrgDefaultsCategoryResult, index: number): string {
    if (c.status === 'Applied') return 'pi pi-check-circle';
    if (c.status === 'Failed' || c.status === 'Invalid') return 'pi pi-times-circle';
    if (c.status === 'Pending') {
      const firstPending = (this.defaultsView?.categories ?? []).findIndex(x => x.status === 'Pending');
      return firstPending === index ? 'pi pi-spin pi-spinner' : 'pi pi-circle';
    }
    return 'pi pi-minus-circle';
  }

  get defaultsHeadline(): string {
    const v = this.defaultsView;
    if (!v) return '';
    if (v.running) return 'Applying default data…';
    if (v.failed) return 'Could not apply';
    if (!v.attempted) return 'Wizard pre-filled';
    return v.completed ? 'Setup complete' : 'Needs attention';
  }

  /** Plan features left out of the new organization because they are Coming Soon (or not visible yet). */
  get skippedFeatures() {
    return this.createResult?.skippedFeatures ?? [];
  }

  get comingSoonNames(): string {
    return this.skippedFeatures.filter(f => f.reason === 'ComingSoon').map(f => f.featureName).join(', ');
  }

  // ── A51 P7: polling the background apply ──────────────────────────────────

  private startDefaultsPolling(orgId: string) {
    this.stopDefaultsPolling();
    this.pollFailures = 0;
    this.pollStopped = false;
    // One cheap read every few seconds; switchMap drops a read that is still in flight when the next tick comes.
    this.poll = timer(OrganizationsListComponent.POLL_FIRST_MS, OrganizationsListComponent.POLL_MS).pipe(
      switchMap(() => this.service.getDefaultsStatus(orgId).pipe(catchError(() => of(null))))
    ).subscribe(res => {
      if (!res?.result) {
        if (++this.pollFailures >= OrganizationsListComponent.POLL_MAX_FAILURES) { this.pollStopped = true; this.stopDefaultsPolling(); }
        return;
      }
      this.pollFailures = 0;
      this.defaultsStatus = res.result;
      if (res.result.status !== 'Running') { this.stopDefaultsPolling(); this.load(); }
    });
  }

  private stopDefaultsPolling() {
    this.poll?.unsubscribe();
    this.poll = null;
  }

  /** Closing the dialog ends the polling; the apply itself carries on in the background. */
  closeResult() {
    this.showResultDialog = false;
    this.stopDefaultsPolling();
  }

  ngOnDestroy() {
    this.stopDefaultsPolling();
  }

  /** Copies the invite link for the super admin to hand over (only ever shown when the e-mail was not sent). */
  copyInviteLink(link: string | null | undefined) {
    if (!link) return;
    const done = () => {
      this.inviteCopied = true;
      this.messageService.add({ severity: 'success', summary: 'Copied', detail: 'Invite link copied.' });
    };
    const fallback = () => {
      const el = document.createElement('textarea');
      el.value = link;
      el.style.position = 'fixed';
      el.style.opacity = '0';
      document.body.appendChild(el);
      el.select();
      try { document.execCommand('copy'); done(); } catch { this.messageService.add({ severity: 'warn', summary: 'Copy failed', detail: 'Select the link and copy it by hand.' }); }
      document.body.removeChild(el);
    };
    if (navigator.clipboard?.writeText) navigator.clipboard.writeText(link).then(done, fallback);
    else fallback();
  }

  private loadCurrencies() {
    this.currenciesService.getAll().subscribe({
      next: (res) => {
        this.currencyOptions = (res.result ?? [])
          .filter(c => !!c.code?.trim())
          .map(c => ({ label: `${c.code!.trim().toUpperCase()} — ${c.name}`, value: c.id }))
          .sort((a, b) => a.label.localeCompare(b.label));
      },
      error: () => {}
    });
  }

  /** The chosen id is not in the list (a currency since deleted or given no code): shown rather than silently dropped. */
  get baseCurrencyMissing(): boolean {
    const id = this.form?.get('baseCurrency')?.value as string | null;
    return !!id && this.currencyOptions.length > 0 && !this.currencyOptions.some(o => o.value === id);
  }

  private loadCountries() {
    this.countriesService.getAllCountries().subscribe({
      next: (res) => {
        this.countryOptions = (res.result ?? [])
          .filter(c => c.isActive)
          .map(c => ({ label: c.name, value: c.name }));
      },
      error: () => {}
    });
  }

  load() {
    this.isLoading = true;
    this.service.getList({ page: 1, pageSize: 100 }).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.orgs = res.result?.data ?? [];
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load organizations' });
      }
    });
  }

  openNew() {
    this.isEditing = false;
    this.editId = null;
    this.originalBaseCurrency = null;
    this.form.reset({ plan: 'BASIC', baseCurrency: null, countryCode: null, industryType: null }, { emitEvent: false });
    this.editIndustry = this.editSetupStatus = this.editCountryCode = null;
    this.industryOptions = [];
    this.industriesError = '';
    this.countryTimezone = null;
    this.form.get('orgCode')!.enable();
    this.form.get('plan')!.enable();
    this.form.get('adminFirstName')!.enable();
    this.form.get('adminEmail')!.enable();
    this.showDialog = true;
  }

  openEdit(org: OrganizationListItemModel) {
    this.isEditing = true;
    this.editId = org.id;
    this.service.getById(org.id).subscribe({
      next: (res) => {
        const d = res.result;
        if (!d) return;
        this.originalBaseCurrency = d.baseCurrency ?? null;
        this.editIndustry = d.industryType ?? null;
        this.editSetupStatus = d.setupStatus ?? null;
        this.editCountryCode = d.countryCode ?? null;
        this.form.patchValue({
          orgCode:      d.orgCode,
          orgName:      d.orgName,
          plan:         d.plan,
          contactEmail: d.contactEmail ?? '',
          contactPhone: d.contactPhone ?? '',
          address:      d.address ?? '',
          country:      d.country ?? '',
          timeZone:     d.timeZone ?? '',
          baseCurrency: d.baseCurrency ?? null,
          adminFirstName: '',
          adminLastName:  '',
          adminEmail:     ''
        });
        // Org code and plan are changed via their own dedicated actions, not this edit form —
        // plan changes deliberately don't touch feature toggles, so they're a separate flow.
        // The initial Admin is only collected here at creation time; changing who the admin is
        // afterward is the separate "Manage Admin" dialog (manageAdmin/saveAdmin below), not this form.
        this.form.get('orgCode')!.disable();
        this.form.get('plan')!.disable();
        this.form.get('adminFirstName')!.disable();
        this.form.get('adminEmail')!.disable();
        this.showDialog = true;
      },
      error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load organization' })
    });
  }

  // ── Features chosen at creation ──────────────────────────────────────────
  showFeaturesDialog = false;
  moduleChoices: FeatureDefinitionModel[] = [];
  selectedModules = new Set<string>();
  private planDefaults = new Map<string, Set<string>>();

  /** Create step 1 → 2: the profile is valid, so ask which features the organization gets before anything is saved. */
  private openFeaturePicker() {
    const load$: Observable<unknown> = this.moduleChoices.length
      ? of(null)
      : forkJoin([this.featuresService.getCatalog(), this.featuresService.getPlanTemplates()]).pipe(
          tap(([catalog, templates]) => {
            this.moduleChoices = (catalog.result ?? [])
              .filter(f => f.category === 'MODULE' && !f.isCore && f.isAvailable !== false)
              .sort((a, b) => a.displayOrder - b.displayOrder);
            this.planDefaults = new Map((templates.result ?? []).map(t =>
              [t.plan.toUpperCase(), new Set(t.features.filter(x => x.isEnabledByDefault).map(x => x.featureCode))]));
          }));
    load$.subscribe({
      next: () => {
        this.useAllPlanDefaults();
        this.showFeaturesDialog = true;
      },
      error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load the feature list' })
    });
  }

  /** Tick what the chosen plan includes by default; the super admin then adjusts. */
  useAllPlanDefaults() {
    const defaults = this.planDefaults.get(String(this.form.get('plan')?.value ?? '').toUpperCase());
    this.selectedModules = new Set(this.moduleChoices
      .filter(m => !this.isComingSoon(m) && defaults?.has(m.featureCode)).map(m => m.featureCode));
  }

  /** A51 P8: Coming Soon / hidden features are not licensed at creation (the server skips them too). */
  isComingSoon(m: FeatureDefinitionModel): boolean {
    return m.status === 'ComingSoon' || m.isVisible === false;
  }

  clearModules() { this.selectedModules = new Set(); }

  toggleModule(code: string, on: boolean) {
    const next = new Set(this.selectedModules);
    const m = this.moduleChoices.find(x => x.featureCode === code);
    if (on && m && this.isComingSoon(m)) return;
    if (on) next.add(code); else next.delete(code);
    this.selectedModules = next;
  }

  confirmCreate() {
    this.showFeaturesDialog = false;
    this.createOrEdit();
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    if (!this.isEditing) { this.openFeaturePicker(); return; }
    this.createOrEdit();
  }

  private createOrEdit() {
    this.isSaving = true;
    const v = this.form.getRawValue();

    const onNext = (res: { success: boolean; message: string; result?: unknown }) => {
      this.isSaving = false;
      if (res.success) {
        this.messageService.add({
          severity: 'success', summary: 'Saved',
          detail: this.isEditing ? 'Organization updated' : 'Organization created'
        });
        this.showDialog = false;
        // A51 P3: a country was chosen, so the server also applied (or tried to apply) its default data - show what happened.
        // A51 P7: the apply runs in the background - the dialog says so and polls until it ends. It is also shown when only the
        // invitation outcome or skipped (Coming Soon) features need saying.
        const created = this.isEditing ? null : (res.result as CreateOrganizationResult | null | undefined);
        if (created?.defaults || created?.invitation || created?.skippedFeatures?.length) {
          this.createResult = created;
          this.createdOrgName = v.orgName;
          this.defaultsStatus = null;
          this.inviteCopied = false;
          this.showResultDialog = true;
          if (created.defaults?.status === 'Running' && created.organizationId) this.startDefaultsPolling(created.organizationId);
        }
        this.load();
      } else {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Save failed' });
      }
    };
    const onError = (err: any) => {
      this.isSaving = false;
      this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Save failed' });
    };

    if (this.isEditing && this.editId) {
      this.service.update(this.editId, {
        orgName:      v.orgName,
        contactEmail: v.contactEmail || undefined,
        contactPhone: v.contactPhone || undefined,
        address:      v.address || undefined,
        country:      v.country || undefined,
        timeZone:     v.timeZone || undefined,
        ...baseCurrencyChange(this.originalBaseCurrency, v.baseCurrency)
      }).subscribe({ next: onNext, error: onError });
    } else {
      this.service.create({
        moduleCodes:    [...this.selectedModules],
        orgCode:        v.orgCode,
        orgName:        v.orgName,
        plan:           v.plan,
        contactEmail:   v.contactEmail || undefined,
        contactPhone:   v.contactPhone || undefined,
        address:        v.address || undefined,
        countryCode:    v.countryCode || undefined,
        industryType:   v.countryCode && v.industryType ? v.industryType : undefined,
        timeZone:       v.timeZone || undefined,
        baseCurrency:   v.baseCurrency || undefined,
        adminFirstName: v.adminFirstName,
        adminLastName:  v.adminLastName || '',
        adminEmail:     v.adminEmail
      }).subscribe({ next: onNext, error: onError });
    }
  }

  // Reactivating a suspended org is a routine, low-risk action.
  confirmActivate(org: OrganizationListItemModel) {
    this.confirmationService.confirm({
      message: `Activate <strong>${org.orgName}</strong>?`,
      header: 'Activate Organization',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Activate',
      acceptButtonStyleClass: 'p-button-success',
      rejectLabel: 'Cancel',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.service.patchStatus(org.id, true).subscribe({
          next: () => {
            this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Organization activated' });
            this.load();
          },
          error: (err) => this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Update failed' })
        });
      }
    });
  }

  // Deactivation is a distinct, more consequential action: it immediately terminates every
  // active session for this org's users, so the confirmation calls that out explicitly.
  confirmDeactivate(org: OrganizationListItemModel) {
    this.confirmationService.confirm({
      message: `Deactivate <strong>${org.orgName}</strong>? This will immediately sign out all of its users and block further logins.`,
      header: 'Deactivate Organization',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Deactivate',
      acceptButtonStyleClass: 'p-button-danger',
      rejectLabel: 'Cancel',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.service.deactivate(org.id).subscribe({
          next: () => {
            this.messageService.add({ severity: 'success', summary: 'Deactivated', detail: 'Organization deactivated; all active sessions were terminated.' });
            this.load();
          },
          error: (err) => this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Update failed' })
        });
      }
    });
  }

  manageFeatures(org: OrganizationListItemModel) {
    this.router.navigate(['/portal/pages/organizations', org.id, 'features']);
  }

  // Opens the view/change-admin dialog. "The org admin" has no dedicated column on Organization —
  // it's derived here as whichever active user in the org holds the OrgAdmin role.
  manageAdmin(org: OrganizationListItemModel) {
    this.adminOrg = org;
    this.orgUsers = [];
    this.currentAdmin = null;
    this.selectedNewAdminId = null;
    this.reinviteEmail = '';
    this.reinviteError = '';
    this.resendResult = null;
    this.inviteCopied = false;
    this.clearPasswordFields();
    this.isLoadingAdmin = true;
    this.showAdminDialog = true;
    this.loadOrgUsers(org.id);
  }

  private loadOrgUsers(orgId: string) {
    this.service.getOrgUsers(orgId).subscribe({
      next: (res) => {
        this.isLoadingAdmin = false;
        this.orgUsers = res.result ?? [];
        this.currentAdmin = this.orgUsers.find(u => u.roleId === ORG_ADMIN_ROLE_ID) ?? null;
        this.reinviteEmail = this.currentAdmin?.email ?? '';
      },
      error: (err) => {
        this.isLoadingAdmin = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to load organization users' });
      }
    });
  }

  // ── Admin status / resend invite / set password ──────────────────────────────────────

  /** Pending invite wins over the active flag: an invited admin has no password yet. */
  get adminStatus(): 'pending' | 'active' | 'inactive' | null {
    const a = this.currentAdmin;
    if (!a) return null;
    if (a.invitePending) return 'pending';
    return a.isActive ? 'active' : 'inactive';
  }

  /** The invite expiry as a Date; the server sends UTC, so a string without a zone is read as UTC. */
  get inviteExpiry(): Date | null {
    const raw = this.currentAdmin?.inviteExpiresAt;
    if (!raw) return null;
    const d = new Date(/(?:[zZ]|[+-]\d\d:?\d\d)$/.test(raw) ? raw : raw + 'Z');
    return isNaN(d.getTime()) ? null : d;
  }

  get inviteExpired(): boolean {
    const d = this.inviteExpiry;
    return !!d && d.getTime() < Date.now();
  }

  get showReinvite(): boolean {
    return !!this.currentAdmin && (!!this.currentAdmin.invitePending || !this.currentAdmin.isActive);
  }

  sendReinvite() {
    if (!this.adminOrg || !this.currentAdmin || this.isReinviting) return;
    const email = this.reinviteEmail.trim();
    if (!email) { this.reinviteError = 'Enter the e-mail address to send the invite to.'; return; }
    const orgId = this.adminOrg.id;
    const userId = this.currentAdmin.userId;
    const changed = email.toLowerCase() !== (this.currentAdmin.email ?? '').toLowerCase();
    this.reinviteError = '';
    this.isReinviting = true;
    this.service.resendInvitation(orgId, userId, changed ? email : null).subscribe({
      next: (res) => {
        this.isReinviting = false;
        if (!res.success) { this.reinviteError = res.message || 'The invite was not sent.'; return; }
        const outcome = res.result;
        this.resendResult = outcome ?? null;
        this.inviteCopied = false;
        if (outcome?.emailSent !== false)
          this.messageService.add({ severity: 'success', summary: 'Invitation email sent', detail: res.message || `A new invitation was sent to ${email}` });
        else
          this.messageService.add({ severity: 'warn', summary: 'Invitation email NOT sent', detail: 'Copy the invite link and send it to the admin yourself.' });
        const updated = outcome?.user;
        if (updated && this.currentAdmin?.userId === updated.userId) {
          this.currentAdmin = { ...this.currentAdmin, ...updated };
          this.orgUsers = this.orgUsers.map(u => u.userId === updated.userId ? { ...u, ...updated } : u);
          this.reinviteEmail = updated.email;
        } else {
          this.loadOrgUsers(orgId);
        }
      },
      error: (err) => {
        this.isReinviting = false;
        this.reinviteError = err?.error?.message || 'The invite was not sent.';
      }
    });
  }

  get passwordChecks(): PasswordCheck[] {
    return passwordChecks(this.newPassword);
  }

  get passwordsMatch(): boolean {
    return !!this.confirmPassword && this.newPassword === this.confirmPassword;
  }

  get canSetPassword(): boolean {
    return !!this.currentAdmin && isStrongPassword(this.newPassword) && this.passwordsMatch && !this.isSettingPassword;
  }

  setAdminPassword() {
    if (!this.adminOrg || !this.currentAdmin || !this.canSetPassword) return;
    const orgId = this.adminOrg.id;
    this.setPasswordError = '';
    this.isSettingPassword = true;
    this.userService.setUserPassword(this.currentAdmin.userId, this.newPassword).subscribe({
      next: (res) => {
        this.isSettingPassword = false;
        if (!res.success) { this.setPasswordError = res.message || 'The password was not set.'; return; }
        this.messageService.add({ severity: 'success', summary: 'Password set', detail: res.message || 'Password set.' });
        this.clearPasswordFields();
        this.loadOrgUsers(orgId);
      },
      error: (err) => {
        this.isSettingPassword = false;
        this.setPasswordError = err?.error?.message || 'The password was not set.';
      }
    });
  }

  private clearPasswordFields() {
    this.newPassword = '';
    this.confirmPassword = '';
    this.setPasswordError = '';
  }

  // Candidates to promote — everyone active in the org except whoever already holds OrgAdmin.
  get adminCandidates(): OrgUserSummary[] {
    return this.orgUsers.filter(u => u.isActive && u.roleId !== ORG_ADMIN_ROLE_ID);
  }

  saveAdmin() {
    if (!this.adminOrg || !this.selectedNewAdminId) return;
    this.isSavingAdmin = true;
    this.service.updateAdmin(this.adminOrg.id, this.selectedNewAdminId).subscribe({
      next: () => {
        this.isSavingAdmin = false;
        this.showAdminDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Organization admin updated.' });
      },
      error: (err) => {
        this.isSavingAdmin = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to update organization admin' });
      }
    });
  }

  readonly planLabel = planLabel;

  planSeverity(plan: string): 'success' | 'info' | 'warn' {
    switch (plan) {
      case 'ENTERPRISE': return 'success';
      case 'STANDARD':   return 'info';
      default:           return 'warn';
    }
  }

  get f() { return this.form.controls; }
}
