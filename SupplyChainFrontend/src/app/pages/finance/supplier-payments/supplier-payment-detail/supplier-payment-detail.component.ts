import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { Observable } from 'rxjs';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { CardModule } from 'primeng/card';
import { TableModule } from 'primeng/table';
import { DividerModule } from 'primeng/divider';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { MessageService, ConfirmationService } from 'primeng/api';
import { FinanceService, SupplierPaymentDetailModel } from '../../../../services/finance.service';
import { AuthService } from '../../../service/auth.service';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';

@Component({
  selector: 'app-supplier-payment-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, ToastModule,
    CardModule, TableModule, DividerModule,
    TooltipModule, ConfirmDialogModule, AttachmentListComponent
  ],
  templateUrl: './supplier-payment-detail.component.html',
  styleUrls: ['./supplier-payment-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class SupplierPaymentDetailComponent implements OnInit {
  payment: SupplierPaymentDetailModel | null = null;
  isLoading  = true;
  isActioning = false;

  constructor(
    private financeService: FinanceService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    private route: ActivatedRoute,
    public authService: AuthService
  ) {}

  ngOnInit() {
    const uuid = this.route.snapshot.paramMap.get('uuid');
    if (uuid) this.load(uuid);
  }

  load(uuid: string) {
    this.isLoading = true;
    this.financeService.getSupplierPaymentById(uuid).subscribe({
      next: (res) => { this.isLoading = false; this.payment = res.success ? res.result : null; },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load payment.' });
      }
    });
  }

  confirmApprove() {
    this.confirmationService.confirm({
      message: 'Approve this payment? It can then be posted to the supplier ledger.',
      header: 'Confirm Approval',
      icon: 'pi pi-check-circle',
      accept: () => this.runAction(this.financeService.approveSupplierPayment(this.payment!.uuid), 'Payment approved.')
    });
  }

  confirmCancel() {
    this.confirmationService.confirm({
      message: 'Cancel this payment? It cannot be resumed.',
      header: 'Confirm Cancel',
      icon: 'pi pi-times-circle',
      accept: () => this.runAction(this.financeService.cancelSupplierPayment(this.payment!.uuid), 'Payment cancelled.')
    });
  }

  confirmPost() {
    this.confirmationService.confirm({
      message: 'Post this payment? This writes a ledger entry and updates invoice paid amounts — it cannot be undone except via Bounce (cheque only).',
      header: 'Confirm Post',
      icon: 'pi pi-exclamation-triangle',
      accept: () => this.runAction(this.financeService.postSupplierPayment(this.payment!.uuid), 'Payment posted.')
    });
  }

  confirmBounce() {
    this.confirmationService.confirm({
      message: 'Mark this cheque as bounced? This reverses the ledger entry and the invoice paid amounts.',
      header: 'Confirm Bounce',
      icon: 'pi pi-exclamation-triangle',
      accept: () => this.runAction(this.financeService.bounceSupplierPayment(this.payment!.uuid), 'Payment marked as bounced.')
    });
  }

  private runAction(obs: Observable<any>, successDetail: string) {
    if (!this.payment) return;
    this.isActioning = true;
    const uuid = this.payment.uuid;
    obs.subscribe({
      next: () => {
        this.isActioning = false;
        this.messageService.add({ severity: 'success', summary: 'Success', detail: successDetail });
        this.load(uuid);
      },
      error: (err) => {
        this.isActioning = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Action failed.' });
      }
    });
  }

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'info' | 'secondary' {
    switch (s) {
      case 'POSTED':    return 'success';
      case 'APPROVED':  return 'info';
      case 'BOUNCED':   return 'warn';
      case 'CANCELLED': return 'danger';
      case 'DRAFT':     return 'secondary';
      default:          return 'secondary';
    }
  }
}
