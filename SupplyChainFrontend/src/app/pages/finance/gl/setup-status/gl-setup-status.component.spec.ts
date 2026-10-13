import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { MessageService } from 'primeng/api';
import { throwError } from 'rxjs';

import { GlSetupStatusComponent, RESET_WORD } from './gl-setup-status.component';
import { AccountingService } from '../../../../services/accounting.service';
import { AuthService } from '../../../service/auth.service';
import { fakeApi, fakeAuth, okRes, status, statusDone } from '../gl.testing';

describe('GlSetupStatusComponent (A42 setup status)', () => {
  let fixture: ComponentFixture<GlSetupStatusComponent>;
  let c: GlSetupStatusComponent;
  let api: jasmine.SpyObj<AccountingService>;

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const qa = (id: string) => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`)) as HTMLElement[];

  async function setup(s = statusDone(6, { status: 'Complete', conversionDate: '2026-07-01', setupCompletedAt: '2026-10-12T10:00:00Z', setupCompletedByName: 'Ayesha Khan' }),
                       perms = ['GL_VIEW', 'GL_SETUP_MANAGE']) {
    api = fakeApi();
    api.getSetupStatus.and.returnValue(okRes(s));
    api.getSetupHistory.and.returnValue(okRes([
      { at: '2026-10-12T10:00:00Z', eventType: 'SetupCompleted', summary: 'General ledger ready', userName: 'Ayesha Khan' },
      { at: '2026-10-11T09:00:00Z', eventType: 'TemplateApplied', summary: 'PK_GENERAL (65 accounts)', userName: 'Ayesha Khan' }
    ]));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlSetupStatusComponent],
      providers: [provideNoopAnimations(), provideRouter([]), MessageService, { provide: AccountingService, useValue: api }, { provide: AuthService, useValue: fakeAuth(perms) }]
    }).compileComponents();
    fixture = TestBed.createComponent(GlSetupStatusComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('shows the status, conversion date, who completed it, the checklist and the history', async () => {
    await setup();
    expect(q('status')!.textContent).toContain('Complete');
    expect(q('conversion-date')!.textContent).toContain('1 Jul 2026');
    expect(q('completed-by')!.textContent).toContain('Ayesha Khan');
    expect(q('check-6')!.className).toContain('done');
    const entries = qa('history-entry');
    expect(entries.length).toBe(2);
    expect(entries[0].textContent).toContain('Setup completed');
    expect(entries[1].textContent).toContain('PK_GENERAL');
  });

  it('the checklist marks the next step', async () => {
    await setup(statusDone(2));
    expect(q('check-3')!.textContent).toContain('Next');
    expect(q('check-1')!.textContent).toContain('Done');
  });

  it('Reset needs the typed word; then resets and reloads', async () => {
    await setup();
    c.openReset();
    fixture.detectChanges();
    expect((q('reset-confirm')!.querySelector('button') as HTMLButtonElement).disabled).toBeTrue();
    c.typed = 'reset';
    c.reset();
    expect(api.resetSetup).not.toHaveBeenCalled();
    c.typed = RESET_WORD;
    api.resetSetup.and.returnValue(okRes(status()));
    c.reset();
    expect(api.resetSetup).toHaveBeenCalled();
    expect(c.resetVisible).toBeFalse();
  });

  it('a reset refusal (journals posted) is shown in the dialog', async () => {
    await setup();
    c.openReset();
    c.typed = RESET_WORD;
    api.resetSetup.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { message: 'Cannot reset after journal entries posted.' } })));
    c.reset();
    fixture.detectChanges();
    expect(c.resetError).toBe('Cannot reset after journal entries posted.');
    expect(c.resetVisible).toBeTrue();
  });

  it('no Reset without GL_SETUP_MANAGE', async () => {
    await setup(undefined, ['GL_VIEW']);
    expect(q('reset')).toBeNull();
  });
});
