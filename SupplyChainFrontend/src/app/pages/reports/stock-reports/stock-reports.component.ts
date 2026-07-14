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
  StockMovementFilter,
  StockMovementItem,
  StockLedgerFilter,
  StockLedgerItem
} from '../../../services/reports.service';

@Component({
  selector: 'app-stock-reports',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, InputTextModule,
            TabViewModule, TagModule, ToastModule, DropdownModule],
  templateUrl: './stock-reports.component.html',
  styleUrls: ['./stock-reports.component.scss'],
  providers: [MessageService]
})
export class StockReportsComponent {
  // Tab 1 — Stock Movement
  movements: StockMovementItem[] = [];
  movementFilter: StockMovementFilter = {};
  isLoadingMovement = false;

  // Tab 2 — Stock Ledger
  ledger: StockLedgerItem[] = [];
  ledgerFilter: StockLedgerFilter = {};
  isLoadingLedger = false;

  txTypeOptions = [
    { label: 'All Types',       value: '' },
    { label: 'GRN Receipt',     value: 'GRN_RECEIPT' },
    { label: 'Material Issue',  value: 'MATERIAL_ISSUE' },
    { label: 'Transfer Out',    value: 'TRANSFER_OUT' },
    { label: 'Transfer In',     value: 'TRANSFER_IN' },
    { label: 'Return',          value: 'RETURN_DISPATCH' },
    { label: 'Stock Adjustment', value: 'STOCK_ADJUSTMENT' }
  ];

  constructor(private svc: ReportsService, private msg: MessageService) {}

  // ── Movement ───────────────────────────────────────────────────────────────

  get totalIn():    number { return this.movements.reduce((s, i) => s + i.quantityIn, 0); }
  get totalOut():   number { return this.movements.reduce((s, i) => s + i.quantityOut, 0); }
  get totalValue(): number { return this.movements.reduce((s, i) => s + i.totalValue, 0); }

  getTxSeverity(type: string): 'success' | 'danger' | 'info' | 'warn' {
    if (type.includes('RECEIPT') || type.includes('IN'))    return 'success';
    if (type.includes('ISSUE')   || type.includes('OUT'))   return 'danger';
    if (type.includes('RETURN'))                             return 'warn';
    return 'info';
  }

  loadMovement(): void {
    this.isLoadingMovement = true;
    this.svc.getStockMovement(this.movementFilter).subscribe({
      next: r => { this.isLoadingMovement = false; this.movements = r.success ? r.result : []; },
      error: () => { this.isLoadingMovement = false; this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load stock movement.' }); }
    });
  }

  clearMovement(): void { this.movementFilter = {}; this.movements = []; }

  // ── Ledger ─────────────────────────────────────────────────────────────────

  loadLedger(): void {
    this.isLoadingLedger = true;
    this.svc.getStockLedger(this.ledgerFilter).subscribe({
      next: r => { this.isLoadingLedger = false; this.ledger = r.success ? r.result : []; },
      error: () => { this.isLoadingLedger = false; this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load stock ledger.' }); }
    });
  }

  clearLedger(): void { this.ledgerFilter = {}; this.ledger = []; }
}
