import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TableModule } from 'primeng/table';
import { MessageService } from 'primeng/api';
import { MaterialService, ReturnDetail } from '../../../../services/material.service';
import { downloadReturnPdf } from '../return-pdf.util';

@Component({
  selector: 'app-return-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule,
    ButtonModule, TagModule, ToastModule, TableModule
  ],
  templateUrl: './return-detail.component.html',
  styleUrls: ['./return-detail.component.scss'],
  providers: [MessageService]
})
export class ReturnDetailComponent implements OnInit {
  isLoading     = false;
  isPosting     = false;
  isCancelling  = false;
  isDownloadPdf = false;
  returnDetail: ReturnDetail | null = null;
  uuid = '';

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
    this.materialService.getReturn(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) this.returnDetail = res.result;
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load return voucher.' });
      }
    });
  }

  post() {
    this.isPosting = true;
    this.materialService.postReturn(this.uuid).subscribe({
      next: () => {
        this.isPosting = false;
        this.messageService.add({ severity: 'success', summary: 'Posted', detail: 'Return voucher posted successfully.' });
        this.load();
      },
      error: (err) => {
        this.isPosting = false;
        this.messageService.add({
          severity: 'error', summary: 'Error',
          detail: err?.error?.message ?? 'Failed to post return.'
        });
      }
    });
  }

  cancel() {
    this.isCancelling = true;
    this.materialService.cancelReturn(this.uuid).subscribe({
      next: () => {
        this.isCancelling = false;
        this.messageService.add({ severity: 'info', summary: 'Cancelled', detail: 'Return voucher cancelled.' });
        this.load();
      },
      error: (err) => {
        this.isCancelling = false;
        this.messageService.add({
          severity: 'error', summary: 'Error',
          detail: err?.error?.message ?? 'Failed to cancel.'
        });
      }
    });
  }

  downloadPdf() {
    if (!this.returnDetail) return;
    downloadReturnPdf(this.returnDetail);
  }

  get totalValue(): number {
    return this.returnDetail?.lines.reduce((s, l) => s + l.lineValue, 0) ?? 0;
  }

  get hasDamagedLines(): boolean {
    return this.returnDetail?.status === 'POSTED' &&
           (this.returnDetail?.lines.some(l => l.condition === 'DAMAGED') ?? false);
  }

  getStatusSeverity(s: string): 'warn' | 'success' | 'danger' | 'secondary' {
    switch (s) {
      case 'DRAFT':     return 'warn';
      case 'POSTED':    return 'success';
      case 'CANCELLED': return 'danger';
      default:          return 'secondary';
    }
  }

  getConditionSeverity(c: string): 'success' | 'danger' {
    return c === 'GOOD' ? 'success' : 'danger';
  }
}
