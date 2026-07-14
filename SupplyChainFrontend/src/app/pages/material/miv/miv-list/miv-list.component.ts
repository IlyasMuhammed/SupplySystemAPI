import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { MessageService } from 'primeng/api';
import { TableLazyLoadEvent } from 'primeng/table';
import { MaterialService, MivListItem, MivListFilter } from '../../../../services/material.service';
import { downloadMivPdf } from '../miv-pdf.util';

@Component({
  selector: 'app-miv-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule,
    TooltipModule, ToastModule, DropdownModule,
    InputTextModule, InputIconModule, IconFieldModule
  ],
  templateUrl: './miv-list.component.html',
  styleUrls: ['./miv-list.component.scss'],
  providers: [MessageService]
})
export class MivListComponent implements OnInit {
  mivs: MivListItem[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  downloadingUuids = new Set<string>();

  filter: MivListFilter = { page: 1, pageSize: 20 };

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  statusOptions = [
    { label: 'All Statuses', value: '' },
    { label: 'Draft',        value: 'DRAFT' },
    { label: 'Posted',       value: 'POSTED' },
    { label: 'Cancelled',    value: 'CANCELLED' }
  ];

  constructor(
    private materialService: MaterialService,
    private messageService: MessageService
  ) {}

  ngOnInit() { this.load(); }

  load() {
    this.isLoading = true;
    this.materialService.getMivs({
      page:     this.currentPage,
      pageSize: this.pageSize,
      search:   this.filter.search  || undefined,
      status:   this.filter.status  || undefined,
      mirUuid:  this.filter.mirUuid || undefined
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.mivs         = res.result.data         ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else {
          this.mivs = []; this.totalRecords = 0;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load issue vouchers.' });
      }
    });
  }

  onSearchChange() {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => { this.currentPage = 1; this.load(); }, 400);
  }

  onFilterChange() { this.currentPage = 1; this.load(); }

  onPageChange(event: TableLazyLoadEvent) {
    this.currentPage = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize)) + 1;
    this.pageSize    = event.rows ?? this.pageSize;
    this.load();
  }

  resetFilters() {
    this.filter      = { page: 1, pageSize: 20 };
    this.currentPage = 1;
    this.load();
  }

  downloadPdf(miv: MivListItem) {
    if (this.downloadingUuids.has(miv.uuid)) return;
    this.downloadingUuids.add(miv.uuid);

    this.materialService.getMiv(miv.uuid).subscribe({
      next: (res) => {
        this.downloadingUuids.delete(miv.uuid);
        if (res.success && res.result) {
          downloadMivPdf(res.result);
        }
      },
      error: () => {
        this.downloadingUuids.delete(miv.uuid);
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to generate PDF.' });
      }
    });
  }

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'DRAFT':      return 'secondary';
      case 'POSTED':     return 'success';
      case 'CANCELLED':  return 'danger';
      default:           return 'secondary';
    }
  }

  getStatusLabel(s: string): string {
    const map: Record<string, string> = {
      DRAFT: 'Draft', POSTED: 'Posted', CANCELLED: 'Cancelled'
    };
    return map[s] ?? s;
  }
}
