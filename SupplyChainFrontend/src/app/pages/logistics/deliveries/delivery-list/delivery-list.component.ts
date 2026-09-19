import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { MessageService } from 'primeng/api';

import { LogisticsService, DeliveryListItemModel, DeliveryFilter } from '../../../../services/logistics.service';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/**
 * Severity for every delivery status the server can return.
 *
 * Exported so the spec can assert the map is complete: an unmapped status falls back to plain
 * grey, which reads as "nothing notable here" — exactly the wrong signal for CANCELLED or
 * SHORT_CLOSED.
 */
export const DELIVERY_STATUS_SEVERITY: Record<string, Severity> = {
  DRAFT:               'secondary',
  RELEASED:            'info',
  PICKING:             'info',
  PICKED:              'info',
  PACKED:              'info',
  STAGED:              'info',
  PENDING_APPROVAL:    'warn',
  GOODS_ISSUED:        'warn',
  IN_TRANSIT:          'info',
  PARTIALLY_DELIVERED: 'warn',
  ON_HOLD:             'warn',
  DELIVERED:           'success',
  CLOSED:              'success',
  SHORT_CLOSED:        'danger',
  CANCELLED:           'danger'
};

@Component({
  selector: 'app-delivery-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, InputTextModule,
    InputIconModule, IconFieldModule, TagModule,
    TooltipModule, ToastModule, DropdownModule
  ],
  templateUrl: './delivery-list.component.html',
  styleUrls: ['./delivery-list.component.scss'],
  providers: [MessageService]
})
export class DeliveryListComponent implements OnInit, OnDestroy {
  deliveries: DeliveryListItemModel[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  searchText         = '';
  selectedStatus     = '';
  selectedDirection  = '';
  selectedSourceType = '';

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  statusOptions = [
    { label: 'All Statuses',        value: '' },
    { label: 'Draft',               value: 'DRAFT' },
    { label: 'Released',            value: 'RELEASED' },
    { label: 'Picking',             value: 'PICKING' },
    { label: 'Picked',              value: 'PICKED' },
    { label: 'Packed',              value: 'PACKED' },
    { label: 'Staged',              value: 'STAGED' },
    { label: 'Pending Approval',    value: 'PENDING_APPROVAL' },
    { label: 'Goods Issued',        value: 'GOODS_ISSUED' },
    { label: 'In Transit',          value: 'IN_TRANSIT' },
    { label: 'Partially Delivered', value: 'PARTIALLY_DELIVERED' },
    { label: 'On Hold',             value: 'ON_HOLD' },
    { label: 'Delivered',           value: 'DELIVERED' },
    { label: 'Short Closed',        value: 'SHORT_CLOSED' },
    { label: 'Closed',              value: 'CLOSED' },
    { label: 'Cancelled',           value: 'CANCELLED' }
  ];

  directionOptions = [
    { label: 'All Directions', value: '' },
    { label: 'Inbound',        value: 'INBOUND' },
    { label: 'Outbound',       value: 'OUTBOUND' },
    { label: 'Transfer',       value: 'TRANSFER' }
  ];

  sourceTypeOptions = [
    { label: 'All Sources',      value: '' },
    { label: 'Purchase Order',   value: 'PO' },
    { label: 'Supplier Return',  value: 'SRO' },
    { label: 'Material Issue',   value: 'MIV' },
    { label: 'Warehouse Transfer', value: 'TRANSFER' },
    { label: 'Manual',           value: 'MANUAL' },
    { label: 'Sale Order',       value: 'SALE_ORDER' }
  ];

  constructor(
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    // Deliberately does not load. A PrimeNG table with [lazy]="true" fires (onLazyLoad) once as
    // it initialises, so loading here too would issue the same request twice on every visit.
    // The table's first lazy event supplies first=0 and rows=pageSize, which is page 1.
  }

  ngOnDestroy() {
    // A pending debounce would otherwise fire a request after the screen is gone.
    if (this.searchTimer) clearTimeout(this.searchTimer);
  }

  load() {
    this.isLoading = true;

    const filter: DeliveryFilter = {
      page: this.currentPage,
      pageSize: this.pageSize,
      search: this.searchText || undefined,
      status: this.selectedStatus || undefined,
      direction: this.selectedDirection || undefined,
      sourceType: this.selectedSourceType || undefined
    };

    this.logisticsService.getDeliveries(filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.deliveries   = res.result.data ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else {
          this.deliveries = [];
          this.totalRecords = 0;
        }
      },
      error: () => {
        // Clearing the loading flag matters as much as the toast: leaving it set shows a
        // spinner for ever, which reads as "still working" rather than "this failed".
        this.isLoading = false;
        this.deliveries = [];
        this.totalRecords = 0;
        this.messageService.add({
          severity: 'error', summary: 'Error', detail: 'Failed to load deliveries.'
        });
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
    this.searchText = '';
    this.selectedStatus = '';
    this.selectedDirection = '';
    this.selectedSourceType = '';
    this.currentPage = 1;
    this.load();
  }

  getStatusSeverity(status: string): Severity {
    return DELIVERY_STATUS_SEVERITY[status] ?? 'secondary';
  }

  /** Turns GOODS_ISSUED into "Goods Issued" so the grid does not shout at the reader. */
  formatStatus(status: string): string {
    if (!status) return '';
    return status
      .split('_')
      .map(word => word.charAt(0) + word.slice(1).toLowerCase())
      .join(' ');
  }
}
