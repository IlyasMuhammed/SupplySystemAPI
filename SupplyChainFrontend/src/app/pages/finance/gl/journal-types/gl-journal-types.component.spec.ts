import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { GlJournalTypesComponent } from './gl-journal-types.component';
import { GlJournalService, JournalType } from '../../../../services/gl-journal.service';
import { AccountingService } from '../../../../services/accounting.service';
import { AuthService } from '../../../service/auth.service';
import { fakeApi, fakeAuth } from '../gl.testing';

const jt = (code: string, over: Partial<JournalType> = {}): JournalType => ({
  uuid: 'u' + code, code, name: code + ' Journal', type: 'Miscellaneous', sequencePrefix: code, requiresPartner: false, autoPost: false,
  isSystem: true, isActive: true, sortOrder: 10, isUsed: true, canDelete: false, ...over
});

describe('GlJournalTypesComponent (A47 tasks 264–269)', () => {
  let fixture: ComponentFixture<GlJournalTypesComponent>;
  let c: GlJournalTypesComponent;
  let api: jasmine.SpyObj<GlJournalService>;
  let confirm: jasmine.Spy;
  let toast: jasmine.Spy;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(types: JournalType[], perms = ['GL_VIEW', 'GL_SETUP_MANAGE']) {
    api = jasmine.createSpyObj<GlJournalService>('GlJournalService',
      ['getJournalTypes', 'createJournalType', 'updateJournalType', 'deactivateJournalType', 'activateJournalType', 'deleteJournalType']);
    api.getJournalTypes.and.returnValue(of(types));
    const acc = fakeApi();
    acc.getPostableAccounts.and.returnValue(of([{ uuid: 'a1', accountCode: '1111', accountName: 'Cash' } as any]));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlJournalTypesComponent],
      providers: [provideNoopAnimations(), { provide: GlJournalService, useValue: api }, { provide: AccountingService, useValue: acc },
        { provide: AuthService, useValue: fakeAuth(perms) }]
    }).compileComponents();
    fixture = TestBed.createComponent(GlJournalTypesComponent);
    c = fixture.componentInstance;
    confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm').and.callFake((o: any) => { o.accept(); return null as any; });
    toast = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('table: code, name, prefix, status; system types show the lock and no delete; ordered by sortOrder', async () => {
    await setup([jt('MISC', { sortOrder: 50, sequencePrefix: 'JV' }), jt('SALE', { sortOrder: 10 }), jt('PROJ', { isSystem: false, isUsed: false, canDelete: true, sortOrder: 90, isActive: false })]);
    expect(c.types.map(t => t.code)).toEqual(['SALE', 'MISC', 'PROJ']);
    expect(q('type-MISC')!.textContent).toContain('JV');
    expect(q('lock-SALE')).not.toBeNull();
    expect(q('lock-PROJ')).toBeNull();
    expect(q('delete-SALE')).toBeNull();
    expect(q('delete-PROJ')).not.toBeNull();
    expect(q('status-PROJ')!.textContent).toContain('Inactive');
    expect(q('activate-PROJ')).not.toBeNull();
    expect(api.getJournalTypes).toHaveBeenCalledWith();
  });

  it('new custom type: validated, then POSTed with upper-case code and prefix', async () => {
    await setup([jt('SALE')]);
    c.openNew();
    c.save();
    expect(c.shownProblem).toBe('Enter the code.');
    c.draft = { ...c.draft, code: 'PROJ', name: ' Project billing ', sequencePrefix: 'PROJ', requiresPartner: true, defaultDebitAccountUuid: 'a1' };
    api.createJournalType.and.returnValue(of(jt('PROJ')));
    c.save();
    expect(api.createJournalType).toHaveBeenCalledWith(jasmine.objectContaining({ code: 'PROJ', name: 'Project billing', type: 'Miscellaneous',
      sequencePrefix: 'PROJ', requiresPartner: true, defaultDebitAccountUuid: 'a1', autoPost: false }));
    expect(c.dialogVisible).toBeFalse();
    expect(api.getJournalTypes).toHaveBeenCalledTimes(2);
  });

  it('edit a system type: code and kind locked in the dialog; server refusals shown as is', async () => {
    await setup([jt('SALE', { type: 'Sale', requiresPartner: true })]);
    c.openEdit(c.types[0]);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const code = (q('jt-code') ?? document.querySelector('[data-testid="jt-code"]')) as HTMLInputElement;
    const prefix = (q('jt-prefix') ?? document.querySelector('[data-testid="jt-prefix"]')) as HTMLInputElement;
    expect(code.disabled).toBeTrue();
    expect(prefix.disabled).toBeTrue();
    api.updateJournalType.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409,
      error: { success: false, message: 'Sequence prefix SALE is already used by journal type X.' } })));
    c.save();
    expect(api.updateJournalType).toHaveBeenCalledWith('uSALE', jasmine.objectContaining({ code: 'SALE', type: 'Sale', requiresPartner: true }));
    expect(c.shownProblem).toContain('already used');
  });

  it('deactivate asks first (task 269), then PATCHes; delete only for unused custom types', async () => {
    await setup([jt('CASH'), jt('PROJ', { isSystem: false, isUsed: false, canDelete: true })]);
    api.deactivateJournalType.and.returnValue(of(jt('CASH', { isActive: false })));
    c.confirmDeactivate(c.types[0]);
    expect(confirm.calls.mostRecent().args[0].message).toContain('can no longer be chosen');
    expect(api.deactivateJournalType).toHaveBeenCalledWith('uCASH');
    api.deleteJournalType.and.returnValue(of({ deleted: true }));
    c.confirmDelete(c.types[1]);
    expect(api.deleteJournalType).toHaveBeenCalledWith('uPROJ');
    expect(toast).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'success' }));
  });

  it('read-only for GL_VIEW: no New / Edit / Deactivate', async () => {
    await setup([jt('SALE')], ['GL_VIEW']);
    expect(q('new-type')).toBeNull();
    expect(q('edit-SALE')).toBeNull();
    expect(q('deactivate-SALE')).toBeNull();
  });
});
