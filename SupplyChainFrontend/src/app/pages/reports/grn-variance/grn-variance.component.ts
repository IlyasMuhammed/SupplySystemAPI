import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { ToastModule } from 'primeng/toast';
import { TagModule } from 'primeng/tag';
import { CalendarModule } from 'primeng/calendar';
import { MessageService } from 'primeng/api';
import { ReportsService, GrnVarianceItem } from '../../../services/reports.service';
import { PdfService } from '../../../services/pdf.service';

@Component({
  selector: 'app-grn-variance',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, InputTextModule, ToastModule, TagModule, CalendarModule],
  templateUrl: './grn-variance.component.html',
  styleUrls: ['./grn-variance.component.scss'],
  providers: [MessageService]
})
export class GrnVarianceComponent implements OnInit {
  items: GrnVarianceItem[] = [];
  isLoading = true;
  dateFrom: Date | null = null;
  dateTo: Date | null = null;

  constructor(private reports: ReportsService, private msg: MessageService, private pdf: PdfService) {}

  ngOnInit(): void { this.load(); }

  load(): void {
    this.isLoading = true;
    this.reports.getGrnVariance({
      dateFrom: this.dateFrom ? this.toIsoDate(this.dateFrom) : undefined,
      dateTo:   this.dateTo   ? this.toIsoDate(this.dateTo)   : undefined
    }).subscribe({
      next:  r => { this.isLoading = false; this.items = r.success ? r.result : []; },
      error: () => {
        this.isLoading = false;
        this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load variance data.' });
      }
    });
  }

  resetFilters(): void {
    this.dateFrom = null;
    this.dateTo   = null;
    this.load();
  }

  getStatusSeverity(status: string): 'success' | 'warn' | 'danger' | 'info' | 'secondary' {
    switch ((status || '').toLowerCase()) {
      case 'completed':  return 'success';
      case 'partial':    return 'warn';
      case 'rejected':   return 'danger';
      case 'pending':    return 'info';
      default:           return 'secondary';
    }
  }

  downloadPdf(): void {
    this.pdf.downloadTableReport({
      title: 'GRN Variance Report',
      fileName: 'grn-variance',
      columns: ['GRN #', 'PO #', 'Supplier', 'Received At', 'Ordered', 'Received', 'Accepted', 'Rejected', 'Variance', 'Status'],
      rows: this.items.map(r => [
        r.grnNumber, r.poNumber, r.supplierName,
        new Date(r.receivedAt).toLocaleString('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false }),
        r.totalOrdered, r.totalReceived, r.totalAccepted, r.totalRejected, r.varianceQty, r.status
      ]),
      dateFilter: { from: this.dateFrom?.toLocaleDateString('en-GB') ?? '', to: this.dateTo?.toLocaleDateString('en-GB') ?? '' },
      accentColor: [245, 158, 11]
    });
  }

  private toIsoDate(d: Date): string {
    return d.toISOString().split('T')[0];
  }
}
