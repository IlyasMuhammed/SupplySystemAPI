import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { Router } from '@angular/router';

export interface LoginRequest {
  email: string;
  password: string;
}

export interface UserRole {
  id: number;
  value: string;
}

export interface RoleListItem {
  roleId: number;
  name: string;
  roleCode: string;
  description?: string;
  isActive: boolean;
  activeUserCount: number;
  permissionCount: number;
}

export interface PermissionItem {
  permissionId: number;
  name: string;
  code: string;
  isAllowed: boolean;
}

export interface PermissionGroup {
  module: string;
  permissions: PermissionItem[];
}

export interface RoleDetail {
  roleId: number;
  name: string;
  roleCode: string;
  description?: string;
  isActive: boolean;
  activeUserCount: number;
  permissionGroups: PermissionGroup[];
}

export interface CreateRoleRequest {
  name: string;
  roleCode: string;
  description?: string;
}

export interface UpdateRoleRequest {
  name: string;
  description?: string;
  isActive: boolean;
}

export interface RoleUser {
  userId: number;
  firstName: string;
  lastName?: string;
  email: string;
  department?: string;
  isActive: boolean;
}

export interface RoleDeactivateConflict {
  activeUserCount: number;
}

export interface LoggedInUser {
  userId: number;
  firstName: string;
  lastName: string;
  email: string;
  phoneNo?: string;
  address?: string;
  role: UserRole;
  profilePictureUrl?: string;
}

export interface LoginResponse {
  accessToken: string;
  refreshToken: string;
  expiresIn: number;
  user: LoggedInUser;
}

export interface RegisterRequest {
  firstName: string;
  middleName?: string;
  lastName?: string;
  email: string;
  password: string;
  phone?: string;
  address?: string;
}

export interface ForgotPasswordRequest {
  email: string;
  password: string;
  verificationCode?: string;
}

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

@Injectable({
  providedIn: 'root',
})
export class AuthService {
  private readonly apiUrl = 'https://localhost:51800/api/auth';

  constructor(private http: HttpClient, private router: Router) {}

  login(email: string, password: string, roleId?: number): Observable<ApiResponse<LoginResponse>> {
    const body: Record<string, unknown> = { email, password };
    if (roleId !== undefined) body['roleId'] = roleId;
    return this.http.post<ApiResponse<LoginResponse>>(`${this.apiUrl}/login`, body).pipe(
      tap((response: ApiResponse<LoginResponse>) => {
        if (response.success && response.result) {
          localStorage.setItem('accessToken', response.result.accessToken);
          localStorage.setItem('refreshToken', response.result.refreshToken);
          localStorage.setItem('userData', JSON.stringify(response.result.user));
        }
      })
    );
  }

  getRoles(): Observable<ApiResponse<UserRole[]>> {
    return this.http.get<ApiResponse<UserRole[]>>(`${this.apiUrl}/roles`);
  }

  // ── Role CRUD (ROLE-001) ───────────────────────────────────────────────────
  private readonly rolesBase = 'https://localhost:51800/api/roles';

  getRoleList(): Observable<ApiResponse<RoleListItem[]>> {
    return this.http.get<ApiResponse<RoleListItem[]>>(this.rolesBase);
  }
  getRoleDetail(id: number): Observable<ApiResponse<RoleDetail>> {
    return this.http.get<ApiResponse<RoleDetail>>(`${this.rolesBase}/${id}`);
  }
  createRoleV2(data: CreateRoleRequest): Observable<ApiResponse<RoleListItem>> {
    return this.http.post<ApiResponse<RoleListItem>>(this.rolesBase, data);
  }
  updateRoleV2(id: number, data: UpdateRoleRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.rolesBase}/${id}`, data);
  }
  replaceRolePermissions(id: number, allowedPermissionIds: number[]): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.rolesBase}/${id}/permissions`, { allowedPermissionIds });
  }
  deactivateRole(id: number): Observable<ApiResponse<RoleDeactivateConflict>> {
    return this.http.patch<ApiResponse<RoleDeactivateConflict>>(`${this.rolesBase}/${id}/deactivate`, {});
  }
  getRoleUsers(id: number): Observable<ApiResponse<RoleUser[]>> {
    return this.http.get<ApiResponse<RoleUser[]>>(`${this.rolesBase}/${id}/users`);
  }

  register(data: RegisterRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.apiUrl}/register`, data);
  }

  activate(token: string): Observable<ApiResponse> {
    const params = new HttpParams().set('token', token);
    return this.http.post<ApiResponse>(`${this.apiUrl}/activate`, null, { params });
  }

  forgotPassword(email: string): Observable<ApiResponse> {
    const params = new HttpParams().set('email', email);
    return this.http.post<ApiResponse>(`${this.apiUrl}/forgot-password`, null, { params });
  }

  resetPassword(email: string, password: string, verificationCode: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.apiUrl}/reset-password`, { email, password, verificationCode });
  }

  refresh(): Observable<ApiResponse<{ accessToken: string; refreshToken: string; expiresIn: number }>> {
    const refreshToken = localStorage.getItem('refreshToken') || '';
    return this.http.post<ApiResponse<{ accessToken: string; refreshToken: string; expiresIn: number }>>(
      `${this.apiUrl}/refresh`, { refreshToken }
    ).pipe(
      tap((response: ApiResponse<{ accessToken: string; refreshToken: string; expiresIn: number }>) => {
        if (response.success && response.result) {
          localStorage.setItem('accessToken', response.result.accessToken);
          localStorage.setItem('refreshToken', response.result.refreshToken);
        }
      })
    );
  }

  getCurrentUser(): Observable<ApiResponse<LoggedInUser>> {
    return this.http.get<ApiResponse<LoggedInUser>>(`${this.apiUrl}/me`);
  }

  updateProfile(data: { userID: number; firstName: string; lastName?: string; email: string; phone?: string; paymentMethodId?: number }): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.apiUrl}/profile`, data);
  }

  uploadProfilePicture(formData: FormData): Observable<ApiResponse<{ profilePictureUrl: string }>> {
    return this.http.post<ApiResponse<{ profilePictureUrl: string }>>(`${this.apiUrl}/profile/picture`, formData);
  }

  deleteProfilePicture(): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.apiUrl}/profile/picture`);
  }

  updatePassword(userID: number, currentPassword: string, newPassword: string): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.apiUrl}/password`, {
      userID,
      currentPassword,
      newPassword,
      confirmPassword: newPassword
    });
  }

  isAuthenticated(): boolean {
    const token = this.getToken();
    if (!token) return false;
    try {
      const payload = JSON.parse(atob(token.split('.')[1]));
      return payload.exp * 1000 > Date.now();
    } catch {
      return false;
    }
  }

  private getTokenPayload(): Record<string, any> | null {
    const token = this.getToken();
    if (!token) return null;
    try {
      return JSON.parse(atob(token.split('.')[1]));
    } catch {
      return null;
    }
  }

  getPermissions(): string[] {
    const p = this.getTokenPayload()?.['permission'];
    if (!p) return [];
    return Array.isArray(p) ? p : [p];
  }

  hasPermission(code: string): boolean {
    return this.getPermissions().includes(code);
  }

  hasAnyPermission(...codes: string[]): boolean {
    const perms = this.getPermissions();
    return codes.some(c => perms.includes(c));
  }

  getToken(): string | null {
    return localStorage.getItem('accessToken');
  }

  getUserData(): LoggedInUser | null {
    const raw = localStorage.getItem('userData');
    return raw ? JSON.parse(raw) : null;
  }

  getUsername(): string | null {
    return this.getUserData()?.firstName || null;
  }

  getUserRole(): string | null {
    return this.getUserData()?.role?.value || null;
  }

  hasRole(role: string): boolean {
    const userRole = this.getUserRole()?.trim().toLowerCase();
    const requiredRole = role.trim().toLowerCase();
    return userRole === requiredRole;
  }

  logout(): void {
    const refreshToken = localStorage.getItem('refreshToken');
    if (refreshToken) {
      this.http.post(`${this.apiUrl}/logout`, { refreshToken }).subscribe({ error: () => {} });
    }
    localStorage.removeItem('accessToken');
    localStorage.removeItem('refreshToken');
    localStorage.removeItem('userData');
    this.router.navigate(['/auth/login']);
  }
}
