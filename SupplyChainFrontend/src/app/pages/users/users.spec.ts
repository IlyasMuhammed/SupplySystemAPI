import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { UsersComponent } from './users';
import { UserListItem, UserService } from '../../services/user.service';
import { AuthService } from '../service/auth.service';
import { TenantService } from '../service/tenant.service';
import { SupplierService } from '../../services/supplier.service';

describe('UsersComponent — Set password (platform super admin)', () => {
  let fixture: ComponentFixture<UsersComponent>;
  let component: UsersComponent;
  let userService: jasmine.SpyObj<UserService>;

  const ME = 1;
  const rows: UserListItem[] = [
    { userID: ME, firstName: 'Sam', lastName: 'Super', email: 'sam@platform.test', isActive: true, createdDate: '', supplierType: 'INTERNAL', supplierIds: [] },
    { userID: 2, firstName: 'Ola', lastName: 'Other', email: 'ola@acme.test', isActive: true, createdDate: '', supplierType: 'INTERNAL', supplierIds: [] }
  ];

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`) ?? document.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(opts: { permissions: string[]; superAdmin: boolean }) {
    userService = jasmine.createSpyObj<UserService>('UserService', ['getUsers', 'setUserPassword']);
    userService.getUsers.and.returnValue(of({ success: true, message: '', result: { items: rows, total: rows.length } }));

    const auth = jasmine.createSpyObj<AuthService>('AuthService', ['hasPermission', 'getRoleList', 'getUserData']);
    auth.hasPermission.and.callFake((code: string) => opts.permissions.includes(code));
    auth.getRoleList.and.returnValue(of({ success: true, message: '', result: [] }));
    auth.getUserData.and.returnValue({ userId: ME, firstName: 'Sam', lastName: 'Super', email: 'sam@platform.test', role: { id: 1, value: 'Super Admin' } } as any);

    const tenant = { isSuperAdmin: () => opts.superAdmin } as Partial<TenantService>;
    const suppliers = jasmine.createSpyObj<SupplierService>('SupplierService', ['getSuppliers']);
    suppliers.getSuppliers.and.returnValue(of({ success: true, message: '', result: { data: [] } } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [UsersComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: UserService, useValue: userService },
        { provide: AuthService, useValue: auth },
        { provide: TenantService, useValue: tenant },
        { provide: SupplierService, useValue: suppliers }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(UsersComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('offers Set password on other users’ rows to a platform super admin, never on their own row', async () => {
    await setup({ permissions: ['USER_MANAGE', 'PLATFORM_SUPER_ADMIN'], superAdmin: true });

    expect(component.users().length).toBe(2);
    expect(query('set-password-action-2')).not.toBeNull();
    expect(query(`set-password-action-${ME}`)).toBeNull();
  });

  it('hides Set password from a user manager who is not a platform super admin', async () => {
    await setup({ permissions: ['USER_MANAGE'], superAdmin: false });

    expect(component.users().length).toBe(2);
    expect(query('set-password-action-2')).toBeNull();
  });

  it('hides Set password when the permission is held without the super-admin flag (global System Admin role)', async () => {
    await setup({ permissions: ['USER_MANAGE', 'PLATFORM_SUPER_ADMIN'], superAdmin: false });

    expect(component.isPlatformSuperAdmin).toBeFalse();
    expect(query('set-password-action-2')).toBeNull();
  });

  it('keeps the dialog button disabled until the rule and confirmation pass, then sets the password', async () => {
    await setup({ permissions: ['USER_MANAGE', 'PLATFORM_SUPER_ADMIN'], superAdmin: true });
    userService.setUserPassword.and.returnValue(of({ success: true, message: "Password set. The user's other sessions were signed out.", result: null }));

    component.openSetPasswordDialog(rows[1]);
    fixture.detectChanges();
    const submit = () => query('set-password-submit')!.querySelector('button') as HTMLButtonElement;
    expect(submit().disabled).toBeTrue();

    component.spNewPassword = 'Str0ng!pw';
    component.spConfirmPassword = 'Str0ng!p';
    fixture.detectChanges();
    expect(submit().disabled).toBeTrue();

    component.spConfirmPassword = 'Str0ng!pw';
    fixture.detectChanges();
    expect(submit().disabled).toBeFalse();

    const loadsBefore = userService.getUsers.calls.count();
    component.submitSetPassword();
    fixture.detectChanges();

    expect(userService.setUserPassword).toHaveBeenCalledWith(2, 'Str0ng!pw');
    expect(component.showSetPasswordDialog).toBeFalse();
    expect(userService.getUsers.calls.count()).toBe(loadsBefore + 1);
  });

  it('shows the server message in the dialog when the password is refused', async () => {
    await setup({ permissions: ['USER_MANAGE', 'PLATFORM_SUPER_ADMIN'], superAdmin: true });
    userService.setUserPassword.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Password must be at least 8 characters long.' } })));

    component.openSetPasswordDialog(rows[1]);
    component.spNewPassword = 'Str0ng!pw';
    component.spConfirmPassword = 'Str0ng!pw';
    component.submitSetPassword();
    fixture.detectChanges();

    expect(component.showSetPasswordDialog).toBeTrue();
    expect(query('set-password-error')!.textContent).toContain('Password must be at least 8 characters long.');
  });
});

describe('UsersComponent — role picker', () => {
  let fixture: ComponentFixture<UsersComponent>;
  let component: UsersComponent;
  let auth: jasmine.SpyObj<AuthService>;

  // What the server returns for ?assignable=true to an Org Admin: no System Admin, no Organization Admin.
  const assignable = [
    { roleId: 7, name: 'Requester', roleCode: 'REQUESTER', isActive: true, isGlobal: true },
    { roleId: 11, name: 'Supply Department Administrator', roleCode: 'SUPPLY_DEPT_ADMIN', isActive: true, isGlobal: true }
  ];
  const orgAdminRow: UserListItem = {
    userID: 5, firstName: 'Olivia', lastName: 'Admin', email: 'olivia@acme.test', isActive: true, createdDate: '',
    supplierType: 'INTERNAL', supplierIds: [], role: { id: 10, value: 'Organization Admin' }
  } as UserListItem;

  beforeEach(async () => {
    const users = jasmine.createSpyObj<UserService>('UserService', ['getUsers']);
    users.getUsers.and.returnValue(of({ success: true, message: '', result: { items: [orgAdminRow], total: 1 } }));
    auth = jasmine.createSpyObj<AuthService>('AuthService', ['hasPermission', 'getRoleList', 'getUserData']);
    auth.hasPermission.and.callFake((code: string) => code === 'USER_MANAGE');
    auth.getRoleList.and.returnValue(of({ success: true, message: '', result: assignable as any }));
    auth.getUserData.and.returnValue({ userId: 1 } as any);
    const suppliers = jasmine.createSpyObj<SupplierService>('SupplierService', ['getSuppliers']);
    suppliers.getSuppliers.and.returnValue(of({ success: true, message: '', result: { data: [] } } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [UsersComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: UserService, useValue: users },
        { provide: AuthService, useValue: auth },
        { provide: TenantService, useValue: { isSuperAdmin: () => false } as Partial<TenantService> },
        { provide: SupplierService, useValue: suppliers }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(UsersComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
  });

  afterEach(() => fixture?.destroy());

  it('asks the server only for the roles this user may assign, and offers only those when adding a user', () => {
    expect(auth.getRoleList).toHaveBeenCalledWith(true);
    expect(component.availableRoles.map(r => r.label)).toEqual(['Requester', 'Supply Department Administrator']);
  });

  it('still shows a user’s current role when editing them, even when it is not assignable', () => {
    component.openEditDialog(orgAdminRow);

    expect(component.editRoleOptions.map(r => r.label)).toEqual(['Organization Admin', 'Requester', 'Supply Department Administrator']);
    expect(component.editForm.value.roleID).toBe(10);
  });
});
