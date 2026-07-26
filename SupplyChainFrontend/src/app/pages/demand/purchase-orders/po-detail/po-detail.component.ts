import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TableModule } from 'primeng/table';
import { DividerModule } from 'primeng/divider';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { TextareaModule } from 'primeng/textarea';
import { DropdownModule } from 'primeng/dropdown';
import { MessageService, ConfirmationService } from 'primeng/api';
import { DemandService, PoDetailModel } from '../../../../services/demand.service';
import { WorkflowService, ApprovalDetailDto } from '../../../../services/workflow.service';
import { SupplierService, EligibleContactModel } from '../../../../services/supplier.service';
import { TimelinePanelComponent } from '../../../../shared/timeline-panel/timeline-panel.component';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';

@Component({
  selector: 'app-po-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, ToastModule,
    TableModule, DividerModule, TooltipModule,
    ConfirmDialogModule, DialogModule, TextareaModule, DropdownModule, TimelinePanelComponent,
    AttachmentListComponent
  ],
  templateUrl: './po-detail.component.html',
  styleUrls: ['./po-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class PoDetailComponent implements OnInit, OnDestroy {
  uuid  = '';
  showTimeline = false;
  po: PoDetailModel | null = null;
  isLoading    = true;
  isSubmitting = false;
  isApproving  = false;
  isRejecting  = false;
  isSending    = false;
  isDownloadingPdf = false;

  showRejectDialog = false;
  rejectReason     = '';

  showSendDialog      = false;
  isLoadingSendContacts = false;
  sendContacts: EligibleContactModel[] = [];
  selectedSendContact: EligibleContactModel | null = null;
  hasSendableContact  = false;

  approvalDetail: ApprovalDetailDto | null = null;
  isLoadingApproval = false;

  showPreviewDialog = false;
  isLoadingPreview  = false;
  previewPdfUrl: SafeResourceUrl | null = null;
  private previewObjectUrl: string | null = null;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private demandService: DemandService,
    private wfService: WorkflowService,
    private supplierService: SupplierService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    private sanitizer: DomSanitizer
  ) {}

  ngOnInit() {
    this.route.params.subscribe(p => { this.uuid = p['uuid']; this.load(); });
  }

  ngOnDestroy() {
    this.revokePreviewUrl();
  }

  load() {
    this.isLoading = true;
    this.approvalDetail = null;
    this.demandService.getPoById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.po = res.success ? res.result : null;
        if (this.po?.status === 'PENDING_APPROVAL') {
          this.loadWorkflowStatus();
        }
      },
      error: (err: any) => {
        this.isLoading = false;
        const detail = err?.error?.message ?? `Failed to load purchase order. (HTTP ${err?.status ?? 0})`;
        this.messageService.add({ severity: 'error', summary: 'Error', detail });
      }
    });
  }

  private loadWorkflowStatus() {
    this.isLoadingApproval = true;
    this.wfService.getHistory({ documentId: this.uuid }).subscribe({
      next: res => {
        if (res.success && res.result?.length) {
          const done = ['APPROVED', 'REJECTED', 'RECALLED', 'CANCELLED'];
          const active = [...res.result]
            .sort((a, b) => b.revisionNo - a.revisionNo)
            .find(h => !done.includes(h.status));
          if (active) {
            this.wfService.getApprovalDetail(active.uuid).subscribe({
              next: dr => {
                this.isLoadingApproval = false;
                if (dr.success) this.approvalDetail = dr.result;
              },
              error: () => { this.isLoadingApproval = false; }
            });
          } else {
            this.isLoadingApproval = false;
          }
        } else {
          this.isLoadingApproval = false;
        }
      },
      error: () => { this.isLoadingApproval = false; }
    });
  }

  confirmSubmit() {
    this.confirmationService.confirm({
      message: `Submit PO <strong>${this.po?.poNumber}</strong> for approval?`,
      header: 'Submit for Approval',
      icon: 'pi pi-send',
      acceptLabel: 'Submit',
      rejectLabel: 'Cancel',
      acceptButtonStyleClass: 'p-button-primary',
      accept: () => this.submitPo()
    });
  }

  submitPo() {
    this.isSubmitting = true;
    this.demandService.submitPo(this.uuid).subscribe({
      next: (res: any) => {
        this.isSubmitting = false;
        if (res?.success === false) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to submit PO.' });
          return;
        }
        this.showPreviewDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Submitted', detail: 'Purchase order submitted for approval.' });
        setTimeout(() => this.load(), 800);
      },
      error: (err: any) => {
        this.isSubmitting = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to submit PO.' });
      }
    });
  }

  confirmApprove() {
    this.confirmationService.confirm({
      message: `Approve PO <strong>${this.po?.poNumber}</strong>? This will allow it to be sent to the supplier.`,
      header: 'Approve Purchase Order',
      icon: 'pi pi-check-circle',
      acceptLabel: 'Approve',
      rejectLabel: 'Cancel',
      acceptButtonStyleClass: 'p-button-success',
      accept: () => this.approvePo()
    });
  }

  openRejectDialog() {
    this.rejectReason = '';
    this.showRejectDialog = true;
  }

  submitReject() {
    if (!this.rejectReason.trim()) return;
    this.showRejectDialog = false;
    this.isRejecting = true;
    this.demandService.rejectPo(this.uuid, this.rejectReason.trim()).subscribe({
      next: (res: any) => {
        this.isRejecting = false;
        if (res?.success === false) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to reject PO.' });
          return;
        }
        this.messageService.add({ severity: 'info', summary: 'Rejected', detail: 'Purchase order rejected.' });
        setTimeout(() => this.load(), 800);
      },
      error: (err: any) => {
        this.isRejecting = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to reject PO.' });
      }
    });
  }

  private approvePo() {
    this.isApproving = true;
    this.demandService.approvePo(this.uuid).subscribe({
      next: (res: any) => {
        this.isApproving = false;
        if (res?.success === false) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to approve PO.' });
          return;
        }
        this.messageService.add({ severity: 'success', summary: 'Approved', detail: 'Purchase order approved.' });
        setTimeout(() => this.load(), 800);
      },
      error: (err: any) => {
        this.isApproving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to approve PO.' });
      }
    });
  }

  confirmSend() {
    if (!this.po) return;
    this.showSendDialog      = true;
    this.isLoadingSendContacts = true;
    this.sendContacts        = [];
    this.selectedSendContact = null;
    this.hasSendableContact  = false;

    this.supplierService.getEligibleContacts(this.po.supplierId).subscribe({
      next: res => {
        this.isLoadingSendContacts = false;
        if (res.success && res.result) {
          this.hasSendableContact  = res.result.hasUsableContact;
          this.sendContacts        = res.result.contacts;
          const primary = res.result.contacts.find(c => c.isPrimary && c.isMobileValid)
                       ?? res.result.contacts.find(c => c.isMobileValid);
          this.selectedSendContact = primary ?? null;
        }
      },
      error: () => {
        this.isLoadingSendContacts = false;
        this.hasSendableContact    = false;
      }
    });
  }

  sendPo() {
    if (!this.selectedSendContact?.isMobileValid) return;
    this.showSendDialog = false;
    this.isSending = true;
    this.demandService.sendPo(this.uuid, this.selectedSendContact.normalisedMobile ?? undefined).subscribe({
      next: (res: any) => {
        this.isSending = false;
        if (res?.success === false) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to send PO.' });
          return;
        }
        this.messageService.add({ severity: 'success', summary: 'Sent', detail: 'Purchase order sent to supplier. WhatsApp notification queued.' });
        setTimeout(() => this.load(), 800);
      },
      error: (err: any) => {
        this.isSending = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to send PO.' });
      }
    });
  }

  getStepSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' {
    switch (s) {
      case 'APPROVED':  return 'success';
      case 'REJECTED':  return 'danger';
      case 'PENDING':   return 'warn';
      case 'SKIPPED':   return 'info';
      default:          return 'secondary';
    }
  }

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'APPROVED':            return 'success';
      case 'SENT':                return 'warn';
      case 'PARTIALLY_RECEIVED':  return 'info';
      case 'RECEIVED':            return 'success';
      case 'PARTIALLY_INVOICED':  return 'warn';
      case 'CLOSED':              return 'info';
      case 'DRAFT':               return 'secondary';
      case 'PENDING_APPROVAL':    return 'warn';
      case 'REJECTED':            return 'danger';
      case 'CANCELLED':           return 'danger';
      default:                    return 'secondary';
    }
  }

  openPreview() {
    if (!this.po) return;
    this.showPreviewDialog = true;
    this.isLoadingPreview  = true;
    this.previewPdfUrl     = null;
    this.demandService.downloadPoPdf(this.uuid).subscribe({
      next: (blob) => {
        this.isLoadingPreview = false;
        this.revokePreviewUrl();
        this.previewObjectUrl = URL.createObjectURL(blob);
        this.previewPdfUrl = this.sanitizer.bypassSecurityTrustResourceUrl(this.previewObjectUrl);
      },
      error: () => {
        this.isLoadingPreview = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to generate PO preview.' });
      }
    });
  }

  closePreview() {
    this.showPreviewDialog = false;
    this.revokePreviewUrl();
    this.previewPdfUrl = null;
  }

  private revokePreviewUrl() {
    if (this.previewObjectUrl) {
      URL.revokeObjectURL(this.previewObjectUrl);
      this.previewObjectUrl = null;
    }
  }

  downloadPdf() {
    if (!this.po) return;
    this.isDownloadingPdf = true;
    this.demandService.downloadPoPdf(this.uuid).subscribe({
      next: (blob) => {
        this.isDownloadingPdf = false;
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `PO-${this.po?.poNumber || this.uuid}.pdf`;
        a.click();
        URL.revokeObjectURL(url);
      },
      error: () => {
        this.isDownloadingPdf = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to generate PO PDF.' });
      }
    });
  }

  get totalAmount(): number {
    return (this.po?.lines ?? []).reduce((s, l) => s + l.lineTotal, 0);
  }
}
