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
import {
  ReportsService,
  InvoiceAgingItem, InvoiceAgingBucketSummary,
  PaymentSummaryModel,
  BudgetUtilizationItem
} from '../../../services/reports.service';
import { PdfService } from '../../../services/pdf.service';

@Component({
  selector: 'app-finance-reports',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, TabViewModule, ToastModule, TagModule, CalendarModule],
  templateUrl: './finance-reports.component.html',
  styleUrls: ['./finance-reports.component.scss'],
  providers: [MessageService]
})
export class FinanceReportsComponent implements OnInit {
  agingItems:     InvoiceAgingItem[]          = [];
  agingBuckets:   InvoiceAgingBucketSummary[] = [];
  paymentSummary: PaymentSummaryModel | null  = null;
  budgetItems:    BudgetUtilizationItem[]     = [];

  isLoadingAging   = true;
  isLoadingPayment = true;
  isLoadingBudget  = true;

  dateFrom: Date | null = null;
  dateTo:   Date | null = null;

  constructor(private reports: ReportsService, private msg: MessageService, private pdf: PdfService) {}
  ngOnInit(): void { this.load(); }

  load(): void {
    const filter = {
      dateFrom: this.dateFrom ? this.toIsoDate(this.dateFrom) : undefined,
      dateTo:   this.dateTo   ? this.toIsoDate(this.dateTo)   : undefined
    };
    this.isLoadingAging   = true;
    this.isLoadingPayment = true;
    this.isLoadingBudget  = true;

    this.reports.getInvoiceAging().subscribe({
      next:  r => { this.isLoadingAging = false; if (r.success) { this.agingItems = r.result.items; this.agingBuckets = r.result.buckets; } },
      error: () => { this.isLoadingAging = false; }
    });
    this.reports.getPaymentSummary(filter).subscribe({
      next:  r => { this.isLoadingPayment = false; this.paymentSummary = r.success ? r.result : null; },
      error: () => { this.isLoadingPayment = false; }
    });
    this.reports.getBudgetUtilization(filter).subscribe({
      next:  r => { this.isLoadingBudget = false; this.budgetItems = r.success ? r.result : []; },
      error: () => { this.isLoadingBudget = false; }
    });
  }

  resetFilters(): void {
    this.dateFrom = null;
    this.dateTo   = null;
    this.load();
  }

  downloadAgingPdf(): void {
    this.pdf.downloadTableReport({
      title: 'Invoice Aging Report',
      fileName: 'invoice-aging',
      columns: ['Invoice #', 'Supplier Ref', 'Supplier', 'Due Date', 'Amount', 'Payment Status', 'Days Overdue', 'Bucket'],
      rows: this.agingItems.map(r => [
        r.invoiceNumber, r.supplierInvoiceNo ?? '—', r.supplierName,
        new Date(r.dueDate).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }),
        r.totalAmount.toFixed(2), r.paymentStatus, r.daysOverdue, r.agingBucket
      ]),
      dateFilter: { from: this.dateFrom?.toLocaleDateString('en-GB') ?? '', to: this.dateTo?.toLocaleDateString('en-GB') ?? '' },
      accentColor: [16, 185, 129]
    });
  }

  downloadBudgetPdf(): void {
    this.pdf.downloadTableReport({
      title: 'Budget Utilization Report',
      fileName: 'budget-utilization',
      columns: ['Budget Code', 'Department', 'POs', 'Budget (PKR)', 'Actual Spend', 'Variance', 'Variance %'],
      rows: this.budgetItems.map(r => [
        r.budgetCode ?? '—', r.department ?? '—', r.poCount,
        r.estimatedAmount.toFixed(2), r.actualSpend.toFixed(2),
        r.varianceAmount.toFixed(2), r.variancePercent.toFixed(1) + '%'
      ]),
      totalsRow: ['TOTAL', '', this.budgetItems.reduce((s, r) => s + r.poCount, 0), this.budgetItems.reduce((s, r) => s + r.estimatedAmount, 0).toFixed(2), this.budgetItems.reduce((s, r) => s + r.actualSpend, 0).toFixed(2), this.budgetItems.reduce((s, r) => s + r.varianceAmount, 0).toFixed(2), ''],
      dateFilter: { from: this.dateFrom?.toLocaleDateString('en-GB') ?? '', to: this.dateTo?.toLocaleDateString('en-GB') ?? '' },
      accentColor: [16, 185, 129]
    });
  }

  private toIsoDate(d: Date): string {
    return d.toISOString().split('T')[0];
  }
}
