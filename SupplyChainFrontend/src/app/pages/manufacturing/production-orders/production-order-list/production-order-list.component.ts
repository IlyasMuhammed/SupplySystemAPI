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
import {
  PRIORITY_OPTIONS, PRODUCTION_ORDER_STATUS_OPTIONS, ProductionOrderListFilter, ProductionOrderListItem,
  ProductionOrderService, priorityLabel, productionStatusSeverity
} from '../../../../services/production-order.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';

/** A30 §29.3 — every production order, filterable by status, priority and text. */
@Component({
  selector: 'app-production-order-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, DropdownModule, IconFieldModule, InputIconModule, InputTextModule, TableModule, TagModule, ToastModule, TooltipModule,
    ...FLOW
  ],
  templateUrl: './production-order-list.component.html',
  styleUrls: ['./production-order-list.component.scss'],
  providers: [MessageService]
})
export class ProductionOrderListComponent implements OnInit {
  readonly statusOptions = PRODUCTION_ORDER_STATUS_OPTIONS;
  readonly priorityOptions = [{ value: null, label: 'All priorities' }, ...PRIORITY_OPTIONS];
  readonly severity = productionStatusSeverity;
  readonly priorityLabel = priorityLabel;

  orders: ProductionOrderListItem[] = [];
  totalRecords = 0;
  currentPage = 1;
  pageSize = 20;
  isLoading = true;
  filter: Omit<ProductionOrderListFilter, 'priority'> & { priority: number | null } = { priority: null };

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private service: ProductionOrderService,
    private authService: AuthService,
    private messageService: MessageService
  ) {}

  get canCreate(): boolean { return this.authService.hasPermission('PROD_CREATE'); }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.isLoading = true;
    this.service.getList({
      page: this.currentPage, pageSize: this.pageSize,
      status: this.filter.status || undefined,
      priority: this.filter.priority ?? undefined,
      search: this.filter.search || undefined
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.orders = res.result?.data ?? [];
        this.totalRecords = res.result?.totalRecords ?? 0;
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load production orders.' });
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
    this.filter = { priority: null };
    this.currentPage = 1;
    this.load();
  }
}
