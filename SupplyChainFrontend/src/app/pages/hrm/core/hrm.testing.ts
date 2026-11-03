import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';

import { environment } from '../../../../environments/environment';
import { AuthService } from '../../service/auth.service';

export const API = `${environment.apiUrl}/`;

export interface Mounted<T> {
  fixture: ComponentFixture<T>;
  c: T;
  http: HttpTestingController;
  toast: jasmine.Spy;
  confirm: jasmine.Spy | null;
  q: (id: string) => HTMLElement | null;
  text: (id: string) => string;
  /** The open request for this path (query string ignored), answered as `{ success, result }`, then the view refreshed. */
  flush: (path: string, result: unknown, method?: string) => TestRequest;
  expectReq: (path: string, method?: string) => TestRequest;
  refuse: (path: string, status: number, message: string, method?: string) => void;
  detect: () => void;
}

export async function mountHrm<T>(type: Type<T>, opts: { perms?: string[]; params?: Record<string, string>; data?: Record<string, unknown>; providers?: unknown[] } = {}): Promise<Mounted<T>> {
  const perms = opts.perms ?? [];
  const auth = { hasPermission: (c: string) => perms.includes(c), hasAnyPermission: (...c: string[]) => c.some(x => perms.includes(x)), isAuthenticated: () => true };
  await TestBed.resetTestingModule().configureTestingModule({
    imports: [type],
    providers: [provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: AuthService, useValue: auth }, ...((opts.providers ?? []) as any[]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(opts.params ?? {}), queryParamMap: convertToParamMap({}), data: opts.data ?? {} } } }]
  }).compileComponents();
  const http = TestBed.inject(HttpTestingController);
  const fixture = TestBed.createComponent(type);
  const inj = fixture.debugElement.injector;
  const messages = inj.get(MessageService, null);
  const toast = messages ? spyOn(messages, 'add') : jasmine.createSpy('toast');
  const confirmations = inj.get(ConfirmationService, null);
  const confirm = confirmations ? spyOn(confirmations, 'confirm').and.callFake((o: any) => { o.accept(); return null as any; }) : null;
  fixture.detectChanges();
  const detect = () => { fixture.detectChanges(); };
  const q = (id: string) => (fixture.nativeElement.querySelector(`[data-testid="${id}"]`) ?? document.body.querySelector(`[data-testid="${id}"]`)) as HTMLElement | null;
  const expectReq = (path: string, method?: string) => http.expectOne(r => r.url === API + path && (!method || r.method === method), `${method ?? 'any'} ${path}`);
  return {
    fixture, c: fixture.componentInstance, http, toast, confirm, q, detect, expectReq,
    text: id => (q(id)?.textContent ?? '').replace(/\s+/g, ' ').trim(),
    flush: (path, result, method) => { const r = expectReq(path, method); r.flush({ success: true, message: '', result }); detect(); return r; },
    refuse: (path, status, message, method) => { expectReq(path, method).flush({ success: false, message }, { status, statusText: 'Refused' }); detect(); }
  };
}

/** Blob reads finish on their own time: wait for the condition rather than guessing a delay. */
export async function until(cond: () => boolean, ms = 2000): Promise<void> {
  const end = Date.now() + ms;
  while (!cond() && Date.now() < end) await new Promise(r => setTimeout(r, 10));
}

/** Answers every read still open (lookups) with an empty list. */
export function drain(http: HttpTestingController): void {
  http.match(r => r.method === 'GET').forEach(r => { if (!r.cancelled) r.flush({ success: true, message: '', result: [] }); });
}

export const page = <T>(data: T[], extra: Record<string, unknown> = {}) => ({ data, totalRecords: data.length, page: 1, pageSize: 25, totalPages: 1, hasNext: false, hasPrevious: false, ...extra });
