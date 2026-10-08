import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from './inventory.service';
import { environment } from '../../environments/environment';

// GET api/dashboard/summary — the home dashboard's numbers in one call. Mirrors
// SMS.Modules.Reports.Models.DashboardModels. A section is null when the user may not see that area:
// the page leaves it out rather than showing a zero that only means "not allowed".

export interface StatusCount { status: string; count: number; }
export interface CurrencyAmount { currency: string; amount: number; count: number; }
export interface DashboardAttentionItem { key: string; count: number; }

export interface DashboardProcurement {
  purchaseOrders: StatusCount[];
  requisitions: StatusCount[];
  activeSuppliers?: number | null;
  totalSuppliers?: number | null;
}

export interface DashboardSales {
  inquiries?: StatusCount[] | null;
  quotations?: StatusCount[] | null;
  saleOrders?: StatusCount[] | null;
  lateSaleOrders?: number | null;
  ordersThisMonth?: number | null;
  activeCustomers?: number | null;
}

export interface DashboardDeliveries {
  outbound: StatusCount[];
  inboundExpected: number;
  late: number;
  shippedThisMonth: number;
}

export interface DashboardProduction {
  orders?: StatusCount[] | null;
  late?: number | null;
  openMakeToOrder?: number | null;
  boms?: StatusCount[] | null;
}

export interface DashboardMoney {
  invoices: StatusCount[];
  outstanding: CurrencyAmount[];
  overdue: CurrencyAmount[];
}

export interface DashboardSummary {
  generatedAt: string;
  procurement?: DashboardProcurement | null;
  receiving?: { grns: StatusCount[] } | null;
  sales?: DashboardSales | null;
  deliveries?: DashboardDeliveries | null;
  production?: DashboardProduction | null;
  receivables?: DashboardMoney | null;
  payables?: DashboardMoney | null;
  material?: { mirs: StatusCount[] } | null;
  inventory?: { activeProducts: number; manufacturedProducts: number } | null;
  attention: DashboardAttentionItem[];
}

/** How many of the given statuses a status list holds (0 for a missing list). */
export function countOf(list: StatusCount[] | null | undefined, ...statuses: string[]): number {
  return (list ?? []).filter(s => statuses.includes(s.status)).reduce((sum, s) => sum + s.count, 0);
}

/** Every count in a status list. */
export function totalOf(list: StatusCount[] | null | undefined): number {
  return (list ?? []).reduce((sum, s) => sum + s.count, 0);
}

@Injectable({ providedIn: 'root' })
export class DashboardSummaryService {
  private readonly base = `${environment.apiUrl}/dashboard`;

  constructor(private http: HttpClient) {}

  getSummary(): Observable<ApiResponse<DashboardSummary>> {
    return this.http.get<ApiResponse<DashboardSummary>>(`${this.base}/summary`);
  }
}
