import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { DropdownModule } from 'primeng/dropdown';
import { DialogModule } from 'primeng/dialog';
import { TextareaModule } from 'primeng/textarea';
import { InputTextModule } from 'primeng/inputtext';
import { CalendarModule } from 'primeng/calendar';
import { BadgeModule } from 'primeng/badge';
import { MessageService } from 'primeng/api';
import { TableLazyLoadEvent } from 'primeng/table';
import { WorkflowService, InboxItemDto } from '../../../services/workflow.service';

export interface SlaStatus {
  label: string;
  cssClass: string;
  pct: number;
}

@Component({
  selector: 'app-approval-inbox',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule,
    ToastModule, TooltipModule, DropdownModule,
    DialogModule, TextareaModule, InputTextModule,
    CalendarModule, BadgeModule
  ],
  templateUrl: './approval-inbox.component.html',
  styleUrls: ['./approval-inbox.component.scss'],
  providers: [MessageService]
})
export class ApprovalInboxComponent implements OnInit, OnDestroy {
  items: InboxItemDto[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = false;
  pendingCount = 0;

  filterCode     = '';
  filterFromDate: Date | null = null;
  filterToDate:   Date | null = null;

  interfaceCodeOptions = [
    { label: 'All Interfaces', value: '' },
    { label: 'PR',   value: 'PR'   },
    { label: 'PO',   value: 'PO'   },
    { label: 'GRN',  value: 'GRN'  },
    { label: 'SRO',  value: 'SRO'  },
    { label: 'QUO',  value: 'QUO'  },
    { label: 'INV',  value: 'INV'  },
    { label: 'PAY',  value: 'PAY'  },
    { label: 'SHIP', value: 'SHIP' }
  ];

  // ── Approve dialog ────────────────────────────────────────────────────────
  showApproveDialog = false;
  approveItem: InboxItemDto | null = null;
  approveRemarks = '';
  isApproving = false;

  // ── Reject dialog ─────────────────────────────────────────────────────────
  showRejectDialog  = false;
  rejectItem: InboxItemDto | null = null;
  rejectReason = '';
  isRejecting  = false;

  private slaTimer: ReturnType<typeof setInterval> | null = null;
  now = new Date();

  constructor(
    private wfService: WorkflowService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.load();
    this.loadCount();
    // Refresh SLA countdowns every 60 seconds
    this.slaTimer = setInterval(() => { this.now = new Date(); }, 60_000);
  }

  ngOnDestroy() {
    if (this.slaTimer) clearInterval(this.slaTimer);
  }

  load() {
    this.isLoading = true;
    const filter: any = { page: this.currentPage, pageSize: this.pageSize };
    if (this.filterCode)      filter.interfaceCode = this.filterCode;
    if (this.filterFromDate)  filter.fromDate = this.filterFromDate.toISOString();
    if (this.filterToDate)    filter.toDate   = this.filterToDate.toISOString();

    this.wfService.getInbox(filter).subscribe({
      next: res => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.items        = res.result.data ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load inbox.' });
      }
    });
  }

  loadCount() {
    this.wfService.getInboxCount().subscribe({
      next: res => { if (res.success && res.result) this.pendingCount = res.result.count; },
      error: () => {}
    });
  }

  onFilterChange() { this.currentPage = 1; this.load(); }
  resetFilters() {
    this.filterCode = ''; this.filterFromDate = null; this.filterToDate = null;
    this.currentPage = 1; this.load();
  }

  onPageChange(e: TableLazyLoadEvent) {
    const rows = e.rows ?? this.pageSize;
    this.currentPage = Math.floor((e.first ?? 0) / rows) + 1;
    this.pageSize = rows;
    this.load();
  }

  // ── SLA countdown ─────────────────────────────────────────────────────────

  getSla(item: InboxItemDto): SlaStatus {
    const dueAt = item.dueAt;
    if (!dueAt) return { label: '—', cssClass: '', pct: 0 };

    const due     = new Date(dueAt).getTime();
    const start   = new Date(item.submittedAt).getTime();
    const nowMs   = this.now.getTime();
    const diffMs  = due - nowMs;

    if (diffMs <= 0) return { label: 'OVERDUE', cssClass: 'sla-overdue', pct: 100 };

    const total = due - start;
    const pct   = total > 0 ? Math.round(((nowMs - start) / total) * 100) : 0;

    const diffHours = diffMs / 3_600_000;
    let label: string;
    if (diffHours < 1) {
      label = `${Math.round(diffMs / 60_000)}m`;
    } else if (diffHours < 24) {
      label = `${Math.round(diffHours)}h`;
    } else {
      label = `${Math.round(diffHours / 24)}d`;
    }

    let cssClass = 'sla-green';
    if (pct >= 80) cssClass = 'sla-red';
    else if (pct >= 50) cssClass = 'sla-amber';

    return { label, cssClass, pct };
  }

  // ── Approve flow ──────────────────────────────────────────────────────────

  openApprove(item: InboxItemDto) {
    this.approveItem    = item;
    this.approveRemarks = '';
    this.showApproveDialog = true;
  }

  submitApprove() {
    if (!this.approveItem) return;
    this.isApproving = true;
    this.wfService.approve(this.approveItem.approvalUUID, this.approveRemarks || undefined).subscribe({
      next: res => {
        this.isApproving = false;
        if (res.success) {
          this.showApproveDialog = false;
          this.messageService.add({ severity: 'success', summary: 'Approved', detail: 'Approval recorded successfully.' });
          this.load(); this.loadCount();
        }
      },
      error: () => {
        this.isApproving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Approval failed. Please try again.' });
      }
    });
  }

  // ── Reject flow ───────────────────────────────────────────────────────────

  openReject(item: InboxItemDto) {
    this.rejectItem   = item;
    this.rejectReason = '';
    this.showRejectDialog = true;
  }

  submitReject() {
    if (!this.rejectItem || !this.rejectReason.trim()) {
      this.messageService.add({ severity: 'warn', summary: 'Required', detail: 'Please enter a rejection reason.' });
      return;
    }
    this.isRejecting = true;
    this.wfService.reject(this.rejectItem.approvalUUID, this.rejectReason).subscribe({
      next: res => {
        this.isRejecting = false;
        if (res.success) {
          this.showRejectDialog = false;
          this.messageService.add({ severity: 'info', summary: 'Rejected', detail: 'Document rejected.' });
          this.load(); this.loadCount();
        }
      },
      error: () => {
        this.isRejecting = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Rejection failed.' });
      }
    });
  }

  getInterfaceSeverity(code: string): 'info' | 'success' | 'warn' | 'secondary' | 'danger' {
    const map: Record<string, 'info' | 'success' | 'warn' | 'secondary' | 'danger'> = {
      PR: 'info', PO: 'success', GRN: 'warn', SRO: 'danger', QUO: 'secondary'
    };
    return map[code] ?? 'secondary';
  }
}
