import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';
import { RolesComponent } from './roles.component';
import { AuthService, PermissionGroup, RoleListItem } from '../../service/auth.service';
import { TenantService } from '../../service/tenant.service';

const p = (id: number, code: string, moduleCode?: string | null, moduleEnabled = true, isAllowed = false) =>
  ({ permissionId: id, name: code.replace(/_/g, ' ').toLowerCase(), code, isAllowed, moduleCode, moduleEnabled });

const SERVER_GROUPS: PermissionGroup[] = [
  { module: 'System', permissions: [p(1, 'USER_MANAGE', 'MODULE_MASTER_DATA'), p(2, 'MODULES_MANAGE', 'MODULE_MASTER_DATA')] },
  { module: 'Purchase Orders', permissions: [p(3, 'PO_VIEW', 'MODULE_DEMAND'), p(4, 'PO_CREATE', 'MODULE_DEMAND', true, true)] },
  { module: 'RFQ', permissions: [p(5, 'RFQ_VIEW', 'MODULE_DEMAND')] },
  { module: 'Manufacturing', permissions: [p(6, 'PROD_VIEW', 'MODULE_MANUFACTURING', false, true), p(7, 'BOM_VIEW', 'MODULE_MANUFACTURING', false)] },
  { module: 'Reports', permissions: [p(8, 'REPORT_VIEW', null)] }
];

describe('RolesComponent — permissions grouped by module (A37 D-14)', () => {
  it('regroups by moduleCode in server order; permissions without a module keep their server group', () => {
    const groups = RolesComponent.groupByModule(SERVER_GROUPS);
    expect(groups.map(g => g.module)).toEqual(['Master Data', 'Demand & Procurement', 'Manufacturing', 'Reports']);
    expect(groups[1].permissions.map(x => x.code)).toEqual(['PO_VIEW', 'PO_CREATE', 'RFQ_VIEW']);
    expect(groups[3].moduleCode).toBeNull();
  });

  it('flags a switched-off module with the tooltip text, but keeps its permissions', () => {
    const groups = RolesComponent.groupByModule(SERVER_GROUPS);
    const mfg = groups.find(g => g.moduleCode === 'MODULE_MANUFACTURING')!;
    expect(mfg.moduleEnabled).toBeFalse();
    expect(mfg.disabledTip).toBe('Enable the Manufacturing module to use this permission.');
    expect(mfg.permissions.length).toBe(2);
    expect(groups.find(g => g.moduleCode === 'MODULE_DEMAND')!.moduleEnabled).toBeTrue();
  });

  it('keeps the server grouping (but still flags) when no permission carries a module code yet', () => {
    const old: PermissionGroup[] = [{ module: 'System', permissions: [{ permissionId: 1, name: 'x', code: 'X', isAllowed: false }] }];
    const groups = RolesComponent.groupByModule(old);
    expect(groups.map(g => g.module)).toEqual(['System']);
    expect(groups[0].moduleEnabled).toBeTrue();
  });

  describe('in the role editor', () => {
    let fixture: ComponentFixture<RolesComponent>;
    let component: RolesComponent;
    let auth: jasmine.SpyObj<AuthService>;

    const role: RoleListItem = { roleId: 7, name: 'Planner', roleCode: 'PLANNER', isActive: true, isGlobal: false,
                                 activeUserCount: 1, permissionCount: 2 } as RoleListItem;

    beforeEach(async () => {
      auth = jasmine.createSpyObj<AuthService>('AuthService', ['getRoleList', 'getRoleDetail', 'replaceRolePermissions']);
      auth.getRoleList.and.returnValue(of({ success: true, message: '', result: [role] } as any));
      auth.getRoleDetail.and.returnValue(of({ success: true, message: '', result: {
        roleId: 7, name: 'Planner', roleCode: 'PLANNER', isActive: true, isGlobal: false, activeUserCount: 1,
        permissionGroups: SERVER_GROUPS.map(g => ({ ...g, permissions: g.permissions.map(x => ({ ...x })) }))
      } } as any));
      auth.replaceRolePermissions.and.returnValue(of({ success: true, message: '', result: null } as any));

      await TestBed.configureTestingModule({
        imports: [RolesComponent],
        providers: [
          provideNoopAnimations(),
          { provide: AuthService, useValue: auth },
          { provide: TenantService, useValue: { tenant: signal({ isSuperAdmin: false }) } }
        ]
      }).compileComponents();
      fixture = TestBed.createComponent(RolesComponent);
      component = fixture.componentInstance;
      fixture.detectChanges();
    });

    afterEach(() => fixture.destroy());

    it('greys the permissions of a switched-off module and marks the group, yet they stay assignable', async () => {
      component.openEdit(role);
      fixture.detectChanges();
      await fixture.whenStable();

      const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
      expect(q('module-off-MODULE_MANUFACTURING')).not.toBeNull();
      expect(q('module-off-MODULE_DEMAND')).toBeNull();
      expect(q('perm-PROD_VIEW')!.classList).toContain('perm-off');
      expect(q('perm-PO_VIEW')!.classList).not.toContain('perm-off');
      const group = component.permissionGroups.find(g => g.moduleCode === 'MODULE_MANUFACTURING')!;
      expect(component.permTip(group, group.permissions[0])).toBe('Enable the Manufacturing module to use this permission.');

      // Assignable: ticking BOM_VIEW (module off) is saved like any other permission.
      component.togglePermission(7, true);
      component.savePermissions();
      expect(auth.replaceRolePermissions).toHaveBeenCalledWith(7, jasmine.arrayContaining([4, 6, 7]));
    });
  });
});
