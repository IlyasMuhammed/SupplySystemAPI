import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { ToastModule } from 'primeng/toast';
import { TagModule } from 'primeng/tag';
import { CalendarModule } from 'primeng/calendar';
import { DropdownModule } from 'primeng/dropdown';
import { MessageService } from 'primeng/api';
import { ReportsService, AuditLogItemModel, AuditLogFilter, PaginatedResponse } from '../../../services/reports.service';

@Component({
  selector: 'app-audit-trail',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, InputTextModule, ToastModule, TagModule, CalendarModule, DropdownModule],
  templateUrl: './audit-trail.component.html',
  styleUrls: ['./audit-trail.component.scss'],
  providers: [MessageService]
})
export class AuditTrailComponent implements OnInit {
  items:        AuditLogItemModel[] = [];
  totalRecords  = 0;
  isLoading     = true;

  filter: AuditLogFilter = { page: 1, pageSize: 50 };
  filterDateFrom: Date | null = null;
  filterDateTo:   Date | null = null;

  moduleOptions = [
    { label: 'All Modules', value: null },
    { label: 'Demand',      value: 'Demand' },
    { label: 'Inventory',   value: 'Inventory' },
    { label: 'Warehouse',   value: 'Warehouse' },
    { label: 'Finance',     value: 'Finance' },
    { label: 'Auth',        value: 'Auth' },
    { label: 'Reports',     value: 'Reports' }
  ];

  actionOptions = [
    { label: 'All Actions', value: null },
    { label: 'CREATE',      value: 'CREATE' },
    { label: 'UPDATE',      value: 'UPDATE' },
    { label: 'DELETE',      value: 'DELETE' },
    { label: 'SUBMIT',      value: 'SUBMIT' },
    { label: 'APPROVE',     value: 'APPROVE' },
    { label: 'REJECT',      value: 'REJECT' },
    { label: 'SEND',        value: 'SEND' },
    { label: 'CANCEL',      value: 'CANCEL' },
    { label: 'AWARD',       value: 'AWARD' },
    { label: 'CONVERT',     value: 'CONVERT' }
  ];

  entityTypeOptions = [
    { label: 'All Entity Types',     value: null },
    { label: 'Purchase Order',       value: 'PurchaseOrder' },
    { label: 'Purchase Requisition', value: 'PurchaseRequisition' },
    { label: 'Quotation',            value: 'Quotation' },
    { label: 'GRN',                  value: 'GRN' },
    { label: 'Stock Adjustment',     value: 'StockAdjustment' },
    { label: 'Invoice',              value: 'Invoice' },
    { label: 'Payment',              value: 'Payment' },
    { label: 'Product',              value: 'Product' },
    { label: 'Warehouse',            value: 'Warehouse' },
    { label: 'User',                 value: 'User' }
  ];

  constructor(private reports: ReportsService, private msg: MessageService) {}
  ngOnInit(): void { this.load(); }

  load(): void {
    this.filter.dateFrom = this.filterDateFrom ? this.toIsoDate(this.filterDateFrom) : undefined;
    this.filter.dateTo   = this.filterDateTo   ? this.toIsoDate(this.filterDateTo)   : undefined;
    this.isLoading = true;
    this.reports.getAuditTrail(this.filter).subscribe({
      next: r => {
        this.isLoading = false;
        if (r.success) {
          const paged = r.result as PaginatedResponse<AuditLogItemModel>;
          this.items        = paged.data;
          this.totalRecords = paged.totalRecords;
        }
      },
      error: () => { this.isLoading = false; this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load audit logs.' }); }
    });
  }

  resetFilters(): void {
    this.filter = { page: 1, pageSize: 50 };
    this.filterDateFrom = null;
    this.filterDateTo   = null;
    this.load();
  }

  onPage(event: any): void {
    this.filter.page     = Math.floor(event.first / event.rows) + 1;
    this.filter.pageSize = event.rows;
    this.load();
  }

  private toIsoDate(d: Date): string {
    return d.toISOString().split('T')[0];
  }
}
