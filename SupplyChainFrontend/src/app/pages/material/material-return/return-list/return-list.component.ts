import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TableModule } from 'primeng/table';
import { DropdownModule } from 'primeng/dropdown';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { MaterialService, ReturnListItem } from '../../../../services/material.service';
import { downloadReturnPdf } from '../return-pdf.util';

@Component({
  selector: 'app-return-list',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule,
    ButtonModule, InputTextModule, TagModule,
    ToastModule, TableModule, DropdownModule, TooltipModule
  ],
  templateUrl: './return-list.component.html',
  styleUrls: ['./return-list.component.scss'],
  providers: [MessageService]
})
export class ReturnListComponent implements OnInit {
  isLoading = false;
  returns: ReturnListItem[] = [];
  downloadingUuids = new Set<string>();
  total    = 0;
  page     = 1;
  pageSize = 20;

  statusFilter = '';
  statusOptions = [
    { label: 'All Statuses', value: '' },
    { label: 'Draft',        value: 'DRAFT' },
    { label: 'Posted',       value: 'POSTED' },
    { label: 'Cancelled',    value: 'CANCELLED' }
  ];

  constructor(
    private materialService: MaterialService,
    private messageService:  MessageService
  ) {}

  ngOnInit() { this.load(); }

  load() {
    this.isLoading = true;
    this.materialService.getReturns({
      status:   this.statusFilter || undefined,
      page:     this.page,
      pageSize: this.pageSize
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.returns = res.result.data;
          this.total   = res.result.totalRecords;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load return vouchers.' });
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
    this.statusFilter = '';
    this.page = 1;
    this.load();
  }

  downloadPdf(ret: ReturnListItem) {
    if (this.downloadingUuids.has(ret.uuid)) return;
    this.downloadingUuids.add(ret.uuid);
    this.materialService.getReturn(ret.uuid).subscribe({
      next: (res) => {
        this.downloadingUuids.delete(ret.uuid);
        if (res.success && res.result) {
          downloadReturnPdf(res.result);
        }
      },
      error: () => {
        this.downloadingUuids.delete(ret.uuid);
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to generate PDF.' });
      }
    });
  }

  getStatusSeverity(s: string): 'warn' | 'success' | 'danger' | 'secondary' {
    switch (s) {
      case 'DRAFT':     return 'warn';
      case 'POSTED':    return 'success';
      case 'CANCELLED': return 'danger';
      default:          return 'secondary';
    }
  }
}
