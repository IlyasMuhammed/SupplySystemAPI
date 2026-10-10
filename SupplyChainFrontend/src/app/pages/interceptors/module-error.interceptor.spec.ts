import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { MessageService } from 'primeng/api';
import { GLOBAL_TOAST_KEY, moduleErrorInterceptor, moduleNotice, resetModuleErrorNotices } from './module-error.interceptor';
import { ModuleService } from '../../services/module.service';

describe('moduleErrorInterceptor (A37 §1.4)', () => {
  let http: HttpClient;
  let ctrl: HttpTestingController;
  let messages: MessageService;
  let modules: jasmine.SpyObj<ModuleService>;

  beforeEach(() => {
    resetModuleErrorNotices();
    modules = jasmine.createSpyObj<ModuleService>('ModuleService', ['refresh']);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([moduleErrorInterceptor])),
        provideHttpClientTesting(),
        MessageService,
        { provide: ModuleService, useValue: modules }
      ]
    });
    http = TestBed.inject(HttpClient);
    ctrl = TestBed.inject(HttpTestingController);
    messages = TestBed.inject(MessageService);
    spyOn(messages, 'add');
  });

  afterEach(() => ctrl.verify());

  function fail403(body: any) {
    let error: any;
    http.post('/api/production-orders', {}).subscribe({ error: e => (error = e) });
    ctrl.expectOne('/api/production-orders').flush(body, { status: 403, statusText: 'Forbidden' });
    return error;
  }

  const moduleBody = (graceEndsAt: string | null = null) => ({
    success: false, errorCode: 'MODULE_NOT_LICENSED',
    message: 'The Manufacturing module is not enabled for your organization.',
    result: { module: 'MODULE_MANUFACTURING', graceEndsAt }
  });

  it('shows one clear global toast for a module 403, refreshes the modules and passes the error on', () => {
    const error = fail403(moduleBody());
    expect(error.status).toBe(403);
    expect(messages.add).toHaveBeenCalledTimes(1);
    const msg = (messages.add as jasmine.Spy).calls.mostRecent().args[0];
    expect(msg.key).toBe(GLOBAL_TOAST_KEY);
    expect(msg.severity).toBe('warn');
    expect(msg.detail).toContain('The Manufacturing module is switched off for your organization.');
    expect(msg.detail).toContain('Settings › Modules');
    expect(modules.refresh).toHaveBeenCalledTimes(1);
  });

  it('mentions the end of the grace period when there is one', () => {
    fail403(moduleBody('2026-11-09T10:00:00Z'));
    const msg = (messages.add as jasmine.Spy).calls.mostRecent().args[0];
    expect(msg.detail).toContain('until 9 Nov 2026');
  });

  it('announces a module only once per burst of calls', () => {
    fail403(moduleBody());
    fail403(moduleBody());
    fail403(moduleBody());
    expect(messages.add).toHaveBeenCalledTimes(1);
    expect(modules.refresh).toHaveBeenCalledTimes(1);
  });

  it('leaves ordinary 403s (permissions) and other errors alone', () => {
    fail403({ success: false, message: 'You do not have permission.' });
    let error: any;
    http.get('/api/x').subscribe({ error: e => (error = e) });
    ctrl.expectOne('/api/x').flush({ message: 'bad' }, { status: 400, statusText: 'Bad' });
    expect(error.status).toBe(400);
    expect(messages.add).not.toHaveBeenCalled();
    expect(modules.refresh).not.toHaveBeenCalled();
  });

  it('falls back to the name in the server message for an unknown code', () => {
    expect(moduleNotice('', 'The Point of Sale module is not enabled for your organization.'))
      .toContain('The Point of Sale module is switched off');
  });
});
