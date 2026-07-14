import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { DialogModule } from 'primeng/dialog';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService, ConfirmationService } from 'primeng/api';
import { PaymentTermsService, PaymentTermModel } from '../../services/payment-terms.service';

@Component({
  selector: 'app-payment-terms',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule,
    TableModule, ButtonModule, InputTextModule, InputNumberModule, InputIconModule, IconFieldModule, DialogModule,
    ToastModule, ConfirmDialogModule, TooltipModule
  ],
  templateUrl: './payment-terms.component.html',
  styleUrls: ['./payment-terms.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class PaymentTermsComponent implements OnInit {
  terms: PaymentTermModel[] = [];
  isLoading = true;
  showDialog = false;
  isEditing = false;
  isSaving = false;
  editId: string | null = null;
  form!: FormGroup;

  constructor(
    private fb: FormBuilder,
    private service: PaymentTermsService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit() {
    this.form = this.fb.group({
      name: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(100)]],
      days: [null, [Validators.min(0), Validators.max(3650)]]
    });
    this.load();
  }

  load() {
    this.isLoading = true;
    this.service.getAll().subscribe({
      next: (res) => {
        this.isLoading = false;
        this.terms = res.result ?? [];
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load payment terms' });
      }
    });
  }

  openNew() {
    this.isEditing = false;
    this.editId = null;
    this.form.reset();
    this.showDialog = true;
  }

  openEdit(term: PaymentTermModel) {
    this.isEditing = true;
    this.editId = term.id;
    this.form.patchValue({ name: term.name, days: term.days ?? null });
    this.showDialog = true;
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.isSaving = true;
    const payload = {
      name: this.form.value.name,
      days: this.form.value.days ?? undefined
    };

    const onNext = (res: { success: boolean; message: string }) => {
      this.isSaving = false;
      if (res.success) {
        this.messageService.add({
          severity: 'success', summary: 'Saved',
          detail: this.isEditing ? 'Payment term updated' : 'Payment term created'
        });
        this.showDialog = false;
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
      this.service.update(this.editId, payload).subscribe({ next: onNext, error: onError });
    } else {
      this.service.create(payload).subscribe({ next: onNext, error: onError });
    }
  }

  confirmDelete(term: PaymentTermModel) {
    this.confirmationService.confirm({
      message: `Delete payment term "${term.name}"?`,
      header: 'Confirm Delete',
      icon: 'pi pi-exclamation-triangle',
      accept: () => {
        this.service.delete(term.id).subscribe({
          next: () => {
            this.messageService.add({ severity: 'success', summary: 'Deleted', detail: 'Payment term deleted' });
            this.load();
          },
          error: (err) => {
            this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Delete failed' });
          }
        });
      }
    });
  }

  get f() { return this.form.controls; }
}
