import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { CardModule } from 'primeng/card';
import { MessageService } from 'primeng/api';
import { ReportsService, PendingApprovalItem } from '../../../services/reports.service';
import { PdfService } from '../../../services/pdf.service';

@Component({
  selector: 'app-pending-approvals',
  standalone: true,
  imports: [CommonModule, TableModule, ButtonModule, TagModule, ToastModule, CardModule],
  templateUrl: './pending-approvals.component.html',
  styleUrls: ['./pending-approvals.component.scss'],
  providers: [MessageService]
})
export class PendingApprovalsComponent implements OnInit {
  items: PendingApprovalItem[] = [];
  isLoading = true;

  constructor(private reports: ReportsService, private msg: MessageService, private pdf: PdfService) {}
  ngOnInit(): void { this.load(); }

  load(): void {
    this.isLoading = true;
    this.reports.getPendingApprovals().subscribe({
      next: r => { this.isLoading = false; this.items = r.success ? r.result : []; },
      error: () => { this.isLoading = false; this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load' }); }
    });
  }

  downloadPdf(): void {
    this.pdf.downloadTableReport({
      title: 'Pending Approvals Report',
      fileName: 'pending-approvals',
      columns: ['Module', 'Type', 'Number', 'Status', 'Created Date', 'Description'],
      rows: this.items.map(r => [
        r.module, r.entityType, r.entityNumber, r.status,
        new Date(r.createdDate).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }),
        r.description ?? '—'
      ]),
      accentColor: [168, 85, 247]
    });
  }
}
