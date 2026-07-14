import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router, ActivatedRoute } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { MessageService, ConfirmationService } from 'primeng/api';
import { MaterialService, MivDetail } from '../../../../services/material.service';
import { downloadMivPdf } from '../miv-pdf.util';

@Component({
  selector: 'app-miv-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule,
    ButtonModule, TagModule, ToastModule, ConfirmDialogModule
  ],
  templateUrl: './miv-detail.component.html',
  styleUrls: ['./miv-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class MivDetailComponent implements OnInit {
  miv: MivDetail | null = null;
  isLoading  = true;
  isPosting  = false;
  isCancelling = false;

  constructor(
    private route:           ActivatedRoute,
    private router:          Router,
    private materialService: MaterialService,
    private messageService:  MessageService,
    private confirmService:  ConfirmationService
  ) {}

  ngOnInit() {
    const uuid = this.route.snapshot.paramMap.get('uuid')!;
    this.materialService.getMiv(uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) this.miv = res.result;
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load issue voucher.' });
      }
    });
  }

  confirmPost() {
    this.confirmService.confirm({
      message: 'Post this voucher? This will decrement stock quantities and cannot be reversed.',
      header: 'Confirm Post',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      accept: () => this.doPost()
    });
  }

  doPost() {
    if (!this.miv) return;
    this.isPosting = true;
    this.materialService.postMiv(this.miv.uuid).subscribe({
      next: (res) => {
        this.isPosting = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Posted', detail: 'Voucher posted. Stock updated.' });
          if (this.miv) this.miv.status = 'POSTED';
        }
      },
      error: (err) => {
        this.isPosting = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Post failed.' });
      }
    });
  }

  confirmCancel() {
    this.confirmService.confirm({
      message: 'Cancel this draft voucher?',
      header: 'Confirm Cancel',
      icon: 'pi pi-question-circle',
      accept: () => this.doCancel()
    });
  }

  doCancel() {
    if (!this.miv) return;
    this.isCancelling = true;
    this.materialService.cancelMiv(this.miv.uuid).subscribe({
      next: (res) => {
        this.isCancelling = false;
        if (res.success) {
          this.messageService.add({ severity: 'info', summary: 'Cancelled', detail: 'Voucher cancelled.' });
          if (this.miv) this.miv.status = 'CANCELLED';
        }
      },
      error: (err) => {
        this.isCancelling = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Cancel failed.' });
      }
    });
  }

  downloadPdf() {
    if (!this.miv) return;
    downloadMivPdf(this.miv);
  }

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'DRAFT':     return 'secondary';
      case 'POSTED':    return 'success';
      case 'CANCELLED': return 'danger';
      default:          return 'secondary';
    }
  }
}
