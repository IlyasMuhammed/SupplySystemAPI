import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';
import { RoleMatrixEditorComponent } from './role-matrix-editor.component';
import { ApprovalRulesEditorComponent } from './approval-rules-editor.component';
import { UomGroupsEditorComponent } from './uom-groups-editor.component';
import { AuthService } from '../../../service/auth.service';

/** A50 tasks 160–162 — the ROLES matrix, APPROVAL_RULES levels and UOM groups editors. */
describe('Setup default payload editors', () => {
  describe('RoleMatrixEditorComponent', () => {
    async function make(auth: any) {
      await TestBed.configureTestingModule({
        imports: [RoleMatrixEditorComponent], providers: [provideNoopAnimations(), { provide: AuthService, useValue: auth }]
      }).compileComponents();
      const f = TestBed.createComponent(RoleMatrixEditorComponent);
      const out: any[] = [];
      f.componentInstance.payloadChange.subscribe(p => out.push(p));
      f.componentRef.setInput('payload', { roles: [
        { code: 'ORG_ADMIN', name: 'Admin', permissions: ['ALL'], requiredModules: [] },
        { code: 'SALES_REP', name: 'Rep', permissions: ['SALE_ORDER_VIEW', 'LEGACY_CODE'], requiredModules: ['MODULE_DEMAND'] }
      ] });
      f.detectChanges();
      return { f, c: f.componentInstance, out };
    }

    const catalog = {
      getRoleList: () => of({ result: [{ roleId: 1 }] }),
      getRoleDetail: () => of({ result: { permissionGroups: [
        { module: 'Sales', permissions: [{ code: 'SALE_ORDER_VIEW' }, { code: 'SALE_ORDER_CREATE' }] },
        { module: 'Admin', permissions: [{ code: 'USER_MANAGE' }] }
      ] } })
    };

    it('rows come from the role catalog plus payload-only codes; ALL ticks everything', async () => {
      const { c, f } = await make(catalog);
      expect(c.allRows.map(r => r.code)).toEqual(['SALE_ORDER_VIEW', 'SALE_ORDER_CREATE', 'USER_MANAGE', 'LEGACY_CODE']);
      expect(c.groups).toEqual(['Sales', 'Admin', 'Other (not in catalog)']);
      expect(c.has(c.roles[0], 'USER_MANAGE')).toBeTrue();
      expect(c.has(c.roles[1], 'SALE_ORDER_CREATE')).toBeFalse();
      expect(f.nativeElement.querySelector('[data-testid="perm-USER_MANAGE"]')).toBeTruthy();
      c.search = 'create';
      expect(c.visibleRows.map(r => r.code)).toEqual(['SALE_ORDER_CREATE']);
    });

    it('toggling and ALL emit the roles; add/remove role', async () => {
      const { c, out } = await make(catalog);
      c.toggle(c.roles[1], 'SALE_ORDER_CREATE', true);
      expect(out[out.length - 1].roles[1].permissions).toContain('SALE_ORDER_CREATE');
      c.toggle(c.roles[1], 'SALE_ORDER_VIEW', false);
      expect(out[out.length - 1].roles[1].permissions).not.toContain('SALE_ORDER_VIEW');
      c.setAll(c.roles[1], true);
      expect(out[out.length - 1].roles[1].permissions).toEqual(['ALL']);
      c.addRole();
      expect(out[out.length - 1].roles.length).toBe(3);
      c.removeRole(2);
      expect(out[out.length - 1].roles.length).toBe(2);
      c.newCode = 'new_code';
      c.addPermission();
      expect(c.allRows.some(r => r.code === 'NEW_CODE')).toBeTrue();
    });

    it('without a catalog it lists the payload codes and says so', async () => {
      const { c } = await make({ getRoleList: () => throwError(() => ({ status: 403 })), getRoleDetail: () => of(null) });
      expect(c.catalogNote).toContain('could not be read');
      expect(c.allRows.map(r => r.code)).toEqual(['SALE_ORDER_VIEW', 'LEGACY_CODE']);
    });
  });

  describe('ApprovalRulesEditorComponent', () => {
    it('edits levels, renumbers them and checks the rules', async () => {
      await TestBed.configureTestingModule({ imports: [ApprovalRulesEditorComponent], providers: [provideNoopAnimations()] }).compileComponents();
      const f = TestBed.createComponent(ApprovalRulesEditorComponent);
      const c = f.componentInstance;
      const out: any[] = [];
      c.payloadChange.subscribe(p => out.push(p));
      f.componentRef.setInput('payload', { notes: 'n', rules: [{ documentType: 'PurchaseOrder', interfaceCode: 'PO', levels: [
        { level: 1, minAmount: 0, maxAmount: 49999, approverRole: 'PROCUREMENT_MANAGER', label: 'PM' }] },
        { documentType: 'ExpenseClaim', afterA49: true, levels: [] }] });
      f.detectChanges();
      expect(f.nativeElement.textContent).not.toContain('After A49');
      c.addLevel(0);
      let last = out[out.length - 1];
      expect(last.notes).toBe('n');
      expect(last.rules[0].levels[1]).toEqual(jasmine.objectContaining({ level: 2, minAmount: 49999 }));
      expect(c.problem).toContain('needs an approver role');
      c.rules[0].levels[1].approverRole = 'FINANCE_MANAGER';
      c.changed();
      expect(c.problem).toBeNull();
      c.removeLevel(0, 0);
      last = out[out.length - 1];
      expect(last.rules[0].levels[0].level).toBe(1);
      expect(c.problem).toContain('level 1 must start at 0');
      c.addRule();
      expect(c.problem).toBeTruthy();
      expect(ApprovalRulesEditorComponent.problemOf([{ documentType: 'X', levels: [{ minAmount: 0, maxAmount: 5, approverRole: 'A' }, { minAmount: 3, approverRole: 'B', maxAmount: 1 }] }]))
        .toContain('max is below min');
    });
  });

  describe('UomGroupsEditorComponent', () => {
    it('one base per group; codes unique; factors above 0', async () => {
      await TestBed.configureTestingModule({ imports: [UomGroupsEditorComponent], providers: [provideNoopAnimations()] }).compileComponents();
      const f = TestBed.createComponent(UomGroupsEditorComponent);
      const c = f.componentInstance;
      const out: any[] = [];
      c.payloadChange.subscribe(p => out.push(p));
      f.componentRef.setInput('payload', { groups: [{ name: 'Weight', units: [{ code: 'KG', name: 'Kilogram', isBase: true }, { code: 'G', name: 'Gram', conversionFactor: 0.001 }] }] });
      f.detectChanges();
      expect(c.baseIndex(c.groups[0])).toBe(0);
      c.setBase(c.groups[0], 1);
      let last = out[out.length - 1];
      expect(last.groups[0].units[1].isBase).toBeTrue();
      expect(last.groups[0].units[0].isBase).toBeUndefined();
      expect(last.groups[0].units[0].conversionFactor).toBe(1);
      expect(c.problem).toBeNull();
      c.addUnit(0);
      expect(c.problem).toContain('not valid');
      c.groups[0].units[2].code = 'kg';
      c.groups[0].units[2].name = 'dup';
      c.changed();
      expect(c.problem).toContain('used twice');
      c.removeUnit(0, 2);
      c.addGroup();
      expect(c.problem).toContain('needs a name');
      c.removeGroup(1);
      expect(c.problem).toBeNull();
    });
  });
});
