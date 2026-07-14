import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { TabViewModule } from 'primeng/tabview';
import { ToastModule } from 'primeng/toast';
import { TagModule } from 'primeng/tag';
import { CalendarModule } from 'primeng/calendar';
import { MessageService } from 'primeng/api';
import { ReportsService, PoSummaryItem, SpendBySupplierItem } from '../../../services/reports.service';
import { PdfService } from '../../../services/pdf.service';

@Component({
  selector: 'app-po-reports',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, TabViewModule, ToastModule, TagModule, CalendarModule],
  templateUrl: './po-reports.component.html',
  styleUrls: ['./po-reports.component.scss'],
  providers: [MessageService]
})
export class PoReportsComponent implements OnInit {
  poSummary:  PoSummaryItem[]       = [];
  spendItems: SpendBySupplierItem[] = [];
  isLoadingSummary = true;
  isLoadingSpend   = true;
  dateFrom: Date | null = null;
  dateTo:   Date | null = null;

  constructor(private reports: ReportsService, private msg: MessageService, private pdf: PdfService) {}
  ngOnInit(): void { this.load(); }

  load(): void {
    const filter = {
      dateFrom: this.dateFrom ? this.toIsoDate(this.dateFrom) : undefined,
      dateTo:   this.dateTo   ? this.toIsoDate(this.dateTo)   : undefined
    };
    this.isLoadingSummary = true;
    this.isLoadingSpend   = true;

    this.reports.getPoSummary(filter).subscribe({
      next:  r => { this.isLoadingSummary = false; this.poSummary = r.success ? r.result : []; },
      error: () => { this.isLoadingSummary = false; }
    });
    this.reports.getSpendBySupplier(filter).subscribe({
      next:  r => { this.isLoadingSpend = false; this.spendItems = r.success ? r.result : []; },
      error: () => { this.isLoadingSpend = false; }
    });
  }

  resetFilters(): void {
    this.dateFrom = null;
    this.dateTo   = null;
    this.load();
  }

  downloadPoSummary(): void {
    this.pdf.downloadTableReport({
      title: 'PO Summary Report',
      fileName: 'po-summary',
      columns: ['Status', 'Count', 'Total Value (PKR)'],
      rows: this.poSummary.map(r => [r.status, r.count, r.totalValue.toFixed(2)]),
      totalsRow: ['TOTAL',
        this.poSummary.reduce((s, r) => s + r.count, 0),
        this.poSummary.reduce((s, r) => s + r.totalValue, 0).toFixed(2)
      ],
      dateFilter: { from: this.dateFrom?.toLocaleDateString('en-GB') ?? '', to: this.dateTo?.toLocaleDateString('en-GB') ?? '' },
      accentColor: [59, 130, 246]
    });
  }

  downloadSpend(): void {
    this.pdf.downloadTableReport({
      title: 'Spend by Supplier Report',
      fileName: 'spend-by-supplier',
      columns: ['Supplier', 'POs', 'Total Spend (PKR)', 'Latest PO Date'],
      rows: this.spendItems.map(r => [
        r.supplierName, r.poCount,
        r.totalSpend.toFixed(2),
        r.latestPoDate ? new Date(r.latestPoDate).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) : '—'
      ]),
      totalsRow: ['TOTAL', this.spendItems.reduce((s, r) => s + r.poCount, 0), this.spendItems.reduce((s, r) => s + r.totalSpend, 0).toFixed(2), ''],
      dateFilter: { from: this.dateFrom?.toLocaleDateString('en-GB') ?? '', to: this.dateTo?.toLocaleDateString('en-GB') ?? '' },
      accentColor: [59, 130, 246]
    });
  }

  private toIsoDate(d: Date): string {
    return d.toISOString().split('T')[0];
  }
}
