import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { AccordionModule } from 'primeng/accordion';
import { MessageService } from 'primeng/api';
import { WorkflowService, DocumentApprovalHistoryItem } from '../../../services/workflow.service';

@Component({
  selector: 'app-document-history',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, ToastModule, AccordionModule
  ],
  templateUrl: './document-history.component.html',
  styleUrls: ['./document-history.component.scss'],
  providers: [MessageService]
})
export class DocumentHistoryComponent implements OnInit {
  documentId  = '';
  history: DocumentApprovalHistoryItem[] = [];
  isLoading   = true;
  documentNumber = '';

  constructor(
    private route: ActivatedRoute,
    private wfService: WorkflowService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.documentId = this.route.snapshot.paramMap.get('id') ?? '';
    this.load();
  }

  load() {
    this.isLoading = true;
    this.wfService.getHistory({ documentId: this.documentId }).subscribe({
      next: res => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.history = res.result;
          if (this.history.length) this.documentNumber = this.history[0].documentNumber;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load document history.' });
      }
    });
  }

  getStatusSeverity(status: string): 'success' | 'danger' | 'warn' | 'info' | 'secondary' {
    const map: Record<string, any> = {
      APPROVED: 'success', COMPLETED: 'success',
      REJECTED: 'danger',
      PENDING: 'warn', IN_PROGRESS: 'warn', ESCALATED: 'danger',
      RECALLED: 'secondary', CANCELLED: 'secondary', SKIPPED: 'secondary'
    };
    return map[status] ?? 'secondary';
  }

  getStepStatusIcon(status: string): string {
    const icons: Record<string, string> = {
      APPROVED: 'pi pi-check-circle', REJECTED: 'pi pi-times-circle',
      SKIPPED: 'pi pi-forward', RECALLED: 'pi pi-replay',
      PENDING: 'pi pi-clock', ESCALATED: 'pi pi-exclamation-triangle'
    };
    return icons[status] ?? 'pi pi-circle';
  }
}
