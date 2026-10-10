import { Component, OnInit, ViewChild, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FLOW } from '../../../shared/flow';
import { RouterModule } from '@angular/router';
import { Table, TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { ToolbarModule } from 'primeng/toolbar';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { TagModule } from 'primeng/tag';
import { RippleModule } from 'primeng/ripple';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';
import { LookupTypesService, LookupType } from '../../../services/lookup-types.service';

@Component({
  selector: 'app-lookup-types-list',
  standalone: true,
  imports: [...FLOW, CommonModule, RouterModule, TableModule, ButtonModule, ToolbarModule,
    InputTextModule, InputIconModule, IconFieldModule, TagModule, RippleModule,
    ConfirmDialogModule, ToastModule],
  templateUrl: './lookup-types-list.component.html',
  styleUrls: ['./lookup-types-list.component.scss'],
  providers: [ConfirmationService, MessageService]
})
export class LookupTypesListComponent implements OnInit {
  @ViewChild('dt') dt?: Table;

  lookupTypes = signal<LookupType[]>([]);
  isLoading   = signal(true);
  deletingIds = new Set<string>();

  constructor(
    private lookupTypesService: LookupTypesService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit() {
    this.loadLookupTypes();
  }

  loadLookupTypes() {
    this.isLoading.set(true);
    this.lookupTypesService.getAllLookupTypes().subscribe({
      next: (response) => {
        this.lookupTypes.set(Array.isArray(response.result) ? response.result : []);
        this.isLoading.set(false);
      },
      error: (err) => {
        this.isLoading.set(false);
        console.error('Error loading lookup types:', err);
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load lookup types', life: 3000 });
      }
    });
  }

  onGlobalFilter(event: Event) {
    const input = event.target as HTMLInputElement;
    this.dt?.filterGlobal(input.value, 'contains');
  }

  deleteLookupType(lookupType: LookupType) {
    this.confirmationService.confirm({
      message: `Are you sure you want to delete "${lookupType.name}"?`,
      header: 'Confirm',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.deletingIds.add(lookupType.id);
        this.lookupTypesService.deleteLookupType(lookupType.id).subscribe({
          next: (response) => {
            this.deletingIds.delete(lookupType.id);
            if (response.success) {
              this.lookupTypes.set(this.lookupTypes().filter(t => t.id !== lookupType.id));
              this.messageService.add({ severity: 'success', summary: 'Success', detail: 'Lookup type deleted successfully', life: 3000 });
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: response.message || 'Failed to delete lookup type', life: 3000 });
            }
          },
          error: () => {
            this.deletingIds.delete(lookupType.id);
            this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to delete lookup type', life: 3000 });
          }
        });
      }
    });
  }
}
