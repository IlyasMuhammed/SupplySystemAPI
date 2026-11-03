import { of, throwError } from 'rxjs';

import { DunningProceduresComponent } from './dunning-procedures.component';
import { mount, procedure } from '../ar-ap.testing';

describe('DunningProceduresComponent (A48 tasks 577–579)', () => {
  const setup = (perms: string[]) => mount(DunningProceduresComponent, { perms, prepare: api => {
    api.getProcedures.and.returnValue(of([procedure({ isDefault: false, code: 'GENTLE', uuid: 'p2', blockSalesAtLevel: null }), procedure()]));
    api.createProcedure.and.returnValue(of(procedure({ uuid: 'p3', code: 'NEW' })));
    api.updateProcedure.and.returnValue(of(procedure()));
  } });

  it('lists procedures default first, the first expanded with its levels (578)', async () => {
    const m = await setup(['AR_VIEW']);
    expect(m.c.procedures.map(p => p.code)).toEqual(['STANDARD', 'GENTLE']);
    expect(m.text('proc-STANDARD')).toContain('Block sales: Level 4');
    expect(m.text('proc-GENTLE')).toContain('Block sales: Never');
    expect(m.q('levels-STANDARD')).not.toBeNull();
    expect(m.q('levels-GENTLE')).toBeNull();
    m.q('toggle-GENTLE')!.click();
    m.fixture.detectChanges();
    expect(m.q('levels-GENTLE')).not.toBeNull();
    expect(m.q('new')).toBeNull();
  });

  it('create: levels add / remove renumber, the rules are checked first, then POST (579)', async () => {
    const m = await setup(['FINANCE_AR_AP_ADMIN']);
    m.c.openNew();
    m.c.addLevel();
    m.c.addLevel();
    expect(m.c.draft!.levels.map(l => l.levelNumber)).toEqual([1, 2, 3]);
    m.c.draft!.blockSalesAtLevel = 3;
    m.c.removeLevel(1);
    expect(m.c.draft!.levels.map(l => l.levelNumber)).toEqual([1, 2]);
    expect(m.c.draft!.blockSalesAtLevel).toBeNull();
    m.c.save();
    expect(m.c.formError).toBe('Enter the procedure code.');
    m.c.draft!.code = 'firm';
    m.c.draft!.name = 'Firm';
    m.c.save();
    expect(m.api.createProcedure).toHaveBeenCalledWith(jasmine.objectContaining({ code: 'FIRM', name: 'Firm', description: null }));
    expect(m.c.formOpen).toBeFalse();
  });

  it('edit sends PUT with the levels as a set; a server refusal shows in the form', async () => {
    const m = await setup(['FINANCE_AR_AP_ADMIN']);
    m.api.updateProcedure.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Dunning levels must be numbered 1 to 4 without gaps.' } })));
    m.c.openEdit(m.c.procedures[0]);
    m.c.save();
    expect(m.api.updateProcedure).toHaveBeenCalledWith('p1', jasmine.objectContaining({ levels: jasmine.any(Array) }));
    expect(m.c.formError).toContain('without gaps');
  });

  it('DUNNING_ADMIN alone cannot edit procedures (contract §3)', async () => {
    const m = await setup(['DUNNING_ADMIN']);
    expect(m.c.canEdit).toBeFalse();
  });
});
