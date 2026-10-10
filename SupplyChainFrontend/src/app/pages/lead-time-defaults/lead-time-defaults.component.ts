import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AbstractControl, FormBuilder, FormGroup, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputNumberModule } from 'primeng/inputnumber';
import { MessageModule } from 'primeng/message';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import {
  LEAD_TIME_DEFAULT_FIELDS, LeadTimeDefaultsModel, LeadTimeDefaultsValues, LeadTimeService, MAX_DEFAULT_LEAD_DAYS
} from '../../services/lead-time.service';
import { AuthService } from '../service/auth.service';
import { FLOW } from '../../shared/flow';

const WRITE_PERMISSION = 'LEAD_TIME_DEFAULTS_MANAGE';

const wholeNumber = (control: AbstractControl): ValidationErrors | null =>
  control.value === null || control.value === undefined || Number.isInteger(control.value) ? null : { wholeNumber: true };

/**
 * A34-PB-08 — Settings → Lead Time Defaults (spec §11.5, API-CONTRACT §4.4). The organization's six default
 * components, used by every variant that has no override of its own. A missing row reads as the system defaults
 * 1/3/1/0/0/0 (D-10); Save upserts all six. Opened with LEAD_TIME_DEFAULTS_MANAGE or INVENTORY_VIEW (route guard =
 * server read codes); saving needs LEAD_TIME_DEFAULTS_MANAGE, read-only otherwise.
 */
@Component({
  selector: 'app-lead-time-defaults',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, ButtonModule, InputNumberModule, MessageModule, TagModule, ToastModule, ...FLOW],
  templateUrl: './lead-time-defaults.component.html',
  styleUrls: ['./lead-time-defaults.component.scss'],
  providers: [MessageService]
})
export class LeadTimeDefaultsComponent implements OnInit {
  readonly fields = LEAD_TIME_DEFAULT_FIELDS;
  readonly maxDays = MAX_DEFAULT_LEAD_DAYS;

  model: LeadTimeDefaultsModel | null = null;
  isLoading = false;
  loadFailed = false;
  isSaving = false;
  submitted = false;
  form: FormGroup;

  constructor(
    private fb: FormBuilder,
    private service: LeadTimeService,
    private authService: AuthService,
    private messages: MessageService
  ) {
    const controls: Record<string, unknown[]> = {};
    for (const f of LEAD_TIME_DEFAULT_FIELDS) {
      controls[f.field] = [0, [Validators.required, Validators.min(0), Validators.max(MAX_DEFAULT_LEAD_DAYS), wholeNumber]];
    }
    this.form = this.fb.group(controls);
  }

  ngOnInit(): void { this.load(); }

  get canEdit(): boolean { return this.authService.hasPermission(WRITE_PERMISSION); }

  get dirty(): boolean {
    if (!this.model) return false;
    const value = this.form.getRawValue() as LeadTimeDefaultsValues;
    return LEAD_TIME_DEFAULT_FIELDS.some(f => value[f.field] !== this.model![f.field]);
  }

  /** The first thing wrong, in words; null when all six are whole numbers from 0 to 365 (BR-C3-01). */
  get problem(): string | null {
    for (const f of LEAD_TIME_DEFAULT_FIELDS) {
      if (this.form.get(f.field)?.invalid) return `${f.label}: a whole number of days between 0 and ${MAX_DEFAULT_LEAD_DAYS}.`;
    }
    return null;
  }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getDefaults().subscribe({
      next: res => {
        this.isLoading = false;
        if (!res.result) { this.loadFailed = true; return; }
        this.apply(res.result);
      },
      error: () => { this.isLoading = false; this.loadFailed = true; }
    });
  }

  private apply(model: LeadTimeDefaultsModel): void {
    this.model = model;
    const values: Partial<LeadTimeDefaultsValues> = {};
    for (const f of LEAD_TIME_DEFAULT_FIELDS) values[f.field] = model[f.field];
    this.form.reset(values);
    this.submitted = false;
    if (this.canEdit) this.form.enable(); else this.form.disable();
  }

  discard(): void { if (this.model) this.apply(this.model); }

  save(): void {
    this.submitted = true;
    if (!this.canEdit || !this.model || this.isSaving) return;
    this.form.markAllAsTouched();
    const problem = this.problem;
    if (problem) return;

    const value = this.form.getRawValue() as LeadTimeDefaultsValues;
    const request = Object.fromEntries(LEAD_TIME_DEFAULT_FIELDS.map(f => [f.field, value[f.field]])) as unknown as LeadTimeDefaultsValues;
    this.isSaving = true;
    this.service.updateDefaults(request).subscribe({
      next: res => {
        this.isSaving = false;
        this.apply(res.result);
        this.messages.add({ severity: 'success', summary: 'Saved', detail: 'The lead time defaults are updated for your organization.' });
      },
      error: err => {
        this.isSaving = false;
        this.messages.add({
          severity: 'error', summary: 'Not saved', life: 8000,
          detail: err?.status === 403
            ? 'Changing the lead time defaults needs the "manage lead time defaults" permission.'
            : err?.error?.message || 'The lead time defaults could not be saved. Nothing was changed.'
        });
      }
    });
  }
}
