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
import { MaterialService, ProjectListItem, ProjectListFilter } from '../../../../services/material.service';

@Component({
  selector: 'app-project-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule,
    TooltipModule, ToastModule, DropdownModule,
    InputTextModule, InputIconModule, IconFieldModule
  ],
  templateUrl: './project-list.component.html',
  styleUrls: ['./project-list.component.scss'],
  providers: [MessageService]
})
export class ProjectListComponent implements OnInit {
  projects: ProjectListItem[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  filter: ProjectListFilter = { page: 1, pageSize: 20 };

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  statusOptions = [
    { label: 'All Statuses', value: '' },
    { label: 'Active',       value: 'ACTIVE' },
    { label: 'On Hold',      value: 'ON_HOLD' },
    { label: 'Completed',    value: 'COMPLETED' },
    { label: 'Cancelled',    value: 'CANCELLED' }
  ];

  constructor(
    private materialService: MaterialService,
    private messageService: MessageService
  ) {}

  ngOnInit() { this.load(); }

  load() {
    this.isLoading = true;
    const f: ProjectListFilter = {
      page:     this.currentPage,
      pageSize: this.pageSize,
      search:   this.filter.search || undefined,
      status:   this.filter.status || undefined
    };
    this.materialService.getProjects(f).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.projects     = res.result.data         ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else {
          this.projects = []; this.totalRecords = 0;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load projects.' });
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

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'ACTIVE':    return 'success';
      case 'ON_HOLD':   return 'warn';
      case 'COMPLETED': return 'info';
      case 'CANCELLED': return 'danger';
      default:          return 'secondary';
    }
  }

  getStatusLabel(s: string): string {
    const map: Record<string, string> = {
      ACTIVE: 'Active', ON_HOLD: 'On Hold', COMPLETED: 'Completed', CANCELLED: 'Cancelled'
    };
    return map[s] ?? s;
  }
}
