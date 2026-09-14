import { CommonModule } from '@angular/common';
import { Component, OnInit, ViewChild, signal } from '@angular/core';
import { AbstractControl, FormBuilder, FormGroup, ReactiveFormsModule, Validators, FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { DividerModule } from 'primeng/divider';
import { DropdownModule } from 'primeng/dropdown';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { InputTextModule } from 'primeng/inputtext';
import { MultiSelectModule } from 'primeng/multiselect';
import { RippleModule } from 'primeng/ripple';
import { Table, TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { UserListItem, UserService, CreateUserRequest } from '../../services/user.service';
import { AuthService } from '../service/auth.service';
import { SupplierService } from '../../services/supplier.service';

@Component({
    selector: 'app-customer',
    standalone: true,
    imports: [
        CommonModule, FormsModule, ReactiveFormsModule, RouterModule,
        TableModule, ButtonModule, RippleModule, ToastModule,
        InputTextModule, DialogModule, InputIconModule, IconFieldModule,
        ConfirmDialogModule, TagModule, CheckboxModule, DividerModule,
        DropdownModule, TooltipModule, MultiSelectModule
    ],
    templateUrl: './customer.html',
    styleUrls: ['./customer.scss'],
    providers: [MessageService, ConfirmationService]
})
export class CustomerComponent implements OnInit {
    users = signal<UserListItem[]>([]);
    totalRecords = 0;
    isLoading = false;

    // Filters
    searchText = '';
    selectedRoleFilter: number | null = null;
    selectedStatusFilter: string | null = null;
    selectedSupplierTypeFilter: string | null = null;
    roleFilterOptions: { label: string; value: number }[] = [];
    readonly statusFilterOptions = [
        { label: 'Active',   value: 'active' },
        { label: 'Inactive', value: 'inactive' }
    ];
    // Shared by the create/edit dialog dropdowns and the grid filter.
    readonly supplierTypeOptions = [
        { label: 'Internal', value: 'INTERNAL' },
        { label: 'External', value: 'EXTERNAL' }
    ];

    // Available roles for create/edit (built dynamically from users)
    availableRoles: { label: string; value: number }[] = [
        { label: 'System Admin', value: 1 }
    ];

    // REQ-2.x — supplier options for the "Assigned Suppliers" multi-select, shown only for
    // supplierType="EXTERNAL" users.
    supplierOptions: { label: string; value: string }[] = [];

    // Pagination
    first = 0;
    rows  = 10;

    // Create dialog
    showCreateDialog   = false;
    createForm!: FormGroup;
    isCreating         = false;
    createdTempPassword = '';
    showTempPasswordDialog = false;

    // Edit dialog
    showEditDialog  = false;
    editForm!: FormGroup;
    editingUser: UserListItem | null = null;
    isSaving        = false;
    originalRoleId: number | null = null;

    // Reset password result
    showResetResultDialog = false;
    resetTempPassword     = '';
    resetForUser: UserListItem | null = null;

    // Department dropdown options (value = name string, matches CreateUserRequest.department)
    readonly departments: { label: string; value: string }[] = [
        { label: 'Administration',          value: 'Administration' },
        { label: 'Finance',                 value: 'Finance' },
        { label: 'Human Resources',         value: 'Human Resources' },
        { label: 'Information Technology',  value: 'Information Technology' },
        { label: 'Inventory',               value: 'Inventory' },
        { label: 'Legal & Compliance',      value: 'Legal & Compliance' },
        { label: 'Logistics',               value: 'Logistics' },
        { label: 'Operations',              value: 'Operations' },
        { label: 'Procurement',             value: 'Procurement' },
        { label: 'Quality Assurance',       value: 'Quality Assurance' },
        { label: 'Sales & Marketing',       value: 'Sales & Marketing' },
        { label: 'Warehouse',               value: 'Warehouse' },
    ];

    private readonly NAME_PATTERN  = /^[A-Za-z\s\-]+$/;
    private readonly EMAIL_PATTERN = /^[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}$/;
    private readonly PHONE_PATTERN = /^\+?[0-9]{7,15}$/;

    @ViewChild('dt') dt!: Table;

    constructor(
        private userService: UserService,
        private authService: AuthService,
        private supplierService: SupplierService,
        private messageService: MessageService,
        private confirmationService: ConfirmationService,
        private fb: FormBuilder
    ) {}

    // Gates the Add User button and per-row actions (edit/delete/reset password/assign role).
    // Was a hardcoded 'System Admin' role-name check — silently hid all of this from Org Admin,
    // who holds a different role name but the same USER_MANAGE permission the backend actually
    // checks. Match that: permission-based, not role-name-based.
    get isAdmin(): boolean {
        return this.authService.hasPermission('USER_MANAGE');
    }

    get activeCount(): number {
        return this.users().filter(u => u.isActive).length;
    }

    get inactiveCount(): number {
        return this.users().filter(u => !u.isActive).length;
    }

    /** Returns the named control from the create form (non-null asserted). */
    cf(name: string): AbstractControl {
        return this.createForm.get(name)!;
    }

    /** Returns the named control from the edit form (non-null asserted). */
    ef(name: string): AbstractControl {
        return this.editForm.get(name)!;
    }

    ngOnInit() {
        this.createForm = this.fb.group({
            firstName:  ['', [
                Validators.required,
                Validators.minLength(2),
                Validators.maxLength(50),
                Validators.pattern(this.NAME_PATTERN)
            ]],
            lastName:   ['', [
                Validators.required,
                Validators.minLength(2),
                Validators.maxLength(50),
                Validators.pattern(this.NAME_PATTERN)
            ]],
            email:      ['', [
                Validators.required,
                Validators.pattern(this.EMAIL_PATTERN)
            ]],
            phone:      ['', [
                Validators.required,
                Validators.pattern(this.PHONE_PATTERN)
            ]],
            department: [null, [Validators.required]],
            roleID:     [null,  [Validators.required]],
            supplierType: [null, [Validators.required]],
            assignedSupplierIds: [[]]
        });

        this.editForm = this.fb.group({
            firstName:  ['', [
                Validators.required,
                Validators.minLength(2),
                Validators.maxLength(50),
                Validators.pattern(this.NAME_PATTERN)
            ]],
            lastName:   ['', [
                Validators.minLength(2),
                Validators.maxLength(50),
                Validators.pattern(this.NAME_PATTERN)
            ]],
            department: [null],
            isActive:   [true],
            roleID:     [null],
            supplierType: [null, [Validators.required]],
            assignedSupplierIds: [[]]
        });

        this.loadRoles();
        this.loadSuppliers();
        this.loadUsers();
    }

    loadSuppliers() {
        this.supplierService.getSuppliers({ status: 'ACTIVE', pageSize: 500 }).subscribe({
            next: (res) => {
                if (res.success && res.result?.data) {
                    this.supplierOptions = res.result.data.map(s => ({ label: s.supplierName, value: s.uuid }));
                }
            },
            error: () => {}
        });
    }

    loadRoles() {
        // Uses RolesController (USER_MANAGE-gated) rather than the workflow engine's
        // approver-options endpoint (WORKFLOW_ADMIN-gated, and backed by a raw un-scoped SQL
        // query) — an Org Admin has USER_MANAGE but not WORKFLOW_ADMIN, and this list only needs
        // the shared role catalog, not the cross-org user/group data approver-options also returns.
        this.authService.getRoleList().subscribe({
            next: (res) => {
                if (res.success && res.result?.length) {
                    this.availableRoles    = res.result.filter(r => r.isActive).map(r => ({ label: r.name, value: r.roleId }));
                    this.roleFilterOptions = [...this.availableRoles];
                }
            },
            error: () => {}
        });
    }

    loadUsersLazy(event: { first?: number; rows?: number | null }) {
        this.first = event.first ?? 0;
        this.rows  = event.rows  ?? this.rows;
        this.loadUsers();
    }

    loadUsers() {
        this.isLoading = true;
        const page = Math.floor(this.first / this.rows) + 1;
        this.userService.getUsers({
            search:       this.searchText               || undefined,
            roleId:       this.selectedRoleFilter        ?? undefined,
            status:       this.selectedStatusFilter      ?? undefined,
            supplierType: this.selectedSupplierTypeFilter ?? undefined,
            page,
            pageSize: this.rows
        }).subscribe({
            next: (res) => {
                this.isLoading = false;
                if (res.success) {
                    const result = res.result;
                    const items: UserListItem[] = result?.items ?? result?.data ?? (Array.isArray(result) ? result : []);
                    this.users.set(items);
                    this.totalRecords = result?.total ?? result?.totalRecords ?? items.length;
                    this.buildRoleOptions(items);
                } else {
                    this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to load users' });
                }
            },
            error: () => {
                this.isLoading = false;
                this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load users' });
            }
        });
    }

    private buildRoleOptions(users: UserListItem[]) {
        const filterRoles = [...this.availableRoles];
        users.forEach(u => {
            if (u.role && !filterRoles.find(r => r.value === u.role!.id)) {
                filterRoles.push({ label: u.role.value, value: u.role.id });
            }
        });
        this.roleFilterOptions = filterRoles;
    }

    onSearch(event: Event) {
        this.searchText = (event.target as HTMLInputElement).value;
        this.first = 0;
        this.loadUsers();
    }

    applyFilters() {
        this.first = 0;
        this.loadUsers();
    }

    clearFilters() {
        this.searchText          = '';
        this.selectedRoleFilter  = null;
        this.selectedStatusFilter = null;
        this.selectedSupplierTypeFilter = null;
        this.first               = 0;
        this.loadUsers();
    }

    getInitials(user: UserListItem): string {
        const f = user.firstName?.[0] ?? '';
        const l = user.lastName?.[0]  ?? '';
        return (f + l).toUpperCase() || 'U';
    }

    getRoleSeverity(role?: string): 'success' | 'warn' | 'info' | 'danger' | 'secondary' {
        if (!role) return 'secondary';
        const r = role.toLowerCase();
        if (r.includes('admin'))   return 'danger';
        if (r.includes('manager')) return 'warn';
        if (r.includes('finance')) return 'success';
        return 'info';
    }

    // ── Create User ──────────────────────────────────────────────────────────

    openCreateDialog() {
        this.createForm.reset({
            firstName:  '',
            lastName:   '',
            email:      '',
            phone:      '',
            department: null,
            roleID:     null,
            supplierType: null,
            assignedSupplierIds: []
        });
        this.showCreateDialog = true;
    }

    saveCreate() {
        if (this.createForm.invalid) { this.createForm.markAllAsTouched(); return; }
        this.isCreating = true;
        const raw = this.createForm.value;
        const payload: CreateUserRequest = {
            firstName:  raw.firstName.trim(),
            lastName:   raw.lastName?.trim()   || undefined,
            email:      raw.email.trim(),
            phone:      raw.phone?.trim()      || undefined,
            department: raw.department?.trim() || undefined,
            roleID:     raw.roleID,
            supplierType: raw.supplierType,
            supplierIds: raw.supplierType === 'EXTERNAL' ? raw.assignedSupplierIds : undefined
        };
        this.userService.createUser(payload).subscribe({
            next: (res) => {
                this.isCreating = false;
                this.showCreateDialog = false;
                if (res.success) {
                    this.createdTempPassword = (res.result as any)?.temporaryPassword ?? '';
                    this.showTempPasswordDialog = true;
                    this.loadUsers();
                } else {
                    this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to create user' });
                }
            },
            error: (err) => {
                this.isCreating = false;
                this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to create user' });
            }
        });
    }

    // ── Edit User ─────────────────────────────────────────────────────────────

    openEditDialog(user: UserListItem) {
        this.editingUser    = user;
        this.originalRoleId = user.role?.id ?? null;
        this.editForm.reset({
            firstName:  user.firstName,
            lastName:   user.lastName   || '',
            department: user.department || null,
            isActive:   user.isActive,
            roleID:     user.role?.id   ?? null,
            supplierType: user.supplierType,
            assignedSupplierIds: user.supplierIds ?? []
        });
        this.showEditDialog = true;
    }

    saveEdit() {
        if (this.editForm.invalid || !this.editingUser) { this.editForm.markAllAsTouched(); return; }
        const raw = this.editForm.value;
        this.isSaving = true;

        this.userService.patchUser(this.editingUser.userID, {
            firstName:  raw.firstName.trim(),
            lastName:   raw.lastName?.trim()   || undefined,
            department: raw.department?.trim() || undefined,
            isActive:   !!raw.isActive,
            supplierType: raw.supplierType,
            supplierIds: raw.supplierType === 'EXTERNAL' ? raw.assignedSupplierIds : []
        }).subscribe({
            next: (res) => {
                if (!res.success) {
                    this.isSaving = false;
                    this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Update failed' });
                    return;
                }
                // Also assign new role if changed
                if (raw.roleID && raw.roleID !== this.originalRoleId) {
                    this.userService.assignRole(this.editingUser!.userID, raw.roleID).subscribe({
                        next: () => this.finishEdit(),
                        error: (err) => {
                            this.isSaving = false;
                            this.messageService.add({ severity: 'warn', summary: 'Partial Save', detail: 'Info saved but role assignment failed: ' + (err.error?.message ?? '') });
                            this.showEditDialog = false;
                            this.loadUsers();
                        }
                    });
                } else {
                    this.finishEdit();
                }
            },
            error: (err) => {
                this.isSaving = false;
                this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Update failed' });
            }
        });
    }

    private finishEdit() {
        this.isSaving       = false;
        this.showEditDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'User updated successfully.' });
        this.loadUsers();
    }

    // ── Delete User ───────────────────────────────────────────────────────────

    confirmDelete(user: UserListItem) {
        this.confirmationService.confirm({
            message: `Delete <strong>${user.firstName} ${user.lastName ?? ''}</strong>? This cannot be undone.`,
            header: 'Delete User',
            icon: 'pi pi-exclamation-triangle',
            acceptLabel: 'Delete',
            acceptButtonStyleClass: 'p-button-danger',
            rejectLabel: 'Cancel',
            rejectButtonStyleClass: 'p-button-text',
            accept: () => {
                this.userService.deleteUser(user.userID).subscribe({
                    next: (res) => {
                        if (res.success) {
                            this.messageService.add({ severity: 'success', summary: 'Deleted', detail: 'User removed.' });
                            this.loadUsers();
                        } else {
                            this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Delete failed' });
                        }
                    },
                    error: (err) => this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Delete failed' })
                });
            }
        });
    }

    // ── Reset Password ────────────────────────────────────────────────────────

    confirmResetPassword(user: UserListItem) {
        this.confirmationService.confirm({
            message: `Reset password for <strong>${user.firstName} ${user.lastName ?? ''}</strong>? A new temporary password will be generated.`,
            header: 'Reset Password',
            icon: 'pi pi-key',
            acceptLabel: 'Reset',
            acceptButtonStyleClass: 'p-button-warning',
            rejectLabel: 'Cancel',
            rejectButtonStyleClass: 'p-button-text',
            accept: () => {
                this.resetForUser = user;
                this.userService.resetPassword(user.userID).subscribe({
                    next: (res) => {
                        if (res.success) {
                            this.resetTempPassword = (res as any).result?.temporaryPassword ?? '';
                            this.showResetResultDialog = true;
                        } else {
                            this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Reset failed' });
                        }
                    },
                    error: (err) => this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Reset failed' })
                });
            }
        });
    }

    exportCSV() {
        this.dt.exportCSV();
    }
}
