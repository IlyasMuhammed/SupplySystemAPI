import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { TableModule } from 'primeng/table';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ConfirmationService, MessageService } from 'primeng/api';

import { CustomerBalance, CustomerDetail, CustomerService, customerTypeLabel } from '../../../services/customer.service';
import { SaleOrderModel, SaleOrderService } from '../../../services/sale-order.service';
import { AuthService } from '../../service/auth.service';
import { ModuleService } from '../../../services/module.service';
import { CustomerFormDialogComponent } from '../customer-form-dialog/customer-form-dialog.component';
import { FLOW, FlowSection } from '../../../shared/flow';
import { formatCode } from '../../../shared/format-code';
import { SALE_ORDER_STATUS_SEVERITY } from '../../sales/sale-orders/sale-order-list/sale-order-list.component';

/** A37 §15.4 — a customer as an SMS Flow object page: Details, Financial, Orders (API-CONTRACT §5). No timeline: partners have none. */
@Component({
  selector: 'app-customer-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ButtonModule, TagModule, ToastModule, TooltipModule, TableModule, ConfirmDialogModule,
    CustomerFormDialogComponent, ...FLOW
  ],
  templateUrl: './customer-detail.component.html',
  providers: [MessageService, ConfirmationService]
})
export class CustomerDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly service = inject(CustomerService);
  private readonly saleOrders = inject(SaleOrderService);
  private readonly auth = inject(AuthService);
  private readonly modules = inject(ModuleService);
  private readonly messages = inject(MessageService);
  private readonly confirm = inject(ConfirmationService);

  uuid = '';
  customer: CustomerDetail | null = null;
  isLoading = true;
  loadError: string | null = null;

  balance: CustomerBalance | null = null;
  orders: SaleOrderModel[] = [];
  ordersTotal = 0;
  ordersLoading = false;
  ordersFailed = false;

  showEdit = false;
  statusBusy = false;

  readonly typeLabel = customerTypeLabel;
  readonly formatCode = formatCode;

  get canEdit(): boolean { return this.auth.hasPermission('CUSTOMER_EDIT'); }
  get canChangeStatus(): boolean { return this.auth.hasPermission('CUSTOMER_DEACTIVATE'); }
  get canViewLedger(): boolean { return this.auth.hasPermission('CUSTOMER_LEDGER_VIEW'); }
  get canViewOrders(): boolean { return this.auth.hasPermission('SALE_ORDER_VIEW'); }
  get creditEnabled(): boolean { return this.modules.isFeatureEnabled('FEATURE_CREDIT_MANAGEMENT'); }
  /** CUST-01 — the walk-in customer cannot be deactivated. */
  get isWalkIn(): boolean { return !!this.customer && (this.customer.isSystem || this.customer.customerType === 'WALK_IN'); }

  get sections(): FlowSection[] {
    return [
      { id: 'sec-details', label: 'Details' },
      { id: 'sec-financial', label: 'Financial' },
      { id: 'sec-orders', label: 'Orders', count: this.canViewOrders ? this.ordersTotal : null }
    ];
  }

  get address(): string {
    const c = this.customer;
    if (!c) return '';
    return [c.addressLine1, c.addressLine2, c.city, c.provinceState, c.postalCode, c.country].filter(Boolean).join(', ');
  }

  /** The balance endpoint's figure when it answered, else the detail's own. */
  get balanceValue(): number { return this.balance?.balance ?? this.customer?.balance ?? 0; }
  get balanceCurrency(): string { return this.balance?.currencyCode ?? this.customer?.currencyCode ?? ''; }
  get overCredit(): boolean {
    return this.creditEnabled && !!this.customer && this.customer.creditLimit > 0 && this.balanceValue > this.customer.creditLimit;
  }

  ngOnInit(): void {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  load(): void {
    this.isLoading = true;
    this.loadError = null;
    this.service.getCustomer(this.uuid).subscribe({
      next: res => {
        this.isLoading = false;
        this.customer = res.result ?? null;
        if (!this.customer) { this.loadError = res.message || 'Customer not found.'; return; }
        this.loadBalance();
        this.loadOrders();
      },
      error: err => {
        this.isLoading = false;
        this.customer = null;
        this.loadError = err?.error?.message || (err?.status === 404 ? 'Customer not found.' : 'Failed to load the customer.');
      }
    });
  }

  private loadBalance(): void {
    this.service.getBalance(this.uuid).subscribe({
      next: res => { this.balance = res.result ?? null; },
      error: () => { this.balance = null; }
    });
  }

  private loadOrders(): void {
    if (!this.canViewOrders) return;
    this.ordersLoading = true;
    this.ordersFailed = false;
    this.saleOrders.getSaleOrders({ partnerId: this.uuid, page: 1, pageSize: 10 }).subscribe({
      next: res => {
        this.ordersLoading = false;
        this.orders = res.result?.data ?? [];
        this.ordersTotal = res.result?.totalRecords ?? this.orders.length;
      },
      error: () => { this.ordersLoading = false; this.ordersFailed = true; }
    });
  }

  orderSeverity(status: string) { return SALE_ORDER_STATUS_SEVERITY[status] ?? 'secondary'; }

  onSaved(): void {
    this.messages.add({ severity: 'success', summary: 'Saved', detail: 'Customer updated.' });
    this.load();
  }

  toggleStatus(): void {
    if (!this.customer || this.statusBusy) return;
    const activate = !this.customer.isActive;
    if (activate) { this.applyStatus(true); return; }
    this.confirm.confirm({
      header: 'Deactivate customer?',
      message: `${this.customer.name} will no longer be offered on new sale orders. Existing documents are not changed.`,
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.applyStatus(false)
    });
  }

  applyStatus(isActive: boolean): void {
    this.statusBusy = true;
    this.service.setStatus(this.uuid, isActive).subscribe({
      next: res => {
        this.statusBusy = false;
        if (res && res.success === false) { this.fail(res.message); return; }
        this.messages.add({ severity: 'success', summary: 'Done', detail: isActive ? 'Customer activated.' : 'Customer deactivated.' });
        this.load();
      },
      error: err => { this.statusBusy = false; this.fail(err?.error?.message); }
    });
  }

  private fail(message?: string | null): void {
    this.messages.add({ severity: 'error', summary: 'Not done', detail: message || 'The action failed.', life: 8000 });
  }

  back(): void { this.router.navigate(['/portal/pages/customers']); }
}
