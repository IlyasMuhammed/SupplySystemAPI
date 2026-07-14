import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TableModule } from 'primeng/table';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { MessageService } from 'primeng/api';
import { MaterialService, WastageListItem, WastageListFilter } from '../../../../services/material.service';

@Component({
  selector: 'app-wastage-list',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule,
    ButtonModule, TagModule, ToastModule,
    TableModule, DropdownModule, CalendarModule
  ],
  templateUrl: './wastage-list.component.html',
  styleUrls: ['./wastage-list.component.scss'],
  providers: [MessageService]
})
export class WastageListComponent implements OnInit {
  isLoading = false;
  wastages: WastageListItem[] = [];
  total    = 0;
  page     = 1;
  pageSize = 20;

  statusFilter     = '';
  sourceTypeFilter = '';
  dateFrom: Date | null = null;
  dateTo:   Date | null = null;

  statusOptions = [
    { label: 'All Statuses',      value: '' },
    { label: 'Pending Approval',  value: 'PENDING_APPROVAL' },
    { label: 'Approved',          value: 'APPROVED' },
    { label: 'Rejected',          value: 'REJECTED' }
  ];

  sourceTypeOptions = [
    { label: 'All Sources',      value: '' },
    { label: 'Manual',          value: 'MANUAL' },
    { label: 'Damaged Return',  value: 'DAMAGED_RETURN' }
  ];

  constructor(
    private materialService: MaterialService,
    private messageService:  MessageService
  ) {}

  ngOnInit() { this.load(); }

  load() {
    this.isLoading = true;
    const filter: WastageListFilter = {
      status:     this.statusFilter     || undefined,
      sourceType: this.sourceTypeFilter || undefined,
      dateFrom:   this.dateFrom?.toISOString(),
      dateTo:     this.dateTo?.toISOString(),
      page:       this.page,
      pageSize:   this.pageSize
    };
    this.materialService.getWastages(filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.wastages = res.result.data;
          this.total    = res.result.totalRecords;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load wastage records.' });
      }
    });
  }

  onLazyLoad(event: any) {
    this.page     = Math.floor(event.first / event.rows) + 1;
    this.pageSize = event.rows;
    this.load();
  }

  applyFilters() { this.page = 1; this.load(); }

  resetFilters() {
    this.statusFilter = this.sourceTypeFilter = '';
    this.dateFrom = this.dateTo = null;
    this.page = 1;
    this.load();
  }

  get pendingCount(): number { return this.wastages.filter(w => w.status === 'PENDING_APPROVAL').length; }

  getStatusSeverity(s: string): 'warn' | 'success' | 'danger' | 'secondary' {
    switch (s) {
      case 'PENDING_APPROVAL': return 'warn';
      case 'APPROVED':         return 'success';
      case 'REJECTED':         return 'danger';
      default:                 return 'secondary';
    }
  }

  getSourceSeverity(s: string): 'secondary' | 'danger' {
    return s === 'DAMAGED_RETURN' ? 'danger' : 'secondary';
  }
}
