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
import { WarehouseService, SroListItemModel, SroListFilter } from '../../../../services/warehouse.service';

@Component({
  selector: 'app-sro-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule,
    TooltipModule, ToastModule, DropdownModule,
    InputTextModule, InputIconModule, IconFieldModule
  ],
  templateUrl: './sro-list.component.html',
  styleUrls: ['./sro-list.component.scss'],
  providers: [MessageService]
})
export class SroListComponent implements OnInit {
  sros: SroListItemModel[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  filter: SroListFilter = { page: 1, pageSize: 20 };

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  statusOptions = [
    { label: 'All Statuses',            value: '' },
    { label: 'Draft',                   value: 'DRAFT' },
    { label: 'Approved',                value: 'APPROVED' },
    { label: 'Rejected',                value: 'REJECTED' },
    { label: 'Dispatched',              value: 'DISPATCHED' },
    { label: 'Supplier Received',       value: 'SUPPLIER_RECEIVED' },
    { label: 'Awaiting Replacement',    value: 'AWAITING_REPLACEMENT' },
    { label: 'Resolved (Credit)',       value: 'RESOLVED_CREDIT' },
    { label: 'Resolved (Replacement)',  value: 'RESOLVED_REPLACEMENT' },
    { label: 'Resolved (Debit)',        value: 'RESOLVED_DEBIT' },
    { label: 'Escalated',               value: 'ESCALATED' }
  ];

  typeOptions = [
    { label: 'All Types',           value: '' },
    { label: 'GRN Rejection',       value: 'GRN_REJECTION' },
    { label: 'Post-Receipt Defect', value: 'POST_RECEIPT_DEFECT' },
    { label: 'Wrong Item',          value: 'WRONG_ITEM' },
    { label: 'Overdelivery',        value: 'OVERDELIVERY' }
  ];

  constructor(
    private warehouseService: WarehouseService,
    private messageService: MessageService
  ) {}

  ngOnInit() { this.load(); }

  load() {
    this.isLoading = true;
    const f: SroListFilter = {
      page:     this.currentPage,
      pageSize: this.pageSize,
      search:   this.filter.search   || undefined,
      status:   this.filter.status   || undefined,
      sroType:  this.filter.sroType  || undefined
    };
    this.warehouseService.getSros(f).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.sros         = res.result.data         ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else {
          this.sros = []; this.totalRecords = 0;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load supplier return orders.' });
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
      case 'APPROVED':             return 'success';
      case 'DISPATCHED':           return 'info';
      case 'SUPPLIER_RECEIVED':    return 'info';
      case 'AWAITING_REPLACEMENT': return 'warn';
      case 'RESOLVED_CREDIT':
      case 'RESOLVED_REPLACEMENT':
      case 'RESOLVED_DEBIT':       return 'success';
      case 'REJECTED':
      case 'ESCALATED':            return 'danger';
      case 'DRAFT':                return 'secondary';
      default:                     return 'secondary';
    }
  }

  getStatusLabel(s: string): string {
    const map: Record<string, string> = {
      DRAFT:                'Draft',
      APPROVED:             'Approved',
      REJECTED:             'Rejected',
      DISPATCHED:           'Dispatched',
      SUPPLIER_RECEIVED:    'Supplier Received',
      AWAITING_REPLACEMENT: 'Awaiting Replacement',
      RESOLVED_CREDIT:      'Resolved (Credit)',
      RESOLVED_REPLACEMENT: 'Resolved (Replacement)',
      RESOLVED_DEBIT:       'Resolved (Debit)',
      ESCALATED:            'Escalated'
    };
    return map[s] ?? s;
  }

  getSroTypeLabel(t: string): string {
    const map: Record<string, string> = {
      GRN_REJECTION:       'GRN Rejection',
      POST_RECEIPT_DEFECT: 'Post-Receipt Defect',
      WRONG_ITEM:          'Wrong Item',
      OVERDELIVERY:        'Overdelivery'
    };
    return map[t] ?? t;
  }

  getSroTypeSeverity(t: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (t) {
      case 'GRN_REJECTION':       return 'danger';
      case 'POST_RECEIPT_DEFECT': return 'warn';
      case 'WRONG_ITEM':          return 'info';
      case 'OVERDELIVERY':        return 'secondary';
      default:                    return 'secondary';
    }
  }

  getReasonLabel(r: string): string {
    const map: Record<string, string> = {
      DAMAGED:      'Damaged',
      DEFECTIVE:    'Defective',
      WRONG_ITEM:   'Wrong Item',
      WRONG_QTY:    'Wrong Quantity',
      SHORT_EXPIRY: 'Short Expiry',
      SPEC_MISMATCH:'Spec Mismatch',
      DUPLICATE:    'Duplicate',
      QUALITY_FAIL: 'Quality Failure',
      OTHER:        'Other'
    };
    return map[r] ?? r;
  }
}
