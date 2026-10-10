import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TextareaModule } from 'primeng/textarea';
import { InputTextModule } from 'primeng/inputtext';
import { DialogModule } from 'primeng/dialog';
import { MessageService } from 'primeng/api';
import { MaterialService, WastageDetail } from '../../../../services/material.service';
import { FLOW, FlowStage, flowStagesFrom } from '../../../../shared/flow';

@Component({
  selector: 'app-wastage-detail',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule,
    ButtonModule, TagModule, ToastModule,
    TextareaModule, InputTextModule, DialogModule,
    ...FLOW
  ],
  templateUrl: './wastage-detail.component.html',
  styleUrls: ['./wastage-detail.component.scss'],
  providers: [MessageService]
})
export class WastageDetailComponent implements OnInit {
  isLoading     = false;
  isApproving   = false;
  isRejecting   = false;
  wastage: WastageDetail | null = null;
  uuid = '';

  showApproveDialog  = false;
  showRejectDialog   = false;
  approvalNotes      = '';
  rejectionReason    = '';

  /** SMS Flow header strip: Recorded → Pending approval → Approved (rejected = red). */
  get stages(): FlowStage[] {
    const w = this.wastage;
    if (!w) return [];
    if (w.status === 'REJECTED') return flowStagesFrom(['Recorded', 'Rejected'], 1, { failed: true });
    return flowStagesFrom(['Recorded', 'Pending approval', 'Approved'], w.status === 'APPROVED' ? 3 : 1);
  }

  constructor(
    private route:           ActivatedRoute,
    private materialService: MaterialService,
    private messageService:  MessageService
  ) {}

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid') || '';
    this.load();
  }

  load() {
    if (!this.uuid) return;
    this.isLoading = true;
    this.materialService.getWastage(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) this.wastage = res.result;
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load wastage record.' });
      }
    });
  }

  openApprove() {
    this.approvalNotes = '';
    this.showApproveDialog = true;
  }

  confirmApprove() {
    this.showApproveDialog = false;
    this.isApproving = true;
    this.materialService.approveWastage(this.uuid, this.approvalNotes || undefined).subscribe({
      next: () => {
        this.isApproving = false;
        this.messageService.add({
          severity: 'success', summary: 'Approved',
          detail: 'Wastage approved. Cost charge posted to project/department.'
        });
        this.load();
      },
      error: (err) => {
        this.isApproving = false;
        this.messageService.add({
          severity: 'error', summary: 'Error',
          detail: err?.error?.message ?? 'Failed to approve wastage.'
        });
      }
    });
  }

  openReject() {
    this.rejectionReason = '';
    this.showRejectDialog = true;
  }

  confirmReject() {
    if (!this.rejectionReason.trim()) {
      this.messageService.add({ severity: 'warn', summary: 'Required', detail: 'Please enter a rejection reason.' });
      return;
    }
    this.showRejectDialog = false;
    this.isRejecting = true;
    this.materialService.rejectWastage(this.uuid, this.rejectionReason).subscribe({
      next: () => {
        this.isRejecting = false;
        this.messageService.add({ severity: 'info', summary: 'Rejected', detail: 'Wastage record rejected.' });
        this.load();
      },
      error: (err) => {
        this.isRejecting = false;
        this.messageService.add({
          severity: 'error', summary: 'Error',
          detail: err?.error?.message ?? 'Failed to reject wastage.'
        });
      }
    });
  }

  getStatusSeverity(s: string): 'warn' | 'success' | 'danger' | 'secondary' {
    switch (s) {
      case 'PENDING_APPROVAL': return 'warn';
      case 'APPROVED':         return 'success';
      case 'REJECTED':         return 'danger';
      default:                 return 'secondary';
    }
  }

  getSourceLabel(s: string): string {
    return s === 'DAMAGED_RETURN' ? 'Damaged Return' : 'Manual';
  }
}
