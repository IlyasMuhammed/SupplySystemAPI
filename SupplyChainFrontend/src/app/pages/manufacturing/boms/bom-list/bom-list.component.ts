import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DropdownModule } from 'primeng/dropdown';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { InputTextModule } from 'primeng/inputtext';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { BOM_STATUS_OPTIONS, BomListFilter, BomListItem, BomService, bomStatusSeverity } from '../../../../services/bom.service';
import { AuthService } from '../../../service/auth.service';

/** A30 §29.2 — every recipe, filterable by product, status and text; the active version stands out. */
@Component({
  selector: 'app-bom-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, DropdownModule, IconFieldModule, InputIconModule, InputTextModule, TableModule, TagModule, ToastModule, TooltipModule
  ],
  templateUrl: './bom-list.component.html',
  styleUrls: ['./bom-list.component.scss'],
  providers: [MessageService]
})
export class BomListComponent implements OnInit {
  readonly statusOptions = BOM_STATUS_OPTIONS;
  readonly severity = bomStatusSeverity;

  boms: BomListItem[] = [];
  totalRecords = 0;
  currentPage = 1;
  pageSize = 20;
  isLoading = true;
  filter: BomListFilter = {};

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private service: BomService,
    private authService: AuthService,
    private messageService: MessageService
  ) {}

  get canCreate(): boolean { return this.authService.hasPermission('BOM_CREATE'); }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.isLoading = true;
    this.service.getBoms({
      page: this.currentPage, pageSize: this.pageSize,
      status: this.filter.status || undefined, search: this.filter.search || undefined
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.boms = res.result?.data ?? [];
        this.totalRecords = res.result?.totalRecords ?? 0;
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load bills of materials.' });
      }
    });
  }

  onSearchChange(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => { this.currentPage = 1; this.load(); }, 400);
  }

  onFilterChange(): void { this.currentPage = 1; this.load(); }

  onPageChange(event: TableLazyLoadEvent): void {
    this.currentPage = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize)) + 1;
    this.pageSize = event.rows ?? this.pageSize;
    this.load();
  }

  resetFilters(): void {
    this.filter = {};
    this.currentPage = 1;
    this.load();
  }
}
