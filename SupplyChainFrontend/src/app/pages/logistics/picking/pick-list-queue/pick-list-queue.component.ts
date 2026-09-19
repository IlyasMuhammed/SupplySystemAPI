import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { SelectModule } from 'primeng/select';
import { InputTextModule } from 'primeng/inputtext';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import { LogisticsService, PickListListItemModel } from '../../../../services/logistics.service';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

export const PICK_LIST_STATUS_SEVERITY: Record<string, Severity> = {
  OPEN:        'info',
  IN_PROGRESS: 'warn',
  COMPLETED:   'success',
  CANCELLED:   'secondary'
};

/** The queue a picker picks from: what is open, and how far through each one is. */
@Component({
  selector: 'app-pick-list-queue',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule, SelectModule, InputTextModule, ToastModule
  ],
  templateUrl: './pick-list-queue.component.html',
  styleUrls: ['./pick-list-queue.component.scss'],
  providers: [MessageService]
})
export class PickListQueueComponent {
  rows: PickListListItemModel[] = [];
  totalRecords = 0;
  isLoading = true;

  search = '';
  status: string | null = null;

  statusOptions = [
    { label: 'Open and in progress', value: null },
    { label: 'Open',                 value: 'OPEN' },
    { label: 'In progress',          value: 'IN_PROGRESS' },
    { label: 'Completed',            value: 'COMPLETED' },
    { label: 'Cancelled',            value: 'CANCELLED' }
  ];

  private page = 1;
  private pageSize = 20;

  constructor(
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  // No ngOnInit load: the table's [lazy] binding fires onLazyLoad once as it initialises, and
  // loading here as well issues the same request twice on every visit. That was F25.

  onLazyLoad(event: TableLazyLoadEvent) {
    this.pageSize = event.rows ?? 20;
    this.page = Math.floor((event.first ?? 0) / this.pageSize) + 1;
    this.load();
  }

  applyFilters() {
    this.page = 1;
    this.load();
  }

  load() {
    this.isLoading = true;

    this.logisticsService.getPickLists({
      search: this.search.trim() || undefined,
      status: this.status ?? undefined,
      page: this.page,
      pageSize: this.pageSize
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.rows = res.result?.data ?? [];
        this.totalRecords = res.result?.totalRecords ?? 0;
      },
      error: () => {
        this.isLoading = false;
        this.rows = [];
        this.totalRecords = 0;
        this.messageService.add({
          severity: 'error', summary: 'Error', detail: 'Failed to load pick lists.'
        });
      }
    });
  }

  getStatusSeverity(status: string): Severity {
    return PICK_LIST_STATUS_SEVERITY[status] ?? 'secondary';
  }

  formatStatus(status: string): string {
    if (!status) return '';
    return status.split('_')
      .map(word => word.charAt(0) + word.slice(1).toLowerCase())
      .join(' ');
  }

  /** How far through the walk, as a percentage of what there is to take. */
  progress(row: PickListListItemModel): number {
    if (row.qtyToPick <= 0) return 0;
    return Math.round((row.qtyPicked / row.qtyToPick) * 100);
  }
}
