import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { SelectModule } from 'primeng/select';
import { DatePickerModule } from 'primeng/datepicker';
import { CheckboxModule } from 'primeng/checkbox';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  CarrierAccountModel,
  CarrierCredentialModel,
  CarrierDetailModel,
  CarrierIntegrationModel,
  CourierProviderModel,
  CARRIER_CAPABILITIES
} from '../../../../services/logistics.service';

/** Null means "whatever the adapter says"; the tri-state the server actually stores. */
type Override = boolean | null;

/**
 * Configuring how a carrier is dealt with: which contracts exist, which one bookings go out on,
 * what each may be asked for, and the secrets it authenticates with.
 *
 * The screen's job is to make three things obvious that are easy to get wrong: which account is
 * the default, whether an account is a sandbox, and whether a capability is off because the
 * carrier cannot do it or because somebody switched it off here.
 */
@Component({
  selector: 'app-carrier-accounts',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, TooltipModule, ToastModule, DialogModule,
    InputTextModule, TextareaModule, SelectModule, DatePickerModule, CheckboxModule
  ],
  templateUrl: './carrier-accounts.component.html',
  styleUrls: ['./carrier-accounts.component.scss'],
  providers: [MessageService]
})
export class CarrierAccountsComponent implements OnInit {
  carrierUuid = '';
  carrier: CarrierDetailModel | null = null;
  accounts: CarrierAccountModel[] = [];

  isLoading = true;
  notFound = false;
  isSubmitting = false;

  capabilityMeta = CARRIER_CAPABILITIES;

  // ── How this carrier is booked ──────────────────────────────────────────────

  integration: CarrierIntegrationModel | null = null;
  providers: CourierProviderModel[] = [];
  integrationForm = { mode: 'MANUAL', providerKey: null as string | null };

  modeOptions = [
    { label: 'Manual — a person books it and keys in the airway bill', value: 'MANUAL' },
    { label: 'API — an adapter books it with the carrier', value: 'API' }
  ];

  /** Credentials per account, loaded on demand when a panel is opened. */
  credentials: Record<string, CarrierCredentialModel[]> = {};
  expanded: Record<string, boolean> = {};

  // ── Account dialog ──────────────────────────────────────────────────────────

  accountDialogVisible = false;
  editingUuid: string | null = null;
  form = this.emptyForm();

  // ── Credential dialog ───────────────────────────────────────────────────────

  credentialDialogVisible = false;
  credentialAccount: CarrierAccountModel | null = null;
  credentialForm = { key: '', value: '', description: '', expiresAt: null as Date | null };

  constructor(
    private route: ActivatedRoute,
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.carrierUuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  load() {
    if (!this.carrierUuid) { this.isLoading = false; this.notFound = true; return; }

    this.isLoading = true;

    this.logisticsService.getCarrierById(this.carrierUuid).subscribe({
      next: (res) => {
        this.carrier = res.result ?? null;
        this.notFound = !this.carrier;
        this.loadAccounts();
        if (this.carrier) this.loadIntegration();
      },
      error: (err) => {
        this.isLoading = false;
        this.carrier = null;
        this.notFound = err?.status === 404;
        if (!this.notFound) this.fail(err, 'Failed to load the carrier.');
      }
    });
  }

  // ── Which adapter books this carrier ────────────────────────────────────────

  /**
   * Both reads are side panels: a failure to load either must not take the accounts down with it, and
   * the card simply stays empty. The carrier row is what the screen is really about.
   */
  private loadIntegration() {
    this.logisticsService.getCarrierIntegration(this.carrierUuid).subscribe({
      next: (res) => {
        this.integration = res.result ?? null;
        this.integrationForm = {
          mode: this.integration?.integrationMode === 'API' ? 'API' : 'MANUAL',
          providerKey: this.integration?.integrationMode === 'API' ? this.integration.providerKey ?? null : null
        };
      },
      error: () => this.integration = null
    });

    if (this.providers.length) return;

    this.logisticsService.getCourierProviders().subscribe({
      next: (res) => this.providers = res.result ?? [],
      error: () => this.providers = []
    });
  }

  get providerOptions() {
    return this.providers.map(p => ({ label: p.displayName, value: p.key }));
  }

  /** The adapter picked in the form, so the credentials it will need can be listed before anything is saved. */
  get selectedProvider(): CourierProviderModel | null {
    return this.integrationForm.mode === 'API'
      ? this.providers.find(p => p.key === this.integrationForm.providerKey) ?? null
      : null;
  }

  /** Something to save: a real change, and an adapter named when an adapter is what was asked for. */
  get canSaveIntegration(): boolean {
    if (this.isSubmitting || !this.integration) return false;

    if (this.integrationForm.mode === 'API' && !this.integrationForm.providerKey) return false;

    const wasApi = this.integration.integrationMode === 'API';
    const isApi  = this.integrationForm.mode === 'API';

    return wasApi !== isApi
        || (isApi && this.integration.providerKey !== this.integrationForm.providerKey);
  }

  saveIntegration() {
    if (!this.canSaveIntegration) return;
    this.isSubmitting = true;

    this.logisticsService.setCarrierIntegration(this.carrierUuid, {
      integrationMode: this.integrationForm.mode,
      providerKey: this.integrationForm.mode === 'API' ? this.integrationForm.providerKey ?? undefined : undefined
    }).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        this.integration = res.result ?? this.integration;
        this.ok('Integration saved.');
        // The accounts panel says which adapter each account resolves to, and it just changed.
        this.loadAccounts();
      },
      error: (err) => {
        this.isSubmitting = false;
        // The server explains — "2 consignment(s) are booked through …" — and that is the answer.
        this.fail(err, 'The integration could not be changed.');
      }
    });
  }

  private loadAccounts() {
    this.logisticsService.getCarrierAccounts(this.carrierUuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.accounts = res.result ?? [];
        // Reloading drops cached credentials rather than showing stale keys against a changed
        // account — the panel refetches when it is next opened.
        this.credentials = {};
      },
      error: (err) => {
        this.isLoading = false;
        this.accounts = [];
        this.fail(err, 'Failed to load the accounts.');
      }
    });
  }

  // ── Display ─────────────────────────────────────────────────────────────────

  /**
   * Which adapter books this carrier, taken from the accounts rather than the carrier row.
   *
   * `CarrierDetailModel` deliberately does not expose `integrationMode` or `providerKey` — a
   * contract test guards that, so the Phase 2 columns never leak into the response the legacy
   * carrier screens read. The account-level answer is better anyway: the server resolved it
   * through the same logic the booking flow uses, so it reflects what would actually happen.
   */
  get providerSummary(): string | null {
    // Null when there are no accounts, or when none resolved an adapter — the empty state and
    // the warning banner respectively already say why.
    return this.accounts.find(a => a.providerDisplayName)?.providerDisplayName ?? null;
  }

  /** A carrier with no adapter can hold accounts but nothing can book on them. */
  get hasBrokenProvider(): boolean {
    return this.accounts.some(a => !!a.providerWarning);
  }

  capabilityLabel(code: string): string {
    return this.capabilityMeta.find(c => c.code === code)?.label ?? code;
  }

  capabilityHint(code: string): string {
    return this.capabilityMeta.find(c => c.code === code)?.hint ?? '';
  }

  /**
   * Why a capability is in the state it is. The distinction the screen exists to make: "the
   * carrier cannot do this" is a fact, "somebody switched it off" is a decision.
   */
  capabilityReason(cap: { supportedByProvider: boolean; enabledOnAccount: boolean | null }): string {
    if (!cap.supportedByProvider) return 'The adapter cannot do this';
    if (cap.enabledOnAccount === false) return 'Switched off for this account';
    return 'Available';
  }

  // ── Account dialog ──────────────────────────────────────────────────────────

  openCreate() {
    this.editingUuid = null;
    this.form = this.emptyForm();
    this.accountDialogVisible = true;
  }

  openEdit(account: CarrierAccountModel) {
    this.editingUuid = account.uuid;

    this.form = {
      accountName: account.accountName,
      accountNumber: account.accountNumber ?? '',
      defaultServiceCode: account.defaultServiceCode ?? '',
      isSandbox: account.isSandbox,
      notes: account.notes ?? '',
      overrides: Object.fromEntries(
        account.capabilities.map(c => [c.name, c.enabledOnAccount])
      ) as Record<string, Override>
    };

    this.accountDialogVisible = true;
  }

  get canSaveAccount(): boolean {
    return !this.isSubmitting && !!this.form.accountName.trim();
  }

  saveAccount() {
    if (!this.canSaveAccount) return;

    this.isSubmitting = true;

    const editing = this.editingUuid;

    // Two calls rather than one observable chosen by a ternary: create and patch return different
    // result types, and a union of them is not callable.
    const done = () => {
      this.isSubmitting = false;
      this.accountDialogVisible = false;
      this.ok(editing ? 'Account updated.' : 'Account added.');
      this.load();
    };

    const failed = (err: any) => {
      this.isSubmitting = false;
      this.fail(err, 'The account could not be saved.');
    };

    if (editing) {
      this.logisticsService.patchCarrierAccount(editing, {
        accountName:        this.form.accountName.trim(),
        accountNumber:      this.form.accountNumber.trim(),
        defaultServiceCode: this.form.defaultServiceCode.trim(),
        isSandbox:          this.form.isSandbox,
        notes:              this.form.notes.trim(),
        // Only the explicit "off" switches are sent as values; anything handed back to the
        // adapter goes through clearOverrides, because null on a patch means "leave alone".
        ...this.overridesToPatch()
      }).subscribe({ next: done, error: failed });
    } else {
      this.logisticsService.createCarrierAccount({
        carrierUuid:        this.carrierUuid,
        accountName:        this.form.accountName.trim(),
        accountNumber:      this.form.accountNumber.trim() || undefined,
        defaultServiceCode: this.form.defaultServiceCode.trim() || undefined,
        isSandbox:          this.form.isSandbox,
        notes:              this.form.notes.trim() || undefined,
        ...this.overridesToCreate()
      }).subscribe({ next: done, error: failed });
    }
  }

  /** Off switches as values; everything else cleared back to the adapter. */
  private overridesToPatch() {
    const off = (code: string) => this.form.overrides[code] === false;
    const cleared = this.capabilityMeta.filter(c => !off(c.code)).map(c => c.code);

    return {
      codEnabled:           off('COD')            ? false : undefined,
      labelsEnabled:        off('LABELS')         ? false : undefined,
      trackingEnabled:      off('TRACKING')       ? false : undefined,
      cancellationEnabled:  off('CANCELLATION')   ? false : undefined,
      pickupBookingEnabled: off('PICKUP_BOOKING') ? false : undefined,
      clearOverrides:       cleared.length ? cleared : undefined
    };
  }

  private overridesToCreate() {
    const off = (code: string) => this.form.overrides[code] === false;

    return {
      codEnabled:           off('COD')            ? false : undefined,
      labelsEnabled:        off('LABELS')         ? false : undefined,
      trackingEnabled:      off('TRACKING')       ? false : undefined,
      cancellationEnabled:  off('CANCELLATION')   ? false : undefined,
      pickupBookingEnabled: off('PICKUP_BOOKING') ? false : undefined
    };
  }

  makeDefault(account: CarrierAccountModel) {
    if (this.isSubmitting || account.isDefault) return;
    this.patch(account, { isDefault: true }, 'Default account changed.');
  }

  setActive(account: CarrierAccountModel, isActive: boolean) {
    this.patch(account, { isActive }, isActive ? 'Account activated.' : 'Account deactivated.');
  }

  private patch(account: CarrierAccountModel, body: object, message: string) {
    if (this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.patchCarrierAccount(account.uuid, body).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.ok(message);
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The change could not be saved.');
      }
    });
  }

  remove(account: CarrierAccountModel) {
    if (this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.deleteCarrierAccount(account.uuid).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.ok('Account removed.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The account could not be removed.');
      }
    });
  }

  // ── Credentials ─────────────────────────────────────────────────────────────

  toggleCredentials(account: CarrierAccountModel) {
    this.expanded[account.uuid] = !this.expanded[account.uuid];
    if (this.expanded[account.uuid]) this.loadCredentials(account);
  }

  private loadCredentials(account: CarrierAccountModel) {
    this.logisticsService.getCarrierCredentials(account.uuid).subscribe({
      next: (res) => this.credentials[account.uuid] = res.result ?? [],
      error: (err) => this.fail(err, 'Failed to load the credentials.')
    });
  }

  credentialsFor(account: CarrierAccountModel): CarrierCredentialModel[] {
    return this.credentials[account.uuid] ?? [];
  }

  openCredential(account: CarrierAccountModel, existingKey?: string) {
    this.credentialAccount = account;
    // Replacing rather than editing: the value cannot be read back, so the only thing a "key
    // already set" form can offer is a new value for it.
    this.credentialForm = { key: existingKey ?? '', value: '', description: '', expiresAt: null };
    this.credentialDialogVisible = true;
  }

  get canSaveCredential(): boolean {
    return !this.isSubmitting
        && !!this.credentialForm.key.trim()
        && !!this.credentialForm.value.trim();
  }

  saveCredential() {
    if (!this.canSaveCredential || !this.credentialAccount) return;

    const account = this.credentialAccount;
    this.isSubmitting = true;

    this.logisticsService.setCarrierCredential(account.uuid, {
      key:         this.credentialForm.key.trim(),
      value:       this.credentialForm.value,
      description: this.credentialForm.description.trim() || undefined,
      expiresAt:   this.credentialForm.expiresAt?.toISOString()
    }).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.credentialDialogVisible = false;
        // Cleared immediately: the secret has no reason to stay in browser memory, and the form
        // is reopened blank precisely because it cannot be read back.
        this.credentialForm = { key: '', value: '', description: '', expiresAt: null };
        this.ok('Credential saved.');
        this.loadCredentials(account);
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The credential could not be saved.');
      }
    });
  }

  removeCredential(account: CarrierAccountModel, credential: CarrierCredentialModel) {
    if (this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.removeCarrierCredential(account.uuid, credential.key).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.ok('Credential removed.');
        this.loadCredentials(account);
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The credential could not be removed.');
      }
    });
  }

  hasExpiredCredential(account: CarrierAccountModel): boolean {
    return this.credentialsFor(account).some(c => c.isExpired);
  }

  // ── Helpers ─────────────────────────────────────────────────────────────────

  private emptyForm() {
    return {
      accountName: '',
      accountNumber: '',
      defaultServiceCode: '',
      isSandbox: false,
      notes: '',
      overrides: Object.fromEntries(
        CARRIER_CAPABILITIES.map(c => [c.code, null])
      ) as Record<string, Override>
    };
  }

  private ok(detail: string) {
    this.messageService.add({ severity: 'success', summary: 'Done', detail });
  }

  private fail(err: any, fallback: string) {
    this.messageService.add({
      severity: 'error',
      summary: 'Not allowed',
      // The server explains its refusals — "'Primary' is this carrier's default account" — and
      // that is far more use than a generic failure.
      detail: err?.error?.message ?? fallback
    });
  }
}
