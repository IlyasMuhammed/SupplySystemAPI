import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import {
  SERVICE_ORDER_STATUSES, ServiceDashboard, ServiceOrderListItem, ServiceOrderService, ServiceOrderStatus,
  readinessIcon, servicePriorityLabel, serviceStatusLabel, serviceStatusTone
} from '../../../../services/service-order.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { SERVICE_ORDERS_ROUTE } from '../service-order-shared';

export interface StatusGroup { status: ServiceOrderStatus; label: string; orders: ServiceOrderListItem[]; }

/** A36-P5-09 — today's services, what waits for materials, mine, and this week's completion rate. */
@Component({
  selector: 'app-service-order-dashboard',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, ToastModule, ...FLOW],
  templateUrl: './service-order-dashboard.component.html',
  styleUrls: ['../_service-shared.scss'],
  providers: [MessageService]
})
export class ServiceOrderDashboardComponent implements OnInit {
  readonly route = SERVICE_ORDERS_ROUTE;
  readonly tone = serviceStatusTone;
  readonly statusLabel = serviceStatusLabel;
  readonly priorityLabel = servicePriorityLabel;
  readonly readiness = readinessIcon;

  data: ServiceDashboard | null = null;
  isLoading = true;
  loadFailed = false;
  todayGroups: StatusGroup[] = [];

  constructor(private service: ServiceOrderService, private auth: AuthService, private messageService: MessageService) {}

  get canCreate(): boolean { return this.auth.hasPermission('SERVICE_ORDER_CREATE'); }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getDashboard().subscribe({
      next: res => {
        this.isLoading = false;
        const d = res.result;
        this.data = d ? {
          today: d.today ?? [], waitingForMaterials: d.waitingForMaterials ?? [], mine: d.mine ?? [],
          completionRate: d.completionRate ?? { completedThisWeek: 0, scheduledThisWeek: 0, percent: 0 }
        } : null;
        this.todayGroups = this.group(this.data?.today ?? []);
      },
      error: () => {
        this.isLoading = false;
        this.loadFailed = true;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load the service dashboard.' });
      }
    });
  }

  /** Today's orders grouped by status, in lifecycle order; empty groups left out. */
  private group(orders: ServiceOrderListItem[]): StatusGroup[] {
    return SERVICE_ORDER_STATUSES
      .map(s => ({ status: s.value, label: s.label, orders: orders.filter(o => o.status === s.value) }))
      .filter(g => g.orders.length > 0);
  }

  /** Clamped to 0–100 for the bar. */
  get percent(): number {
    const p = this.data?.completionRate.percent ?? 0;
    return Math.max(0, Math.min(100, Math.round(p)));
  }
}
