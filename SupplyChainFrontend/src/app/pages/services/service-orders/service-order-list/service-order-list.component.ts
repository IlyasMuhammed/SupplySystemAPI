import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { DropdownModule } from 'primeng/dropdown';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { InputTextModule } from 'primeng/inputtext';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import {
  SERVICE_ORDER_STATUSES, SERVICE_PRIORITY_OPTIONS, ServiceOrderListItem, ServiceOrderService, ServiceOrderStatus,
  readinessIcon, servicePriorityLabel, serviceStatusLabel, serviceStatusTone
} from '../../../../services/service-order.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { UserService } from '../../../../services/user.service';
import { AuthService } from '../../../service/auth.service';
import { AssigneeOption, assigneeOptions$ } from '../../../sales/sale-inquiries/sale-inquiry.shared';
import { FLOW } from '../../../../shared/flow';
import { PartnerOption, SERVICE_ORDERS_ROUTE, customerOptions$, ymd } from '../service-order-shared';

/** A36-P2-12 — every service order: status chips, customer / assignee / date / priority filters, server paging. */
@Component({
  selector: 'app-service-order-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, CalendarModule, DropdownModule, IconFieldModule, InputIconModule, InputTextModule, TableModule, ToastModule, TooltipModule,
    ...FLOW
  ],
  templateUrl: './service-order-list.component.html',
  styleUrls: ['../_service-shared.scss'],
  providers: [MessageService]
})
export class ServiceOrderListComponent implements OnInit, OnDestroy {
  readonly route = SERVICE_ORDERS_ROUTE;
  readonly statuses = SERVICE_ORDER_STATUSES;
  readonly priorityOptions = [{ value: null as number | null, label: 'All priorities' }, ...SERVICE_PRIORITY_OPTIONS];
  readonly tone = serviceStatusTone;
  readonly statusLabel = serviceStatusLabel;
  readonly priorityLabel = servicePriorityLabel;
  readonly readiness = readinessIcon;

  orders: ServiceOrderListItem[] = [];
  totalRecords = 0;
  currentPage = 1;
  pageSize = 25;
  isLoading = true;

  selectedStatuses = new Set<ServiceOrderStatus>();
  customerUuid: string | null = null;
  assignedUserId: number | null = null;
  priority: number | null = null;
  dateRange: Date[] | null = null;
  search = '';

  customerOptions: PartnerOption[] = [];
  userOptions: AssigneeOption[] = [];

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private service: ServiceOrderService,
    private partners: BusinessPartnerService,
    private users: UserService,
    private auth: AuthService,
    private messageService: MessageService
  ) {}

  get canCreate(): boolean { return this.auth.hasPermission('SERVICE_ORDER_CREATE'); }

  ngOnInit(): void {
    // The lazy table's first (onLazyLoad) is the first load.
    customerOptions$(this.partners).subscribe(o => this.customerOptions = o);
    assigneeOptions$(this.users, this.auth).subscribe(o => this.userOptions = o);
  }

  ngOnDestroy(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
  }

  load(): void {
    this.isLoading = true;
    const [from, to] = this.dateRange ?? [];
    this.service.getList({
      page: this.currentPage, pageSize: this.pageSize,
      status: [...this.selectedStatuses],
      customerUuid: this.customerUuid || undefined,
      assignedUserId: this.assignedUserId ?? undefined,
      priority: this.priority ?? undefined,
      fromDate: ymd(from),
      toDate: ymd(to ?? from),
      search: this.search.trim() || undefined
    }).subscribe({
      next: res => {
        this.isLoading = false;
        this.orders = res.result?.data ?? [];
        this.totalRecords = res.result?.totalRecords ?? 0;
      },
      error: () => {
        this.isLoading = false;
        this.orders = [];
        this.totalRecords = 0;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load service orders.' });
      }
    });
  }

  isOn(status: ServiceOrderStatus): boolean { return this.selectedStatuses.has(status); }

  /** Chips toggle; several can be on at once ("All" clears them). */
  toggleStatus(status: ServiceOrderStatus | null): void {
    if (status === null) this.selectedStatuses.clear();
    else if (this.selectedStatuses.has(status)) this.selectedStatuses.delete(status);
    else this.selectedStatuses.add(status);
    this.onFilterChange();
  }

  onSearchChange(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => { this.currentPage = 1; this.load(); }, 400);
  }

  onDateChange(): void {
    // A range picker emits once for the start date; wait for the end (or a cleared range).
    if (this.dateRange && this.dateRange.length === 2 && !this.dateRange[1]) return;
    this.onFilterChange();
  }

  onFilterChange(): void { this.currentPage = 1; this.load(); }

  onPageChange(event: TableLazyLoadEvent): void {
    this.currentPage = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize)) + 1;
    this.pageSize = event.rows ?? this.pageSize;
    this.load();
  }

  resetFilters(): void {
    this.selectedStatuses.clear();
    this.customerUuid = null;
    this.assignedUserId = null;
    this.priority = null;
    this.dateRange = null;
    this.search = '';
    this.onFilterChange();
  }
}
