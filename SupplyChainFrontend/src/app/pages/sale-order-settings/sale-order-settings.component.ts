import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { MessageModule } from 'primeng/message';
import { PaginatorModule, PaginatorState } from 'primeng/paginator';
import { TableModule } from 'primeng/table';
import { TabViewModule, TabViewChangeEvent } from 'primeng/tabview';
import { TagModule } from 'primeng/tag';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import {
  SaleOrderConfigService, SaleOrderConfigModel, SaleOrderConfigAuditModel, DepartmentOptionModel
} from '../../services/sale-order-config.service';
import { AuthService } from '../service/auth.service';
import {
  APPROVAL_MODE_OPTIONS, ChoiceOption, FULFILMENT_MODE_OPTIONS, ImpactNote, MAX_CC_LENGTH, MAX_RESERVATION_HOURS,
  MIN_RESERVATION_HOURS, SUPPLIER_SELECTION_OPTIONS, SettingChange, SettingsValue,
  departmentLabel, describeHours, diffSettings, displayAuditValue, dropShipDefaultValidator, emailListValidator,
  fromModel, impactNotes, pickupDefaultValidator, settingLabel, toRequest
} from './sale-order-settings.shared';

const WRITE_PERMISSION = 'SALE_ORDER_CONFIG_WRITE';

/** The settings that only matter while purchase orders are raised automatically. */
const AUTO_PO_DEPENDENTS = ['supplierSelectionMode', 'autoPoApprovalMode'];

@Component({
  selector: 'app-sale-order-settings',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule,
    ButtonModule, DialogModule, DropdownModule, InputNumberModule, MessageModule, PaginatorModule, TableModule,
    TabViewModule, TagModule, TextareaModule, ToastModule, ToggleSwitchModule, TooltipModule
  ],
  templateUrl: './sale-order-settings.component.html',
  styleUrls: ['./sale-order-settings.component.scss'],
  providers: [MessageService]
})
export class SaleOrderSettingsComponent implements OnInit {
  readonly supplierOptions = SUPPLIER_SELECTION_OPTIONS;
  readonly approvalOptions = APPROVAL_MODE_OPTIONS;
  readonly fulfilmentOptions = FULFILMENT_MODE_OPTIONS;
  readonly minHours = MIN_RESERVATION_HOURS;
  readonly maxHours = MAX_RESERVATION_HOURS;
  readonly maxCc = MAX_CC_LENGTH;
  readonly auditPageSize = 20;

  isLoading = true;
  loadFailed = false;
  isSaving = false;

  /** As last saved on the server. */
  config: SaleOrderConfigModel | null = null;
  private baseline: SettingsValue | null = null;

  departments: DepartmentOptionModel[] = [];
  departmentsLoaded = false;
  departmentsFailed = false;
  departmentOptions: { label: string; value: number }[] = [];

  /** What differs from what is saved, kept up to date as the form is edited. */
  changes: SettingChange[] = [];

  reviewVisible = false;
  reviewChanges: SettingChange[] = [];
  reviewNotes: ImpactNote[] = [];

  activeTab = 0;
  audit: SaleOrderConfigAuditModel[] = [];
  auditTotal = 0;
  auditPage = 1;
  isLoadingAudit = false;
  auditFailed = false;
  private auditLoaded = false;

  form: FormGroup;

  /** Drop ship cannot be the default while drop shipping is off. */
  readonly dropShipBlocked = (option: ChoiceOption): boolean =>
    option.value === 'DROP_SHIP' && !this.form.get('dropShipEnabled')?.value;

  constructor(
    private fb: FormBuilder,
    private service: SaleOrderConfigService,
    private authService: AuthService,
    private messageService: MessageService
  ) {
    this.form = this.fb.group({
      autoPoEnabled:             [true],
      supplierSelectionMode:     ['BEST_MATCH'],
      autoPoApprovalMode:        ['REQUIRE_WORKFLOW'],
      defaultFulfillmentMode:    ['IN_STOCK'],
      dropShipEnabled:           [false],
      selfPickupEnabled:         [true],
      partialFulfillmentAllowed: [true],
      shipmentRequiredDefault:   [true],
      reservationTtlHours:       [72 as number | null,
        [Validators.required, Validators.min(MIN_RESERVATION_HOURS), Validators.max(MAX_RESERVATION_HOURS)]],
      emailIntimationEnabled:    [true],
      intimationDepartmentId:    [null as number | null],
      intimationCcEmails:        ['', [emailListValidator]]
    }, { validators: [dropShipDefaultValidator, pickupDefaultValidator] });

    this.form.valueChanges.subscribe(() => this.onFormChanged());
  }

  ngOnInit() {
    this.load();
  }

  // ── State ───────────────────────────────────────────────────────────────────

  get canEdit(): boolean { return this.authService.hasPermission(WRITE_PERMISSION); }

  get value(): SettingsValue { return this.form.getRawValue() as SettingsValue; }

  get dirty(): boolean { return this.changes.length > 0; }

  get autoPoOn(): boolean { return !!this.form.get('autoPoEnabled')?.value; }

  /** "3 days" beside "72 hours", once the hours are more than a couple of days. */
  get holdInDays(): string {
    const hours = this.form.get('reservationTtlHours')?.value as number | null;
    return hours !== null && hours !== undefined && hours >= 48 ? describeHours(hours) : '';
  }

  /** The chosen department has nobody at its head, so an email to it goes nowhere. */
  get chosenDepartmentHasNoHead(): boolean {
    const id = this.form.get('intimationDepartmentId')?.value as number | null;
    const found = id === null || id === undefined ? undefined : this.departments.find(d => d.departmentId === id);
    return !!found && !found.hasHead;
  }

  get noDepartments(): boolean { return this.departmentsLoaded && this.departments.length === 0; }

  // ── Loading ─────────────────────────────────────────────────────────────────

  load() {
    this.isLoading = true;
    this.loadFailed = false;

    this.service.getConfig().subscribe({
      next: (res) => {
        this.isLoading = false;
        if (!res.success || !res.result) { this.loadFailed = true; return; }
        this.applyConfig(res.result);
      },
      error: () => {
        this.isLoading = false;
        this.loadFailed = true;
      }
    });

    this.loadDepartments();
  }

  private loadDepartments() {
    this.service.getDepartments().subscribe({
      next: (res) => {
        this.departments = res.result ?? [];
        this.departmentsLoaded = true;
        this.departmentsFailed = false;
        this.rebuildDepartmentOptions();
        this.onFormChanged();
      },
      error: () => {
        this.departmentsFailed = true;
        this.rebuildDepartmentOptions();
      }
    });
  }

  /** Puts a saved policy on the page and makes it the thing "unsaved" is measured against. */
  private applyConfig(model: SaleOrderConfigModel) {
    this.config = model;
    this.baseline = fromModel(model);
    this.resetToBaseline();
  }

  private resetToBaseline() {
    if (!this.baseline) return;
    this.form.patchValue(this.baseline, { emitEvent: false });
    this.form.markAsPristine();
    this.form.markAsUntouched();
    this.applyPermission();
    this.rebuildDepartmentOptions();
    this.changes = [];
  }

  /** Read-only for anyone without the write permission; the two auto purchase order choices follow their switch. */
  private applyPermission() {
    if (!this.canEdit) {
      this.form.disable({ emitEvent: false });
      return;
    }
    this.form.enable({ emitEvent: false });
    this.syncDependents();
  }

  private syncDependents() {
    if (!this.canEdit) return;
    for (const name of AUTO_PO_DEPENDENTS) {
      const control = this.form.get(name)!;
      if (this.autoPoOn && control.disabled) control.enable({ emitEvent: false });
      if (!this.autoPoOn && control.enabled) control.disable({ emitEvent: false });
    }
  }

  private onFormChanged() {
    this.syncDependents();
    this.changes = this.baseline ? diffSettings(this.baseline, this.value, this.departments) : [];
  }

  private rebuildDepartmentOptions() {
    const options = this.departments.map(d => ({
      label: d.hasHead ? departmentLabel(d) : `${departmentLabel(d)}, no head assigned`,
      value: d.departmentId
    }));

    // A department that was chosen once and has since gone would otherwise show as an empty box.
    const current = (this.baseline?.intimationDepartmentId ?? null) as number | null;
    if (current !== null && !options.some(o => o.value === current)) {
      options.unshift({
        label: this.departmentsLoaded ? `Department ${current} (not found)` : `Department ${current}`,
        value: current
      });
    }
    this.departmentOptions = options;
  }

  // ── Saving ──────────────────────────────────────────────────────────────────

  /** The first thing wrong with the form, in words. */
  firstProblem(): string | null {
    const ttl = this.form.get('reservationTtlHours');
    if (ttl?.invalid) return `Hold reservations for between ${MIN_RESERVATION_HOURS} and ${MAX_RESERVATION_HOURS} hours.`;

    const cc = this.form.get('intimationCcEmails')?.errors;
    if (cc?.['invalidEmails']) return `These copy addresses are not valid: ${(cc['invalidEmails'] as string[]).join(', ')}.`;
    if (cc?.['tooLong']) return `The copy addresses are too long. Keep them within ${MAX_CC_LENGTH} characters.`;

    if (this.form.errors?.['dropShipDefault']) return 'Drop ship is the default fulfilment, so drop shipping has to be turned on.';
    if (this.form.errors?.['pickupDefault']) return 'New orders start as customer pickup, so customer pickup has to be turned on.';
    if (this.form.invalid) return 'Some settings are not valid.';
    return null;
  }

  reviewAndSave() {
    if (!this.canEdit || !this.baseline || this.isSaving) return;

    this.form.markAllAsTouched();
    const problem = this.firstProblem();
    if (problem) {
      this.messageService.add({ severity: 'warn', summary: 'Check the settings', detail: problem });
      return;
    }
    if (!this.dirty) {
      this.messageService.add({ severity: 'info', summary: 'Nothing to save', detail: 'No setting has been changed.' });
      return;
    }

    this.reviewChanges = diffSettings(this.baseline, this.value, this.departments);
    this.reviewNotes = impactNotes(this.baseline, this.value);
    this.reviewVisible = true;
  }

  confirmSave() {
    if (this.isSaving || !this.canEdit) return;
    this.isSaving = true;

    this.service.updateConfig(toRequest(this.value)).subscribe({
      next: (res) => {
        this.isSaving = false;
        if (!res.success || !res.result) { this.reviewVisible = false; this.failed(res.message); return; }

        this.reviewVisible = false;
        this.applyConfig(res.result);
        this.messageService.add({
          severity: 'success', summary: 'Saved',
          detail: 'The sale order settings are updated and the change is in the history.'
        });

        this.auditLoaded = false;
        if (this.activeTab === 1) this.loadAudit(1);
      },
      error: (err) => {
        this.isSaving = false;
        this.reviewVisible = false;
        this.failed(err?.status === 403
          ? 'Only the Supply Department Administrator can change these settings.'
          : err?.error?.message);
      }
    });
  }

  private failed(reason?: string) {
    this.messageService.add({
      severity: 'error', summary: 'Not saved',
      detail: reason || 'The settings could not be saved. Nothing was changed.'
    });
  }

  discard() {
    this.resetToBaseline();
  }

  // ── History ─────────────────────────────────────────────────────────────────

  onTabChange(event: TabViewChangeEvent) {
    this.activeTab = event.index;
    if (event.index === 1 && !this.auditLoaded) this.loadAudit(1);
  }

  loadAudit(page = 1) {
    this.isLoadingAudit = true;
    this.auditFailed = false;

    this.service.getAudit(page, this.auditPageSize).subscribe({
      next: (res) => {
        this.isLoadingAudit = false;
        this.auditLoaded = true;
        this.audit = res.result?.data ?? [];
        this.auditTotal = res.result?.totalRecords ?? 0;
        this.auditPage = page;
      },
      error: () => {
        this.isLoadingAudit = false;
        this.auditFailed = true;
      }
    });
  }

  onAuditPage(event: PaginatorState) {
    this.loadAudit(Math.floor((event.first ?? 0) / this.auditPageSize) + 1);
  }

  auditSetting(row: SaleOrderConfigAuditModel): string {
    return settingLabel(row.fieldChanged);
  }

  auditFrom(row: SaleOrderConfigAuditModel): string {
    return displayAuditValue(row.fieldChanged, row.oldValue, this.departments);
  }

  auditTo(row: SaleOrderConfigAuditModel): string {
    return displayAuditValue(row.fieldChanged, row.newValue, this.departments);
  }

  auditWho(row: SaleOrderConfigAuditModel): string {
    return row.changedByName || `User ${row.changedBy}`;
  }
}
