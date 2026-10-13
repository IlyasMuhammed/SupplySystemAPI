import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { OpeningBalanceDateComponent } from './opening-balance-date.component';
import { ImportService } from '../../../services/import.service';
import { AuthService } from '../../service/auth.service';

describe('OpeningBalanceDateComponent (A40 D-7, Finance settings)', () => {
  let fixture: ComponentFixture<OpeningBalanceDateComponent>;
  let component: OpeningBalanceDateComponent;
  let api: jasmine.SpyObj<ImportService>;
  let messages: jasmine.SpyObj<MessageService>;
  let permissions: string[];

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(date: string | null, perms = ['FINANCE_SETUP_MANAGE']) {
    permissions = perms;
    api = jasmine.createSpyObj<ImportService>('ImportService', ['getOpeningBalanceDate', 'setOpeningBalanceDate']);
    api.getOpeningBalanceDate.and.returnValue(of({ success: true, message: '', result: { date } }));
    api.setOpeningBalanceDate.and.callFake(d => of({ success: true, message: '', result: { date: d } }));
    messages = jasmine.createSpyObj<MessageService>('MessageService', ['add']);
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [OpeningBalanceDateComponent],
      providers: [
        provideNoopAnimations(),
        { provide: ImportService, useValue: api },
        { provide: MessageService, useValue: messages },
        { provide: AuthService, useValue: { hasPermission: (p: string) => permissions.includes(p) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(OpeningBalanceDateComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('explains the date and says when it is not set', async () => {
    await setup(null);
    expect(q('opening-date-card')!.textContent).toContain('cannot be imported until it is set');
    expect(q('opening-date-unset')).not.toBeNull();
    expect(component.date).toBeNull();
  });

  it('shows a saved date as a local date (no UTC shift)', async () => {
    await setup('2026-01-01');
    expect(component.date!.getFullYear()).toBe(2026);
    expect(component.date!.getMonth()).toBe(0);
    expect(component.date!.getDate()).toBe(1);
    expect(q('opening-date-unset')).toBeNull();
  });

  it('saves the picked day as yyyy-MM-dd; Save only when it changed', async () => {
    await setup('2026-01-01');
    expect(component.dirty).toBeFalse();
    component.date = new Date(2025, 11, 31);
    expect(component.dirty).toBeTrue();
    component.save();
    expect(api.setOpeningBalanceDate).toHaveBeenCalledOnceWith('2025-12-31');
    expect(component.saved).toBe('2025-12-31');
    expect(component.dirty).toBeFalse();
    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'success' }));
  });

  it('a refusal is shown in the server\'s words', async () => {
    await setup(null);
    api.setOpeningBalanceDate.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { message: 'Opening balances were already imported at 2026-01-01.' } })));
    component.date = new Date(2026, 5, 30);
    component.save();
    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'Opening balances were already imported at 2026-01-01.' }));
  });

  it('a 409 (opening documents exist) shows the server\'s sentence and restores the saved date', async () => {
    await setup('2026-01-01');
    const msg = 'The opening balance date cannot be moved before existing opening documents (2026-01-01).';
    api.setOpeningBalanceDate.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { success: false, message: msg } })));
    component.date = new Date(2025, 5, 30);
    component.save();
    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: msg }));
    expect(component.saved).toBe('2026-01-01');
    expect(component.date!.getFullYear()).toBe(2026);
  });

  it('Clear sends null; refused with 409 once opening documents exist', async () => {
    await setup('2026-01-01');
    fixture.detectChanges();
    expect(q('opening-date-clear')).not.toBeNull();
    component.clear();
    expect(api.setOpeningBalanceDate).toHaveBeenCalledWith(null);
    expect(component.saved).toBeNull();

    await setup('2026-01-01');
    api.setOpeningBalanceDate.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { message: 'The opening balance date cannot be cleared: opening documents exist.' } })));
    component.clear();
    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'The opening balance date cannot be cleared: opening documents exist.' }));
    expect(component.saved).toBe('2026-01-01');
  });

  it('read-only without FINANCE_SETUP_MANAGE', async () => {
    await setup('2026-01-01', ['INVOICE_VIEW']);
    expect(q('opening-date-save')).toBeNull();
    component.date = new Date(2026, 1, 1);
    component.save();
    expect(api.setOpeningBalanceDate).not.toHaveBeenCalled();
  });

  it('A42 — GL_SETUP_MANAGE alone may change it too', async () => {
    await setup('2026-01-01', ['GL_SETUP_MANAGE']);
    expect(q('opening-date-save')).not.toBeNull();
    component.date = new Date(2026, 1, 1);
    component.save();
    expect(api.setOpeningBalanceDate).toHaveBeenCalledOnceWith('2026-02-01');
  });

  it('a failed load says so', async () => {
    await setup(null);
    api.getOpeningBalanceDate.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    component.ngOnInit();
    fixture.detectChanges();
    expect(q('opening-date-failed')).not.toBeNull();
  });
});
