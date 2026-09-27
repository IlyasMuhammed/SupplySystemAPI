import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from './inventory.service';
import { environment } from '../../environments/environment';

// A31 C9 §11 — the Consolidated Purchase Required dashboard. Mirrors
// SMS.Modules.Material.Models.PurchaseRequiredModels.

export interface PurchaseRequiredLine {
  variantUuid: string;
  productUuid: string;
  productName: string;
  variantName: string;
  sku: string;
  uom: string;
  totalShortageQty: number;
  affectedPoCount: number;
  earliestRequiredDate: string;
  defaultSupplierId?: string | null;
  defaultSupplierName?: string | null;
  currentStockOnHand: number;
  pendingPoUuid?: string | null;
  pendingPoNumber?: string | null;
  pendingPoQuantity?: number | null;
  pendingPoStatus?: string | null;
  isAcknowledgedManually: boolean;
  acknowledgedNotes?: string | null;
  acknowledgedAt?: string | null;
}

export interface PurchaseRequiredAffectedOrder {
  productionOrderUuid: string;
  productionNumber: string;
  status: string;
  plannedQuantity: number;
  shortageQuantity: number;
  plannedStartDate?: string | null;
  requiredDate: string;
}

export interface PurchaseRequiredListFilter {
  supplierId?: string;
  minShortageQty?: number;
  sortBy?: 'shortage' | 'urgency' | 'product';
}

export interface CreatePurchaseRequiredPoRequest {
  supplierId: string;
  supplierName: string;
  quantity: number;
  unitPrice: number;
  requiredDate: string;
  notes?: string;
}

@Injectable({ providedIn: 'root' })
export class PurchaseRequiredService {
  private readonly base = `${environment.apiUrl}/purchase-required`;

  constructor(private http: HttpClient) {}

  getList(filter: PurchaseRequiredListFilter = {}): Observable<ApiResponse<PurchaseRequiredLine[]>> {
    let params = new HttpParams();
    if (filter.supplierId)     params = params.set('supplierId', filter.supplierId);
    if (filter.minShortageQty) params = params.set('minShortageQty', String(filter.minShortageQty));
    if (filter.sortBy)         params = params.set('sortBy', filter.sortBy);
    return this.http.get<ApiResponse<PurchaseRequiredLine[]>>(this.base, { params });
  }

  getAffectedOrders(variantUuid: string): Observable<ApiResponse<PurchaseRequiredAffectedOrder[]>> {
    return this.http.get<ApiResponse<PurchaseRequiredAffectedOrder[]>>(`${this.base}/${variantUuid}/affected-orders`);
  }

  acknowledge(variantUuid: string, notes?: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.base}/${variantUuid}/acknowledge`, { notes: notes ?? null });
  }

  clearAcknowledgement(variantUuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.base}/${variantUuid}/acknowledge/clear`, {});
  }

  /** Consolidates onto an open Draft PO for the chosen supplier (same mechanism as the automatic
   * flow) and links every live supply requirement for this variant to it — unlike posting straight
   * to /api/purchase-orders, this is what makes "Create Purchase Order" actually disappear afterward. */
  createPurchaseOrder(variantUuid: string, req: CreatePurchaseRequiredPoRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.base}/${variantUuid}/create-po`, req);
  }
}
