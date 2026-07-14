import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

export interface UserRole {
  id: number;
  value: string;
}

export interface UserListItem {
  userID: number;
  firstName: string;
  lastName?: string;
  email: string;
  department?: string;
  isActive: boolean;
  role?: UserRole;
  createdDate: string;
  lastLoginAt?: string;
}

export interface UserListResult {
  items: UserListItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface DashboardStats {
  wizards: Wizard;
  bestSellingProducts: BestSellingProducts;
  revenueStream: RevenueStream;
}

export interface Wizard {
  orders: number;
  revenue: number;
  customers: number;
  reviews: number;
}

export interface BestSellingProducts {
  products: SellingProduct[];
}

export interface SellingProduct {
  name: string;
  category: string;
  percentage: number;
  totalQuantity: number;
}

export interface RevenueStream {
  revenueStreamData: RevenueStreamData[];
}

export interface RevenueStreamData {
  quarter: string;
  categoryARevenue: number;
  categoryBRevenue: number;
  categoryCRevenue: number;
}

@Injectable({
  providedIn: 'root'
})
export class DashboardService {
  private readonly baseUrl = 'https://localhost:51800/api';

  constructor(private http: HttpClient) {}

  getUsers(params?: { search?: string; status?: string; page?: number; pageSize?: number }): Observable<ApiResponse<any>> {
    let httpParams = new HttpParams();
    if (params?.search) httpParams = httpParams.set('search', params.search);
    if (params?.status) httpParams = httpParams.set('status', params.status);
    if (params?.page) httpParams = httpParams.set('page', params.page.toString());
    if (params?.pageSize) httpParams = httpParams.set('pageSize', params.pageSize.toString());
    return this.http.get<ApiResponse<any>>(`${this.baseUrl}/users`, { params: httpParams });
  }

  deleteUser(id: number): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/users/${id}`);
  }

  getDashboardStats(): Observable<ApiResponse<DashboardStats>> {
    return this.http.get<ApiResponse<DashboardStats>>(`${this.baseUrl}/Dashboard/GetDashboardStats`);
  }
}
