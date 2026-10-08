import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { Observable } from 'rxjs';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TableModule } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { MessageService, ConfirmationService } from 'primeng/api';
import { FinanceService, SupplierPaymentDetailModel } from '../../../../services/finance.service';
import { AuthService } from '../../../service/auth.service';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';
import { TimelinePanelComponent } from '../../../../shared/timeline-panel/timeline-panel.component';
import { DocCurrencyPanelComponent } from '../../../../shared/doc-currency/doc-currency-panel.component';
import { FxDifferenceComponent } from '../../../../shared/doc-currency/fx-difference.component';
import { DocCurrencyInfo, cachedDocCurrency, hasBaseAmounts, missingRateOf } from '../../../../shared/doc-currency/doc-currency';
import { MoneyPipe } from '../../../../shared/money/money.pipe';

@Component({
  selector: 'app-supplier-payment-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, ToastModule,
    TableModule, TooltipModule, ConfirmDialogModule, AttachmentListComponent, TimelinePanelComponent,
    DocCurrencyPanelComponent, FxDifferenceComponent, MoneyPipe
  ],
  templateUrl: './supplier-payment-detail.component.html',
  styleUrls: ['./supplier-payment-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class SupplierPaymentDetailComponent implements OnInit {
  payment: SupplierPaymentDetailModel | null = null;
  isLoading  = true;
  isActioning = false;
  showTimeline = false;

  // ── A35-E-06: currency, rate locked at posting (purchase base), realized exchange differences ──
  readonly moneyCode = { display: 'code' } as const;
  private readonly mapCurrency = cachedDocCurrency((p: SupplierPaymentDetailModel) => ({
    currencyId: p.currencyId, currencyCode: p.currencyCode, exchangeRate: p.exchangeRate,
    baseCurrencyId: p.baseCurrencyId, baseCurrencyCode: p.baseCurrencyCode, rateLockedAt: null
  }));
  get currencyInfo(): DocCurrencyInfo | null { return this.mapCurrency(this.payment); }
  get showAmountBase(): boolean { return hasBaseAmounts(this.currencyInfo) && this.payment?.amountBase != null; }
  get baseCurrencyRef(): string | null { return this.payment?.baseCurrencyId || this.payment?.baseCurrencyCode || null; }
  get hasLineFx(): boolean {
    return (this.payment?.lines ?? []).some(l => l.exchangeDifference !== null && l.exchangeDifference !== undefined);
  }

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

  // ── What the server lets whom do: approve needs PAYMENT_APPROVE; post, bounce and cancel PAYMENT_PROCESS ──

  private get status(): string { return this.payment?.status ?? ''; }
  private can(code: string): boolean { return this.authService.hasPermission(code); }

  get canApprove(): boolean { return this.status === 'DRAFT' && this.can('PAYMENT_APPROVE'); }
  get canPost(): boolean    { return this.status === 'APPROVED' && this.can('PAYMENT_PROCESS'); }
  /** A posted cheque only: bouncing reverses its ledger entry and the invoices' paid amounts. */
  get canBounce(): boolean  { return this.status === 'POSTED' && this.payment?.paymentMethod === 'CHEQUE' && this.can('PAYMENT_PROCESS'); }
  get canCancel(): boolean  { return (this.status === 'DRAFT' || this.status === 'APPROVED') && this.can('PAYMENT_PROCESS'); }

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
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.runAction(this.financeService.approveSupplierPayment(this.payment!.uuid), 'Payment approved.')
    });
  }

  confirmCancel() {
    this.confirmationService.confirm({
      message: 'Cancel this payment? It cannot be resumed.',
      header: 'Confirm Cancel',
      icon: 'pi pi-times-circle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.runAction(this.financeService.cancelSupplierPayment(this.payment!.uuid), 'Payment cancelled.')
    });
  }

  confirmPost() {
    this.confirmationService.confirm({
      message: 'Post this payment? This writes a ledger entry and updates invoice paid amounts — it cannot be undone except via Bounce (cheque only).',
      header: 'Confirm Post',
      icon: 'pi pi-exclamation-triangle',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.runAction(this.financeService.postSupplierPayment(this.payment!.uuid), 'Payment posted.')
    });
  }

  confirmBounce() {
    this.confirmationService.confirm({
      message: 'Mark this cheque as bounced? This reverses the ledger entry and the invoice paid amounts.',
      header: 'Confirm Bounce',
      icon: 'pi pi-exclamation-triangle',
      rejectButtonStyleClass: 'p-button-text',
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
        // A35 D-5 — posting needs a rate for the payment's currency on the payment date.
        const missing = missingRateOf(err?.error?.message);
        if (missing) {
          this.messageService.add({ severity: 'error', summary: 'No exchange rate', detail: missing.message, life: 10000 });
          return;
        }
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
