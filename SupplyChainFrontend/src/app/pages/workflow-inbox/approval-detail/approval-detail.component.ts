import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, ActivatedRoute, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { DialogModule } from 'primeng/dialog';
import { TextareaModule } from 'primeng/textarea';
import { DividerModule } from 'primeng/divider';
import { TabViewModule } from 'primeng/tabview';
import { TimelineModule } from 'primeng/timeline';
import { CardModule } from 'primeng/card';
import { MessageService } from 'primeng/api';
import {
  WorkflowService,
  ApprovalDetailDto,
  AuditLogEntryDto,
  StepDetailDto,
  DocumentApprovalHistoryItem
} from '../../../services/workflow.service';

interface StepGroup {
  stepNumber: number;
  stepName: string;
  approvalMode: string;
  assignedRole: string | null;
  canReject: boolean;
  isCurrentStep: boolean;
  isEscalated: boolean;
  dueAt: string | null;
  status: string;
  rows: StepDetailDto[];
}

@Component({
  selector: 'app-approval-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, ToastModule,
    TooltipModule, DialogModule, TextareaModule,
    DividerModule, TabViewModule, TimelineModule, CardModule
  ],
  templateUrl: './approval-detail.component.html',
  styleUrls: ['./approval-detail.component.scss'],
  providers: [MessageService]
})
export class ApprovalDetailComponent implements OnInit {
  uuid         = '';
  detail: ApprovalDetailDto | null = null;
  auditLog: AuditLogEntryDto[] = [];
  history: DocumentApprovalHistoryItem[] = [];

  isLoadingDetail = true;
  isLoadingAudit  = false;
  isLoadingHistory = false;

  // ── Approve ────────────────────────────────────────────────────────────────
  showApproveDialog = false;
  approveRemarks    = '';
  isApproving       = false;

  // ── Reject ─────────────────────────────────────────────────────────────────
  showRejectDialog = false;
  rejectReason     = '';
  isRejecting      = false;

  // ── Recall ─────────────────────────────────────────────────────────────────
  showRecallDialog = false;
  recallReason     = '';
  isRecalling      = false;

  activeTab = 0;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private wfService: WorkflowService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.loadDetail();
  }

  loadDetail() {
    this.isLoadingDetail = true;
    this.wfService.getApprovalDetail(this.uuid).subscribe({
      next: res => {
        this.isLoadingDetail = false;
        if (res.success && res.result) this.detail = res.result;
        else this.messageService.add({ severity: 'error', summary: 'Not found', detail: 'Approval not found.' });
      },
      error: () => {
        this.isLoadingDetail = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load approval.' });
      }
    });
  }

  onTabChange(e: any) {
    this.activeTab = e.index;
    if (e.index === 1 && !this.auditLog.length) this.loadAudit();
    if (e.index === 2 && !this.history.length)  this.loadHistory();
  }

  loadAudit() {
    this.isLoadingAudit = true;
    this.wfService.getApprovalAudit(this.uuid).subscribe({
      next: res => {
        this.isLoadingAudit = false;
        if (res.success && res.result) this.auditLog = res.result;
      },
      error: () => { this.isLoadingAudit = false; }
    });
  }

  loadHistory() {
    if (!this.detail) return;
    this.isLoadingHistory = true;
    this.wfService.getHistory({ documentId: this.detail.documentId, interfaceCode: this.detail.interfaceCode }).subscribe({
      next: res => {
        this.isLoadingHistory = false;
        if (res.success && res.result) this.history = res.result;
      },
      error: () => { this.isLoadingHistory = false; }
    });
  }

  // ── Approve ────────────────────────────────────────────────────────────────

  get canApprove(): boolean {
    return this.detail?.status === 'PENDING' || this.detail?.status === 'IN_PROGRESS';
  }

  get canReject(): boolean {
    if (!this.detail) return false;
    const currentStep = this.detail.steps.find(s => s.isCurrentStep);
    return this.canApprove && (currentStep?.canReject ?? false);
  }

  get canRecall(): boolean {
    return !!this.detail?.allowRecall && this.canApprove;
  }

  submitApprove() {
    this.isApproving = true;
    this.wfService.approve(this.uuid, this.approveRemarks || undefined).subscribe({
      next: res => {
        this.isApproving = false;
        if (res.success) {
          this.showApproveDialog = false;
          this.messageService.add({ severity: 'success', summary: 'Approved', detail: 'Approval recorded.' });
          this.loadDetail();
        }
      },
      error: () => {
        this.isApproving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Approval failed.' });
      }
    });
  }

  submitReject() {
    if (!this.rejectReason.trim()) {
      this.messageService.add({ severity: 'warn', summary: 'Required', detail: 'Rejection reason is required.' });
      return;
    }
    this.isRejecting = true;
    this.wfService.reject(this.uuid, this.rejectReason).subscribe({
      next: res => {
        this.isRejecting = false;
        if (res.success) {
          this.showRejectDialog = false;
          this.messageService.add({ severity: 'info', summary: 'Rejected', detail: 'Document rejected.' });
          this.loadDetail();
        }
      },
      error: () => {
        this.isRejecting = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Rejection failed.' });
      }
    });
  }

  submitRecall() {
    this.isRecalling = true;
    this.wfService.recall(this.uuid, this.recallReason || undefined).subscribe({
      next: res => {
        this.isRecalling = false;
        if (res.success) {
          this.showRecallDialog = false;
          this.messageService.add({ severity: 'warn', summary: 'Recalled', detail: 'Document recalled.' });
          this.loadDetail();
        }
      },
      error: () => {
        this.isRecalling = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Recall failed.' });
      }
    });
  }

  // ── Helpers ────────────────────────────────────────────────────────────────

  getStatusSeverity(status: string): 'success' | 'danger' | 'warn' | 'info' | 'secondary' {
    const map: Record<string, any> = {
      APPROVED: 'success', COMPLETED: 'success',
      REJECTED: 'danger',
      PENDING: 'warn', IN_PROGRESS: 'warn', ESCALATED: 'danger',
      RECALLED: 'secondary', CANCELLED: 'secondary',
      SKIPPED: 'secondary'
    };
    return map[status] ?? 'secondary';
  }

  getStepIcon(step: StepDetailDto): string {
    if (step.isCurrentStep) return 'pi pi-clock';
    const icons: Record<string, string> = {
      APPROVED: 'pi pi-check-circle', REJECTED: 'pi pi-times-circle',
      SKIPPED: 'pi pi-forward', RECALLED: 'pi pi-replay',
      ESCALATED: 'pi pi-exclamation-triangle', PENDING: 'pi pi-circle'
    };
    return icons[step.status] ?? 'pi pi-circle';
  }

  getStepColor(step: StepDetailDto): string {
    if (step.isCurrentStep) return '#f59e0b';
    const colors: Record<string, string> = {
      APPROVED: '#10b981', REJECTED: '#ef4444',
      SKIPPED: '#94a3b8', RECALLED: '#64748b',
      ESCALATED: '#ef4444', PENDING: '#e5e7eb'
    };
    return colors[step.status] ?? '#e5e7eb';
  }

  getAuditIcon(action: string): string {
    const icons: Record<string, string> = {
      SUBMITTED: 'pi pi-send', APPROVED: 'pi pi-check',
      REJECTED: 'pi pi-times', RECALLED: 'pi pi-replay',
      ESCALATED: 'pi pi-arrow-up', DELEGATED: 'pi pi-share-alt',
      REISSUED: 'pi pi-refresh', CANCELLED: 'pi pi-ban'
    };
    return icons[action] ?? 'pi pi-circle';
  }

  getHistoryStatusSeverity(status: string): any {
    return this.getStatusSeverity(status);
  }

  get currentStep(): StepDetailDto | undefined {
    return this.detail?.steps.find(s => s.isCurrentStep);
  }

  get groupedSteps(): StepGroup[] {
    if (!this.detail) return [];
    const map = new Map<number, StepGroup>();
    for (const step of this.detail.steps) {
      if (!map.has(step.stepNumber)) {
        map.set(step.stepNumber, {
          stepNumber: step.stepNumber,
          stepName: step.stepName,
          approvalMode: step.approvalMode,
          assignedRole: step.assignedRole,
          canReject: step.canReject,
          isCurrentStep: false,
          isEscalated: false,
          dueAt: step.dueAt,
          status: 'PENDING',
          rows: []
        });
      }
      const group = map.get(step.stepNumber)!;
      group.rows.push(step);
      if (step.isCurrentStep) group.isCurrentStep = true;
      if (step.isEscalated)   group.isEscalated   = true;
      if (step.dueAt && !group.dueAt) group.dueAt = step.dueAt;
    }
    for (const group of map.values()) {
      const statuses = group.rows.map(r => r.status);
      if (statuses.some(s => s === 'REJECTED'))
        group.status = 'REJECTED';
      else if (statuses.every(s => s === 'APPROVED' || s === 'SKIPPED'))
        group.status = 'APPROVED';
      else if (group.isCurrentStep)
        group.status = 'PENDING';
      else
        group.status = group.rows[0]?.status ?? 'PENDING';
    }
    return Array.from(map.values()).sort((a, b) => a.stepNumber - b.stepNumber);
  }

  getGroupColor(group: { isCurrentStep: boolean; status: string }): string {
    if (group.isCurrentStep) return '#f59e0b';
    const colors: Record<string, string> = {
      APPROVED: '#10b981', REJECTED: '#ef4444',
      SKIPPED: '#94a3b8', RECALLED: '#64748b',
      ESCALATED: '#ef4444', PENDING: '#e5e7eb'
    };
    return colors[group.status] ?? '#e5e7eb';
  }

  getGroupIcon(group: { isCurrentStep: boolean; status: string }): string {
    if (group.isCurrentStep) return 'pi pi-clock';
    const icons: Record<string, string> = {
      APPROVED: 'pi pi-check-circle', REJECTED: 'pi pi-times-circle',
      SKIPPED: 'pi pi-forward', RECALLED: 'pi pi-replay',
      ESCALATED: 'pi pi-exclamation-triangle', PENDING: 'pi pi-circle'
    };
    return icons[group.status] ?? 'pi pi-circle';
  }
}
