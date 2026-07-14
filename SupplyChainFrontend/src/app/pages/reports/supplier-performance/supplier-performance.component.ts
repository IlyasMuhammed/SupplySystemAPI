import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { ToastModule } from 'primeng/toast';
import { CalendarModule } from 'primeng/calendar';
import { MessageService } from 'primeng/api';
import { ReportsService, SupplierPerformanceItem } from '../../../services/reports.service';
import { PdfService } from '../../../services/pdf.service';

@Component({
  selector: 'app-supplier-performance',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, ToastModule, CalendarModule],
  templateUrl: './supplier-performance.component.html',
  styleUrls: ['./supplier-performance.component.scss'],
  providers: [MessageService]
})
export class SupplierPerformanceComponent implements OnInit {
  items: SupplierPerformanceItem[] = [];
  isLoading = true;
  dateFrom: Date | null = null;
  dateTo:   Date | null = null;

  constructor(private reports: ReportsService, private msg: MessageService, private pdf: PdfService) {}
  ngOnInit(): void { this.load(); }

  load(): void {
    this.isLoading = true;
    this.reports.getSupplierPerformance({
      dateFrom: this.dateFrom ? this.toIsoDate(this.dateFrom) : undefined,
      dateTo:   this.dateTo   ? this.toIsoDate(this.dateTo)   : undefined
    }).subscribe({
      next:  r => { this.isLoading = false; this.items = r.success ? r.result : []; },
      error: () => { this.isLoading = false; this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load supplier performance data.' }); }
    });
  }

  resetFilters(): void {
    this.dateFrom = null;
    this.dateTo   = null;
    this.load();
  }

  downloadPdf(): void {
    this.pdf.downloadTableReport({
      title: 'Supplier Performance Report',
      fileName: 'supplier-performance',
      columns: ['Supplier', 'POs', 'Total Spend', 'On-Time %', 'Quality Score', 'GRNs', 'Rejected GRNs'],
      rows: this.items.map(r => [
        r.supplierName, r.poCount, r.totalSpend.toFixed(2),
        r.onTimeDeliveryRate.toFixed(1) + '%',
        r.qualityScore.toFixed(1),
        r.grnCount, r.rejectedGrnCount
      ]),
      dateFilter: { from: this.dateFrom?.toLocaleDateString('en-GB') ?? '', to: this.dateTo?.toLocaleDateString('en-GB') ?? '' },
      accentColor: [20, 184, 166]
    });
  }

  private toIsoDate(d: Date): string {
    return d.toISOString().split('T')[0];
  }
}
