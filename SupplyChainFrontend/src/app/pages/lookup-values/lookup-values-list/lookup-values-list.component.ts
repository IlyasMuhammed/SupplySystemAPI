import { Component, OnInit, ViewChild, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { Table, TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { ToolbarModule } from 'primeng/toolbar';  
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { RippleModule } from 'primeng/ripple';
import { TagModule } from 'primeng/tag';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';
import { LookupValuesService, LookupValue } from '../../../services/lookup-values.service';

@Component({
  selector: 'app-lookup-values-list',
  standalone: true,
  imports: [CommonModule, RouterModule, TableModule, ButtonModule, ToolbarModule,
    InputTextModule, InputIconModule, IconFieldModule, TagModule, RippleModule, ConfirmDialogModule, ToastModule],
  templateUrl: './lookup-values-list.component.html',
  styleUrls: ['./lookup-values-list.component.scss'],
  providers: [ConfirmationService, MessageService]
})
export class LookupValuesListComponent implements OnInit {
  @ViewChild('dt') dt?: Table;

  lookupValues = signal<LookupValue[]>([]);
  isLoading   = signal(true);
  deletingIds = new Set<string>();

  constructor(
    private lookupValuesService: LookupValuesService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit() {
    this.messageService.add({ severity: 'info', summary: 'Info', detail: 'Use the create form to add lookup values.', life: 4000 });
    this.isLoading.set(false);
  }

  onGlobalFilter(event: Event) {
    const input = event.target as HTMLInputElement;
    this.dt?.filterGlobal(input.value, 'contains');
  }

  deleteLookupValue(lookupValue: LookupValue) {
    this.confirmationService.confirm({
      message: `Are you sure you want to delete "${(lookupValue as any).value}"?`,
      header: 'Confirm',
      icon: 'pi pi-exclamation-triangle',
      accept: () => {
        this.deletingIds.add(lookupValue.id);
        this.lookupValuesService.deleteLookupValue(lookupValue.id).subscribe({
          next: (response) => {
            this.deletingIds.delete(lookupValue.id);
            if (response.success) {
              this.lookupValues.set(this.lookupValues().filter(v => v.id !== lookupValue.id));
              this.messageService.add({ severity: 'success', summary: 'Success', detail: 'Lookup value deleted successfully', life: 3000 });
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: response.message || 'Failed to delete', life: 3000 });
            }
          },
          error: () => {
            this.deletingIds.delete(lookupValue.id);
            this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to delete lookup value', life: 3000 });
          }
        });
      }
    });
  }
}
