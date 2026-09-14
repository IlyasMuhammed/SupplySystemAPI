import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputNumberModule } from 'primeng/inputnumber';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { OrganizationSettingsService } from '../../services/organization-settings.service';

const MIN_DAYS = 1;
const MAX_DAYS = 90;

@Component({
  selector: 'app-portal-settings',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, ButtonModule, InputNumberModule, ToastModule],
  templateUrl: './portal-settings.component.html',
  styleUrls: ['./portal-settings.component.scss'],
  providers: [MessageService]
})
export class PortalSettingsComponent implements OnInit {
  readonly minDays = MIN_DAYS;
  readonly maxDays = MAX_DAYS;

  isLoading = true;
  isSaving = false;
  form!: FormGroup;

  constructor(
    private fb: FormBuilder,
    private service: OrganizationSettingsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.form = this.fb.group({
      ackLinkExpiryDays: [null, [Validators.required, Validators.min(MIN_DAYS), Validators.max(MAX_DAYS)]]
    });
    this.load();
  }

  load() {
    this.isLoading = true;
    this.service.get().subscribe({
      next: (res) => {
        this.isLoading = false;
        this.form.patchValue({ ackLinkExpiryDays: res.result?.ackLinkExpiryDays ?? MIN_DAYS });
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load settings' });
      }
    });
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.isSaving = true;

    this.service.update({ ackLinkExpiryDays: this.form.value.ackLinkExpiryDays }).subscribe({
      next: (res) => {
        this.isSaving = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Portal settings updated' });
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Save failed' });
        }
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Save failed' });
      }
    });
  }

  get f() { return this.form.controls; }
}
