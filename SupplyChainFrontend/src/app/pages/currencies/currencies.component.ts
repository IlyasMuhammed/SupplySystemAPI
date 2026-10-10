import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { DialogModule } from 'primeng/dialog';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TooltipModule } from 'primeng/tooltip';
import { TagModule } from 'primeng/tag';
import { MessageService, ConfirmationService } from 'primeng/api';
import { CurrenciesService, CurrencyModel } from '../../services/currencies.service';
import { FLOW } from '../../shared/flow';

@Component({
  selector: 'app-currencies',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule, RouterModule,
    TableModule, ButtonModule, InputTextModule, InputIconModule, IconFieldModule, DialogModule,
    ToastModule, ConfirmDialogModule, TooltipModule, TagModule, ...FLOW
  ],
  templateUrl: './currencies.component.html',
  styleUrls: ['./currencies.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class CurrenciesComponent implements OnInit {
  currencies: CurrencyModel[] = [];
  isLoading = true;
  showDialog = false;
  isEditing = false;
  isSaving = false;
  editId: string | null = null;
  form!: FormGroup;

  constructor(
    private fb: FormBuilder,
    private service: CurrenciesService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit() {
    this.form = this.fb.group({
      name:   ['', [Validators.required, Validators.minLength(2), Validators.maxLength(100)]],
      code:   ['', [Validators.maxLength(10)]],
      symbol: ['', [Validators.maxLength(10)]]
    });
    this.load();
  }

  load() {
    this.isLoading = true;
    this.service.getAll().subscribe({
      next: (res) => {
        this.isLoading = false;
        this.currencies = res.result ?? [];
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load currencies' });
      }
    });
  }

  openNew() {
    this.isEditing = false;
    this.editId = null;
    this.form.reset();
    this.showDialog = true;
  }

  openEdit(currency: CurrencyModel) {
    this.isEditing = true;
    this.editId = currency.id;
    this.form.patchValue({ name: currency.name, code: currency.code ?? '', symbol: currency.symbol ?? '' });
    this.showDialog = true;
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.isSaving = true;
    const payload = {
      name:   this.form.value.name,
      code:   this.form.value.code || undefined,
      symbol: this.form.value.symbol || undefined
    };

    const onNext = (res: { success: boolean; message: string }) => {
      this.isSaving = false;
      if (res.success) {
        this.messageService.add({
          severity: 'success', summary: 'Saved',
          detail: this.isEditing ? 'Currency updated' : 'Currency created'
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

  confirmDelete(currency: CurrencyModel) {
    this.confirmationService.confirm({
      message: `Delete currency "${currency.name}"?`,
      header: 'Confirm Delete',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.service.delete(currency.id).subscribe({
          next: () => {
            this.messageService.add({ severity: 'success', summary: 'Deleted', detail: 'Currency deleted' });
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
