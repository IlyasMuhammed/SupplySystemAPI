import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { DialogModule } from 'primeng/dialog';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { CheckboxModule } from 'primeng/checkbox';
import { DividerModule } from 'primeng/divider';
import { MessageService, ConfirmationService } from 'primeng/api';
import {
  AuthService,
  RoleListItem,
  RoleDetail,
  PermissionGroup,
  CreateRoleRequest,
  UpdateRoleRequest,
  RoleDeactivateConflict
} from '../../service/auth.service';
import { TenantService } from '../../service/tenant.service';

@Component({
  selector: 'app-roles',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule, FormsModule,
    ButtonModule, InputTextModule, TextareaModule, DialogModule,
    TableModule, TagModule, ToastModule, TooltipModule,
    ConfirmDialogModule, CheckboxModule, DividerModule
  ],
  templateUrl: './roles.component.html',
  providers: [MessageService, ConfirmationService]
})
export class RolesComponent implements OnInit {
  roles: RoleListItem[] = [];
  isLoading = false;

  // ── Create ──────────────────────────────────────────────────────────────────
  showCreateDialog = false;
  isSaving = false;
  createForm!: FormGroup;

  // ── Edit + permissions ───────────────────────────────────────────────────────
  showEditDialog = false;
  isUpdating = false;
  isSavingPerms = false;
  editForm!: FormGroup;
  editingRole: RoleListItem | null = null;
  roleDetail: RoleDetail | null = null;
  isLoadingDetail = false;
  permissionGroups: PermissionGroup[] = [];
  allowedIds = new Set<number>();

  // ── Deactivate conflict ──────────────────────────────────────────────────────
  showDeactivateDialog = false;
  isDeactivating = false;
  deactivatingRole: RoleListItem | null = null;
  deactivateConflict: RoleDeactivateConflict | null = null;

  constructor(
    private authService: AuthService,
    private tenantService: TenantService,
    private fb: FormBuilder,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  // Global roles (the shared catalog) can only be edited by a Super Admin — an Org Admin can
  // fully manage their own org's custom roles, but not this shared catalog every other org relies on.
  get isSuperAdmin(): boolean {
    return this.tenantService.tenant()?.isSuperAdmin ?? false;
  }

  canModify(role: RoleListItem): boolean {
    return this.isSuperAdmin || !role.isGlobal;
  }

  ngOnInit(): void {
    this.createForm = this.fb.group({
      name:        ['', [Validators.required, Validators.maxLength(100)]],
      roleCode:    ['', [Validators.required, Validators.maxLength(50), Validators.pattern(/^[A-Z0-9_]+$/)]],
      description: ['', Validators.maxLength(500)]
    });
    this.editForm = this.fb.group({
      name:        ['', [Validators.required, Validators.maxLength(100)]],
      description: ['', Validators.maxLength(500)],
      isActive:    [true]
    });
    this.load();
  }

  load(): void {
    this.isLoading = true;
    this.authService.getRoleList().subscribe({
      next: res => {
        this.isLoading = false;
        this.roles = res?.result ?? [];
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load roles.' });
      }
    });
  }

  // ── Create ─────────────────────────────────────────────────────────────────

  openCreate(): void {
    this.createForm.reset({ name: '', roleCode: '', description: '' });
    this.showCreateDialog = true;
  }

  onRoleNameInput(): void {
    const name: string = this.createForm.get('name')?.value ?? '';
    const code = name.trim().toUpperCase().replace(/[^A-Z0-9]+/g, '_').replace(/^_|_$/g, '');
    this.createForm.get('roleCode')?.setValue(code, { emitEvent: false });
  }

  onRoleCodeInput(event: Event): void {
    const val = (event.target as HTMLInputElement).value.toUpperCase().replace(/[^A-Z0-9_]/g, '');
    this.createForm.get('roleCode')?.setValue(val, { emitEvent: false });
  }

  saveCreate(): void {
    if (this.createForm.invalid) { this.createForm.markAllAsTouched(); return; }
    this.isSaving = true;
    const v = this.createForm.value;
    const req: CreateRoleRequest = {
      name:        v.name.trim(),
      roleCode:    v.roleCode.trim().toUpperCase(),
      description: v.description?.trim() || undefined
    };
    this.authService.createRoleV2(req).subscribe({
      next: res => {
        this.isSaving = false;
        if (res.success) {
          this.showCreateDialog = false;
          this.messageService.add({ severity: 'success', summary: 'Created', detail: `Role "${req.name}" created.` });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to create role.' });
        }
      },
      error: err => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to create role.' });
      }
    });
  }

  // ── Edit + permissions ─────────────────────────────────────────────────────

  openEdit(role: RoleListItem): void {
    this.editingRole = role;
    this.editForm.reset({ name: role.name, description: role.description ?? '', isActive: role.isActive });
    this.roleDetail = null;
    this.permissionGroups = [];
    this.allowedIds = new Set();
    this.showEditDialog = true;
    this.loadRoleDetail(role.roleId);
  }

  private loadRoleDetail(roleId: number): void {
    this.isLoadingDetail = true;
    this.authService.getRoleDetail(roleId).subscribe({
      next: res => {
        this.isLoadingDetail = false;
        this.roleDetail = res?.result ?? null;
        this.permissionGroups = this.roleDetail?.permissionGroups ?? [];
        this.allowedIds = new Set(
          this.permissionGroups
            .flatMap(g => g.permissions)
            .filter(p => p.isAllowed)
            .map(p => p.permissionId)
        );
      },
      error: () => {
        this.isLoadingDetail = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load role permissions.' });
      }
    });
  }

  isAllowed(permId: number): boolean {
    return this.allowedIds.has(permId);
  }

  togglePermission(permId: number, checked: boolean): void {
    if (checked) this.allowedIds.add(permId);
    else this.allowedIds.delete(permId);
  }

  isGroupAllChecked(group: PermissionGroup): boolean {
    return group.permissions.every(p => this.allowedIds.has(p.permissionId));
  }

  isGroupPartialChecked(group: PermissionGroup): boolean {
    const count = group.permissions.filter(p => this.allowedIds.has(p.permissionId)).length;
    return count > 0 && count < group.permissions.length;
  }

  toggleGroup(group: PermissionGroup, checked: boolean): void {
    group.permissions.forEach(p => {
      if (checked) this.allowedIds.add(p.permissionId);
      else this.allowedIds.delete(p.permissionId);
    });
  }

  saveEdit(): void {
    if (this.editForm.invalid || !this.editingRole) { this.editForm.markAllAsTouched(); return; }
    this.isUpdating = true;
    const v = this.editForm.value;
    const req: UpdateRoleRequest = {
      name:        v.name.trim(),
      description: v.description?.trim() || undefined,
      isActive:    v.isActive ?? true
    };
    this.authService.updateRoleV2(this.editingRole.roleId, req).subscribe({
      next: res => {
        this.isUpdating = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Role updated.' });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to update role.' });
        }
      },
      error: err => {
        this.isUpdating = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to update role.' });
      }
    });
  }

  savePermissions(): void {
    if (!this.editingRole) return;
    this.isSavingPerms = true;
    const ids = Array.from(this.allowedIds);
    this.authService.replaceRolePermissions(this.editingRole.roleId, ids).subscribe({
      next: res => {
        this.isSavingPerms = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Permissions saved.' });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to save permissions.' });
        }
      },
      error: err => {
        this.isSavingPerms = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to save permissions.' });
      }
    });
  }

  // ── Deactivate ─────────────────────────────────────────────────────────────

  confirmDeactivate(role: RoleListItem): void {
    if (!role.isActive) {
      this.messageService.add({ severity: 'info', summary: 'Already Inactive', detail: `"${role.name}" is already inactive.` });
      return;
    }
    this.confirmationService.confirm({
      message: `Deactivate role <strong>${role.name}</strong>? This will prevent it from being assigned to new users.`,
      header: 'Deactivate Role',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Deactivate',
      acceptButtonStyleClass: 'p-button-warning',
      rejectLabel: 'Cancel',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.doDeactivate(role)
    });
  }

  private doDeactivate(role: RoleListItem): void {
    this.authService.deactivateRole(role.roleId).subscribe({
      next: res => {
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Deactivated', detail: `"${role.name}" deactivated.` });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to deactivate.' });
        }
      },
      error: err => {
        if (err.status === 409) {
          this.deactivatingRole = role;
          this.deactivateConflict = err.error?.result ?? { activeUserCount: 0 };
          this.showDeactivateDialog = true;
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to deactivate.' });
        }
      }
    });
  }

  closeDeactivateDialog(): void {
    this.showDeactivateDialog = false;
    this.deactivatingRole = null;
    this.deactivateConflict = null;
  }

  groupAllowedCount(group: PermissionGroup): number {
    return group.permissions.filter(p => this.allowedIds.has(p.permissionId)).length;
  }

  get activeUserCount(): number {
    return this.deactivateConflict?.activeUserCount ?? 0;
  }
}
