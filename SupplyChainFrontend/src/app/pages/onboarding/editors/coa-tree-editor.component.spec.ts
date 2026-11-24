import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { CoaTreeEditorComponent } from './coa-tree-editor.component';
import { allCoa } from '../onboarding.shared';
import { sampleCoa } from '../onboarding.testing';

/** A50 tasks 128–130 — COA tree: rename, add, remove, role badges, move, drag-and-drop validation. */
describe('CoaTreeEditorComponent', () => {
  let fixture: ComponentFixture<CoaTreeEditorComponent>;
  let c: CoaTreeEditorComponent;
  let emitted: any[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [CoaTreeEditorComponent], providers: [provideNoopAnimations()] }).compileComponents();
    fixture = TestBed.createComponent(CoaTreeEditorComponent);
    c = fixture.componentInstance;
    emitted = [];
    c.payloadChange.subscribe(p => emitted.push(p));
    fixture.componentRef.setInput('payload', { template_code: 'T', accounts: sampleCoa() });
    fixture.detectChanges();
  });

  const codes = () => allCoa(emitted[emitted.length - 1].accounts).map((a: any) => a.code);
  const node = (code: string) => {
    let f: any = null;
    const walk = (ns: any[]) => ns.forEach(n => { if (n.data.code === code) f = n; walk(n.children ?? []); });
    walk(c.nodes);
    return f;
  };

  it('renders the accounts with role badges and the count', () => {
    expect(c.count).toBe(7);
    expect(fixture.nativeElement.querySelector('[data-testid="coa-count"]').textContent).toContain('7 accounts');
    c.expandAll(true);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="coa-role-1111"]').textContent).toContain('DEFAULT_CASH');
  });

  it('role dropdown: only in role-edit mode; a role moves to the new account and can be cleared', () => {
    c.expandAll(true);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="coa-role-select-1111"]')).toBeNull();
    fixture.componentRef.setInput('allowRoleEdit', true);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="coa-role-select-1111"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="coa-role-select-111"]')).toBeNull();
    expect(c.roleOptions.length).toBe(17);

    c.setRole(node('1112'), 'DEFAULT_CASH');
    let accounts = allCoa(emitted[emitted.length - 1].accounts);
    expect(accounts.find((a: any) => a.code === '1112')!.default_account_key).toBe('DEFAULT_CASH');
    expect(accounts.find((a: any) => a.code === '1112')!.is_default_account).toBeTrue();
    expect(accounts.find((a: any) => a.code === '1111')!.default_account_key).toBeUndefined();
    expect(accounts.filter((a: any) => a.default_account_key === 'DEFAULT_CASH').length).toBe(1);

    c.setRole(node('1112'), '');
    accounts = allCoa(emitted[emitted.length - 1].accounts);
    expect(accounts.some((a: any) => a.default_account_key === 'DEFAULT_CASH')).toBeFalse();
    const before = emitted.length;
    c.setRole(node('111'), 'DEFAULT_CASH');
    expect(emitted.length).toBe(before);
  });

  it('renames an account and emits the payload, keeping the template fields', () => {
    const n = node('1112');
    c.edit(n);
    n.data.name = '  Main bank  ';
    c.commit(n);
    const last = emitted[emitted.length - 1];
    expect(last.template_code).toBe('T');
    expect(allCoa(last.accounts).find((a: any) => a.code === '1112')!.name).toBe('Main bank');
    expect(c.editingKey).toBeNull();
  });

  it('adds an account under a level-3 group as a level-4 leaf with the next code', () => {
    c.add(node('111'));
    expect(codes()).toContain('1113');
    const added = allCoa(emitted[emitted.length - 1].accounts).find((a: any) => a.code === '1113')!;
    expect(added.level).toBe(4);
    expect(added.is_group).toBeFalse();
    expect(added.root_type).toBe('Asset');
    expect(c.isNew(node('1113'))).toBeTrue();
    expect(c.canAddUnder(node('1111'))).toBeFalse();
  });

  it('removes an account, but not one carrying a default role', () => {
    c.remove(node('1112'));
    expect(codes()).not.toContain('1112');
    const before = emitted.length;
    c.remove(node('1111'));
    expect(emitted.length).toBe(before);
    expect(c.problem).toContain('DEFAULT_CASH');
  });

  it('"Move to…" offers same-level groups of the same root and moves the account', () => {
    const n = node('1112');
    const targets = c.moveTargets(n).map(t => t.label);
    expect(targets).toEqual(['112 Receivables']);
    c.startMove(n);
    c.moveTarget = node('112').key;
    c.move(n);
    const moved = emitted[emitted.length - 1].accounts[0].children[0].children.find((g: any) => g.code === '112');
    expect(moved.children.map((a: any) => a.code)).toEqual(['1112']);
  });

  it('a drop that breaks the A42 rules is undone', () => {
    // Simulate p-tree accepting a drop that put leaf 1112 directly under root 2 (level 2).
    const n = node('1112');
    const ev = {
      accept: () => {
        const p = node('111');
        p.children = p.children.filter((x: any) => x !== n);
        const root2 = c.nodes[1];
        root2.children = [...(root2.children ?? []), n];
      }
    };
    c.onDrop(ev);
    expect(c.problem).toContain('That move was undone');
    expect(node('111').children.map((x: any) => x.data.code)).toContain('1112');
  });

  it('a valid drop is kept and emitted', () => {
    const n = node('1112');
    c.onDrop({ accept: () => {
      const p = node('111'); p.children = p.children.filter((x: any) => x !== n);
      const t = node('112'); t.children = [...(t.children ?? []), n];
    } });
    expect(c.problem).toBeNull();
    expect(emitted.length).toBeGreaterThan(0);
  });

  it('shows the empty state for a payload without accounts', () => {
    fixture.componentRef.setInput('payload', { accounts: [] });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="coa-empty"]')).toBeTruthy();
  });
});
