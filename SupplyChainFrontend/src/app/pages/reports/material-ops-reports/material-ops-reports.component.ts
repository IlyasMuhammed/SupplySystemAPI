import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TabViewModule } from 'primeng/tabview';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { MessageService } from 'primeng/api';
import {
  ReportsService,
  MaterialReturnReportFilter,
  MaterialReturnReportItem,
  WastageReportFilter,
  WastageReportItem,
  ReservedStockFilter,
  ReservedStockItem
} from '../../../services/reports.service';

@Component({
  selector: 'app-material-ops-reports',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, InputTextModule,
            TabViewModule, TagModule, ToastModule, DropdownModule],
  templateUrl: './material-ops-reports.component.html',
  styleUrls: ['./material-ops-reports.component.scss'],
  providers: [MessageService]
})
export class MaterialOpsReportsComponent {
  // Returns
  returns: MaterialReturnReportItem[] = [];
  returnFilter: MaterialReturnReportFilter = {};
  isLoadingReturns = false;

  // Wastage
  wastages: WastageReportItem[] = [];
  wastageFilter: WastageReportFilter = {};
  isLoadingWastage = false;

  // Reserved Stock
  reserved: ReservedStockItem[] = [];
  reservedFilter: ReservedStockFilter = {};
  isLoadingReserved = false;

  returnStatusOptions = [
    { label: 'All Statuses', value: '' },
    { label: 'Draft',        value: 'DRAFT' },
    { label: 'Posted',       value: 'POSTED' },
    { label: 'Cancelled',    value: 'CANCELLED' }
  ];

  conditionOptions = [
    { label: 'All Conditions', value: '' },
    { label: 'Good',           value: 'GOOD' },
    { label: 'Damaged',        value: 'DAMAGED' }
  ];

  wastageStatusOptions = [
    { label: 'All Statuses',    value: '' },
    { label: 'Pending Approval', value: 'PENDING_APPROVAL' },
    { label: 'Approved',         value: 'APPROVED' },
    { label: 'Rejected',         value: 'REJECTED' }
  ];

  sourceTypeOptions = [
    { label: 'All Sources',    value: '' },
    { label: 'Manual',         value: 'MANUAL' },
    { label: 'Damaged Return', value: 'DAMAGED_RETURN' }
  ];

  reservedStatusOptions = [
    { label: 'Active + Flagged', value: '' },
    { label: 'Active',           value: 'ACTIVE' },
    { label: 'Flagged',          value: 'FLAGGED' },
    { label: 'Released',         value: 'RELEASED' },
    { label: 'Consumed',         value: 'CONSUMED' }
  ];

  constructor(private svc: ReportsService, private msg: MessageService) {}

  // ── Returns ────────────────────────────────────────────────────────────────

  get returnTotal(): number { return this.returns.reduce((s, i) => s + i.lineValue, 0); }

  getReturnStatusSeverity(s: string): 'success' | 'warn' | 'danger' | 'info' {
    switch (s) {
      case 'POSTED':    return 'success';
      case 'DRAFT':     return 'warn';
      case 'CANCELLED': return 'danger';
      default:          return 'info';
    }
  }

  getConditionSeverity(c: string): 'success' | 'danger' {
    return c === 'GOOD' ? 'success' : 'danger';
  }

  loadReturns(): void {
    this.isLoadingReturns = true;
    this.svc.getMaterialReturn(this.returnFilter).subscribe({
      next: r => { this.isLoadingReturns = false; this.returns = r.success ? r.result : []; },
      error: () => { this.isLoadingReturns = false; this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load returns.' }); }
    });
  }

  clearReturns(): void { this.returnFilter = {}; this.returns = []; }

  // ── Wastage ────────────────────────────────────────────────────────────────

  get wastageTotal(): number { return this.wastages.reduce((s, i) => s + i.amount, 0); }

  getWastageStatusSeverity(s: string): 'warn' | 'success' | 'danger' {
    switch (s) {
      case 'APPROVED':         return 'success';
      case 'REJECTED':         return 'danger';
      default:                 return 'warn';
    }
  }

  loadWastage(): void {
    this.isLoadingWastage = true;
    this.svc.getMaterialWastage(this.wastageFilter).subscribe({
      next: r => { this.isLoadingWastage = false; this.wastages = r.success ? r.result : []; },
      error: () => { this.isLoadingWastage = false; this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load wastage.' }); }
    });
  }

  clearWastage(): void { this.wastageFilter = {}; this.wastages = []; }

  // ── Reserved Stock ─────────────────────────────────────────────────────────

  get reservedTotal(): number { return this.reserved.reduce((s, i) => s + i.reservedQty, 0); }
  get flaggedCount():  number { return this.reserved.filter(i => i.isFlagged).length; }

  getReservedStatusSeverity(s: string): 'success' | 'warn' | 'danger' | 'info' {
    switch (s) {
      case 'ACTIVE':   return 'success';
      case 'FLAGGED':  return 'danger';
      case 'RELEASED': return 'warn';
      default:         return 'info';
    }
  }

  getAgeSeverity(days: number): 'success' | 'warn' | 'danger' {
    if (days <= 7)  return 'success';
    if (days <= 30) return 'warn';
    return 'danger';
  }

  loadReserved(): void {
    this.isLoadingReserved = true;
    this.svc.getReservedStock(this.reservedFilter).subscribe({
      next: r => { this.isLoadingReserved = false; this.reserved = r.success ? r.result : []; },
      error: () => { this.isLoadingReserved = false; this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load reserved stock.' }); }
    });
  }

  clearReserved(): void { this.reservedFilter = {}; this.reserved = []; }
}
