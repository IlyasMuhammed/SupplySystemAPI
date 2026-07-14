import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

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

export interface UserDetail {
  userID: number;
  firstName: string;
  middleName?: string;
  lastName?: string;
  email: string;
  phone?: string;
  address?: string;
  department?: string;
  isActive: boolean;
  role?: UserRole;
  createdDate: string;
  lastLoginAt?: string;
  temporaryPassword?: string;
}

export interface CreateUserRequest {
  firstName: string;
  lastName?: string;
  email: string;
  phone?: string;
  address?: string;
  department?: string;
  roleID: number;
}

export interface PatchUserRequest {
  firstName?: string;
  lastName?: string;
  department?: string;
  isActive?: boolean;
}

export interface UserListResult {
  items: UserListItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

export interface UserListFilter {
  roleId?: number;
  status?: string;
  department?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

@Injectable({
  providedIn: 'root'
})
export class UserService {
  private readonly baseUrl = 'https://localhost:51800/api/users';

  constructor(private http: HttpClient) {}

  getUsers(filter?: UserListFilter): Observable<ApiResponse<any>> {
    let params = new HttpParams();
    if (filter?.roleId) params = params.set('roleId', filter.roleId.toString());
    if (filter?.status) params = params.set('status', filter.status);
    if (filter?.department) params = params.set('department', filter.department);
    if (filter?.search) params = params.set('search', filter.search);
    if (filter?.page) params = params.set('page', filter.page.toString());
    if (filter?.pageSize) params = params.set('pageSize', filter.pageSize.toString());
    return this.http.get<ApiResponse<any>>(this.baseUrl, { params });
  }

  getUser(id: number): Observable<ApiResponse<UserDetail>> {
    return this.http.get<ApiResponse<UserDetail>>(`${this.baseUrl}/${id}`);
  }

  createUser(data: CreateUserRequest): Observable<ApiResponse<UserDetail>> {
    return this.http.post<ApiResponse<UserDetail>>(this.baseUrl, data);
  }

  patchUser(id: number, data: PatchUserRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${this.baseUrl}/${id}`, data);
  }

  assignRole(id: number, roleID: number): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.baseUrl}/${id}/role`, { roleID });
  }

  resetPassword(id: number): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.baseUrl}/${id}/reset-password`, {});
  }

  deleteUser(id: number): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/${id}`);
  }
}
