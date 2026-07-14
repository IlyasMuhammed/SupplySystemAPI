import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TableModule } from 'primeng/table';
import { CalendarModule } from 'primeng/calendar';
import { DropdownModule } from 'primeng/dropdown';
import { MessageService } from 'primeng/api';
import { MaterialService, CostLedgerEntry, CostLedgerFilter } from '../../../services/material.service';

@Component({
  selector: 'app-department-cost-ledger',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule,
    ButtonModule, InputTextModule, TagModule,
    ToastModule, TableModule, CalendarModule, DropdownModule
  ],
  templateUrl: './department-cost-ledger.component.html',
  styleUrls: ['./department-cost-ledger.component.scss'],
  providers: [MessageService]
})
export class DepartmentCostLedgerComponent implements OnInit {
  isLoading  = false;
  entries: CostLedgerEntry[] = [];
  total      = 0;
  page       = 1;
  pageSize   = 20;

  department    = '';
  dateFrom: Date | null = null;
  dateTo:   Date | null = null;
  transactionType = '';

  txTypes = [
    { label: 'All Types', value: '' },
    { label: 'Issue',     value: 'ISSUE' },
    { label: 'Return',    value: 'RETURN' },
    { label: 'Wastage',   value: 'WASTAGE' }
  ];

  get totalIssued(): number   { return this.entries.filter(e => e.amount > 0).reduce((s, e) => s + e.amount, 0); }
  get totalReturned(): number { return this.entries.filter(e => e.amount < 0).reduce((s, e) => s + Math.abs(e.amount), 0); }
  get netCost(): number       { return this.entries.reduce((s, e) => s + e.amount, 0); }

  constructor(
    private materialService: MaterialService,
    private messageService:  MessageService
  ) {}

  ngOnInit() { this.load(); }

  load() {
    this.isLoading = true;
    const filter: CostLedgerFilter = {
      department:      this.department?.trim() || undefined,
      dateFrom:        this.dateFrom?.toISOString(),
      dateTo:          this.dateTo?.toISOString(),
      transactionType: this.transactionType || undefined,
      page:            this.page,
      pageSize:        this.pageSize
    };
    this.materialService.getDepartmentCostLedger(filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.entries = res.result.data;
          this.total   = res.result.totalRecords;
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.messageService.add({
          severity: 'error', summary: 'Error',
          detail: err?.error?.message ?? 'Failed to load cost ledger.'
        });
      }
    });
  }

  onLazyLoad(event: any) {
    this.page     = Math.floor(event.first / event.rows) + 1;
    this.pageSize = event.rows;
    this.load();
  }

  applyFilters() {
    this.page = 1;
    this.load();
  }

  resetFilters() {
    this.department      = '';
    this.dateFrom        = null;
    this.dateTo          = null;
    this.transactionType = '';
    this.page = 1;
    this.load();
  }

  getTxSeverity(t: string): 'success' | 'warn' | 'danger' | 'secondary' {
    switch (t) {
      case 'ISSUE':   return 'success';
      case 'RETURN':  return 'warn';
      case 'WASTAGE': return 'danger';
      default:        return 'secondary';
    }
  }
}
