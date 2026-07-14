import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { ToastModule } from 'primeng/toast';
import { CalendarModule } from 'primeng/calendar';
import { MessageService } from 'primeng/api';
import { ReportsService, UserActivityItem } from '../../../services/reports.service';
import { PdfService } from '../../../services/pdf.service';

@Component({
  selector: 'app-user-activity',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, ToastModule, CalendarModule],
  templateUrl: './user-activity.component.html',
  styleUrls: ['./user-activity.component.scss'],
  providers: [MessageService]
})
export class UserActivityComponent implements OnInit {
  items: UserActivityItem[] = [];
  isLoading = true;
  dateFrom: Date | null = null;
  dateTo:   Date | null = null;

  constructor(private reports: ReportsService, private msg: MessageService, private pdf: PdfService) {}
  ngOnInit(): void { this.load(); }

  load(): void {
    this.isLoading = true;
    this.reports.getUserActivity({
      dateFrom: this.dateFrom ? this.toIsoDate(this.dateFrom) : undefined,
      dateTo:   this.dateTo   ? this.toIsoDate(this.dateTo)   : undefined
    }).subscribe({
      next:  r => { this.isLoading = false; this.items = r.success ? r.result : []; },
      error: () => { this.isLoading = false; this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load user activity.' }); }
    });
  }

  resetFilters(): void {
    this.dateFrom = null;
    this.dateTo   = null;
    this.load();
  }

  downloadPdf(): void {
    this.pdf.downloadTableReport({
      title: 'User Activity Report',
      fileName: 'user-activity',
      columns: ['User', 'Total Actions', 'Created', 'Updated', 'Deleted', 'Approved', 'Last Action'],
      rows: this.items.map(r => [
        r.userName ?? `User #${r.userId}`, r.totalActions,
        r.createCount, r.updateCount, r.deleteCount, r.approveCount,
        r.lastActionAt ? new Date(r.lastActionAt).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) : '—'
      ]),
      dateFilter: { from: this.dateFrom?.toLocaleDateString('en-GB') ?? '', to: this.dateTo?.toLocaleDateString('en-GB') ?? '' },
      accentColor: [100, 116, 139]
    });
  }

  private toIsoDate(d: Date): string {
    return d.toISOString().split('T')[0];
  }
}
