import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TabViewModule } from 'primeng/tabview';
import { ToastModule } from 'primeng/toast';
import { CardModule } from 'primeng/card';
import { TagModule } from 'primeng/tag';
import { MessageService } from 'primeng/api';
import { ReportsService, StockLevelItem, ReorderAlertItem, InventoryValuationItem } from '../../../services/reports.service';
import { PdfService } from '../../../services/pdf.service';

@Component({
  selector: 'app-inventory-reports',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, InputTextModule, TabViewModule, ToastModule, CardModule, TagModule],
  templateUrl: './inventory-reports.component.html',
  styleUrls: ['./inventory-reports.component.scss'],
  providers: [MessageService]
})
export class InventoryReportsComponent implements OnInit {
  stockLevels:  StockLevelItem[]          = [];
  reorderAlerts: ReorderAlertItem[]       = [];
  valuation:    InventoryValuationItem[]  = [];
  isLoadingStock    = true;
  isLoadingReorder  = true;
  isLoadingVal      = true;

  constructor(private reports: ReportsService, private msg: MessageService, private pdf: PdfService) {}
  ngOnInit(): void { this.load(); }

  downloadStockLevels(): void {
    this.pdf.downloadTableReport({
      title: 'Stock Levels Report',
      fileName: 'stock-levels',
      columns: ['Product', 'SKU', 'Warehouse', 'On Hand', 'Reserved', 'Available', 'Unit Cost', 'Stock Value'],
      rows: this.stockLevels.map(r => [
        r.productName, r.sku ?? '—', r.warehouseName ?? '—',
        r.qtyOnHand, r.qtyReserved, r.qtyAvailable,
        r.unitCost.toFixed(2), r.stockValue.toFixed(2)
      ]),
      totalsRow: ['', '', 'TOTAL', '', '', '', '', this.stockLevels.reduce((s, r) => s + r.stockValue, 0).toFixed(2)],
      accentColor: [99, 102, 241]
    });
  }

  downloadReorderAlerts(): void {
    this.pdf.downloadTableReport({
      title: 'Reorder Alerts Report',
      fileName: 'reorder-alerts',
      columns: ['Product', 'SKU', 'Warehouse', 'On Hand', 'Reorder Point', 'Reorder Qty', 'Shortfall'],
      rows: this.reorderAlerts.map(r => [
        r.productName, r.sku ?? '—', r.warehouseName ?? '—',
        r.qtyOnHand, r.reorderPoint, r.reorderQty, r.shortfall
      ]),
      accentColor: [239, 68, 68]
    });
  }

  downloadValuation(): void {
    this.pdf.downloadTableReport({
      title: 'Inventory Valuation Report',
      fileName: 'inventory-valuation',
      columns: ['Warehouse', 'Products', 'Total Qty', 'Total Value (PKR)'],
      rows: this.valuation.map(r => [r.warehouseName ?? '—', r.productCount, r.totalQty, r.totalValue.toFixed(2)]),
      totalsRow: ['TOTAL', this.valuation.reduce((s, r) => s + r.productCount, 0), this.valuation.reduce((s, r) => s + r.totalQty, 0), this.valuation.reduce((s, r) => s + r.totalValue, 0).toFixed(2)],
      accentColor: [99, 102, 241]
    });
  }

  load(): void {
    this.isLoadingStock   = true;
    this.isLoadingReorder = true;
    this.isLoadingVal     = true;

    this.reports.getStockLevels().subscribe({
      next: r => { this.isLoadingStock = false; this.stockLevels = r.success ? r.result : []; },
      error: () => { this.isLoadingStock = false; }
    });

    this.reports.getReorderAlerts().subscribe({
      next: r => { this.isLoadingReorder = false; this.reorderAlerts = r.success ? r.result : []; },
      error: () => { this.isLoadingReorder = false; }
    });

    this.reports.getInventoryValuation().subscribe({
      next: r => { this.isLoadingVal = false; this.valuation = r.success ? r.result : []; },
      error: () => { this.isLoadingVal = false; }
    });
  }
}
