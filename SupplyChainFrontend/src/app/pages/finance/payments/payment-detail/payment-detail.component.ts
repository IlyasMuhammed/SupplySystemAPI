import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { CardModule } from 'primeng/card';
import { DividerModule } from 'primeng/divider';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { FinanceService, PaymentDetailModel } from '../../../../services/finance.service';
import { FLOW } from '../../../../shared/flow';

// Read-only legacy payment history (pre-SFM-003 flow). No write actions here —
// see SupplierPaymentDetailComponent for the live approve/post/bounce lifecycle.
@Component({
  selector: 'app-payment-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, ToastModule,
    CardModule, DividerModule, TooltipModule,
    ...FLOW
  ],
  templateUrl: './payment-detail.component.html',
  providers: [MessageService]
})
export class PaymentDetailComponent implements OnInit {
  payment: PaymentDetailModel | null = null;
  isLoading = true;

  constructor(
    private financeService: FinanceService,
    private messageService: MessageService,
    private route: ActivatedRoute
  ) {}

  ngOnInit() {
    const uuid = this.route.snapshot.paramMap.get('uuid');
    if (uuid) this.load(uuid);
  }

  load(uuid: string) {
    this.isLoading = true;
    this.financeService.getPaymentById(uuid).subscribe({
      next: (res) => { this.isLoading = false; this.payment = res.success ? res.result : null; },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load payment.' });
      }
    });
  }

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' {
    switch (s) {
      case 'Cleared':   return 'success';
      case 'Reversed':  return 'danger';
      case 'Processed': return 'warn';
      default:          return 'secondary';
    }
  }
}
