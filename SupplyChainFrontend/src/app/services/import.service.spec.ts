import { TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { HttpErrorResponse, HttpEventType, HttpHeaders, HttpResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { Subject, of } from 'rxjs';

import {
  ImportHubService, ImportProgress, ImportProgressEvent, ImportService, UploadEvent, fileNameFrom, importErrorMessage, importErrorText,
  normalizeProgress, saveDownload, watchImportProgress
} from './import.service';
import { AuthService } from '../pages/service/auth.service';
import { environment } from '../../environments/environment';

const API = `${environment.apiUrl}/import`;

describe('ImportService (A40 API-CONTRACT)', () => {
  let service: ImportService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(), provideHttpClientTesting(),
        { provide: AuthService, useValue: { getToken: () => null } }
      ]
    });
    service = TestBed.inject(ImportService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads profiles, one profile with its fields, and saves profile settings', () => {
    service.getProfiles().subscribe();
    http.expectOne(`${API}/profiles`).flush({ success: true, message: '', result: [] });

    service.getProfile('PRODUCT_CATEGORY').subscribe();
    http.expectOne(`${API}/profiles/PRODUCT_CATEGORY`).flush({ success: true, message: '', result: null });

    service.updateProfileSettings('UNIT', { duplicateStrategy: 'UPDATE', maxRowsPerFile: 1000, maxFileSizeMb: 5 }).subscribe();
    const put = http.expectOne(`${API}/profiles/UNIT/settings`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ duplicateStrategy: 'UPDATE', maxRowsPerFile: 1000, maxFileSizeMb: 5 });
    put.flush({ success: true, message: '', result: null });
  });

  it('uploads multipart (file, entityType, strategy, sheet, preset, trimmed notes) and reports progress then the session', () => {
    const events: UploadEvent[] = [];
    const file = new File(['a,b\n1,2'], 'units.csv', { type: 'text/csv' });
    service.upload({ file, entityType: 'UNIT', duplicateStrategy: 'SKIP', sheetName: 'Data', mappingPreset: 'Old ERP', notes: '  first go  ' })
      .subscribe(e => events.push(e));
    const req = http.expectOne(`${API}/sessions`);
    expect(req.request.method).toBe('POST');
    expect(req.request.reportProgress).toBeTrue();
    const form = req.request.body as FormData;
    expect((form.get('file') as File).name).toBe('units.csv');
    expect(form.get('entityType')).toBe('UNIT');
    expect(form.get('duplicateStrategy')).toBe('SKIP');
    expect(form.get('sheetName')).toBe('Data');
    expect(form.get('mappingPreset')).toBe('Old ERP');
    expect(form.get('notes')).toBe('first go');

    req.event({ type: HttpEventType.UploadProgress, loaded: 50, total: 200 });
    req.flush({ success: true, message: '', result: { uuid: 's1' } });
    expect(events[0]).toEqual({ type: 'progress', percent: 25 });
    expect(events[1]).toEqual({ type: 'done', session: jasmine.objectContaining({ uuid: 's1' }) as any });
  });

  it('leaves optional multipart fields out when not given', () => {
    service.upload({ file: new File(['x'], 'a.xlsx'), entityType: 'UNIT', notes: '   ' }).subscribe();
    const form = http.expectOne(`${API}/sessions`).request.body as FormData;
    for (const k of ['duplicateStrategy', 'sheetName', 'mappingPreset', 'notes']) expect(form.has(k)).withContext(k).toBeFalse();
  });

  it('lists sessions with paging and filters; session, preview, mappings, presets', () => {
    service.getSessions({ entityType: 'PRODUCT', status: 'COMPLETED', page: 2, pageSize: 10 }).subscribe();
    const list = http.expectOne(r => r.url === `${API}/sessions`);
    expect(list.request.params.get('entityType')).toBe('PRODUCT');
    expect(list.request.params.get('status')).toBe('COMPLETED');
    expect(list.request.params.get('page')).toBe('2');
    expect(list.request.params.get('pageSize')).toBe('10');
    list.flush({ success: true, message: '', result: { data: [], totalRecords: 0, page: 2, pageSize: 10 } });

    service.getSession('u1').subscribe();
    http.expectOne(`${API}/sessions/u1`).flush({});
    service.getPreview('u1').subscribe();
    const prev = http.expectOne(r => r.url === `${API}/sessions/u1/preview`);
    expect(prev.request.params.get('rows')).toBe('20');
    prev.flush({});
    service.getMappings('u1').subscribe();
    http.expectOne(`${API}/sessions/u1/mappings`).flush({});
    service.getPresets('UNIT').subscribe();
    const pre = http.expectOne(r => r.url === `${API}/mapping-presets`);
    expect(pre.request.params.get('entityType')).toBe('UNIT');
    pre.flush({});
  });

  it('PUTs the mappings, saves a preset, validates, executes, cancels, reads progress and errors', () => {
    service.updateMappings('u1', [{ sourceColumnIndex: 0, targetFieldName: 'code' }, { sourceColumnIndex: 1, targetFieldName: null }]).subscribe();
    const put = http.expectOne(`${API}/sessions/u1/mappings`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ mappings: [{ sourceColumnIndex: 0, targetFieldName: 'code' }, { sourceColumnIndex: 1, targetFieldName: null }] });
    put.flush({});

    service.savePreset('u1', 'Mine').subscribe();
    const save = http.expectOne(`${API}/sessions/u1/mappings/save`);
    expect(save.request.body).toEqual({ presetName: 'Mine' });
    save.flush({});

    for (const [call, path] of [
      [() => service.validate('u1'), 'validate'], [() => service.execute('u1'), 'execute'], [() => service.cancel('u1'), 'cancel']
    ] as [() => any, string][]) {
      call().subscribe();
      const r = http.expectOne(`${API}/sessions/u1/${path}`);
      expect(r.request.method).withContext(path).toBe('POST');
      r.flush({});
    }

    service.getProgress('u1').subscribe();
    http.expectOne(`${API}/sessions/u1/progress`).flush({});
    service.getErrors('u1', 3, 25).subscribe();
    const err = http.expectOne(r => r.url === `${API}/sessions/u1/errors`);
    expect(err.request.params.get('page')).toBe('3');
    expect(err.request.params.get('pageSize')).toBe('25');
    err.flush({});
  });

  it('downloads templates, the workbook and reports as blobs with the response headers', () => {
    const got: HttpResponse<Blob>[] = [];
    service.downloadTemplate('UNIT').subscribe(r => got.push(r));
    service.downloadMigrationWorkbook().subscribe(r => got.push(r));
    service.downloadErrors('u1').subscribe(r => got.push(r));
    service.downloadResults('u1').subscribe(r => got.push(r));
    for (const url of [`${API}/profiles/UNIT/template`, `${API}/migration-workbook`, `${API}/sessions/u1/errors/download`,
                       `${API}/sessions/u1/results/download`]) {
      const r = http.expectOne(url);
      expect(r.request.responseType).toBe('blob');
      r.flush(new Blob(['x']), { headers: { 'Content-Disposition': 'attachment; filename="x.xlsx"' } });
    }
    expect(got.length).toBe(4);
    expect(got[0].headers.get('Content-Disposition')).toContain('x.xlsx');
  });

  it('reads and sets the opening balance date (Finance)', () => {
    service.getOpeningBalanceDate().subscribe();
    http.expectOne(`${environment.apiUrl}/finance/opening-balance-date`).flush({ success: true, message: '', result: { date: null } });
    service.setOpeningBalanceDate('2026-01-01').subscribe();
    const put = http.expectOne(`${environment.apiUrl}/finance/opening-balance-date`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ date: '2026-01-01' });
    put.flush({});
  });
});

describe('import download / error helpers', () => {
  it('fileNameFrom reads filename* and filename, else null', () => {
    expect(fileNameFrom("attachment; filename=\"a.xlsx\"; filename*=UTF-8''Unit%20template.xlsx")).toBe('Unit template.xlsx');
    expect(fileNameFrom('attachment; filename="customers.xlsx"')).toBe('customers.xlsx');
    expect(fileNameFrom('attachment; filename=plain.csv')).toBe('plain.csv');
    expect(fileNameFrom(null)).toBeNull();
    expect(fileNameFrom('attachment')).toBeNull();
  });

  it('saveDownload names the file from the header, else the fallback', () => {
    const anchors: HTMLAnchorElement[] = [];
    spyOn(URL, 'createObjectURL').and.returnValue('blob:x');
    spyOn(URL, 'revokeObjectURL');
    spyOn(HTMLAnchorElement.prototype, 'click').and.callFake(function (this: HTMLAnchorElement) { anchors.push(this); });
    saveDownload(new HttpResponse({ body: new Blob(['x']), headers: new HttpHeaders({ 'Content-Disposition': 'attachment; filename="srv.xlsx"' }) }), 'fb.xlsx');
    saveDownload(new HttpResponse({ body: new Blob(['x']) }), 'fb.xlsx');
    expect(anchors.map(a => a.download)).toEqual(['srv.xlsx', 'fb.xlsx']);
    expect(URL.revokeObjectURL).toHaveBeenCalledTimes(2);
  });

  it('importErrorMessage reads the server sentence out of a JSON blob; importErrorText out of JSON', async () => {
    const blobErr = new HttpErrorResponse({ status: 400, error: new Blob([JSON.stringify({ message: 'Another Product import is already running.' })]) });
    expect(await importErrorMessage(blobErr, 'fb')).toBe('Another Product import is already running.');
    expect(await importErrorMessage(new HttpErrorResponse({ status: 500, error: new Blob(['<html>']) }), 'fb')).toBe('fb');
    expect(importErrorText(new HttpErrorResponse({ status: 400, error: { message: 'The file has no data rows.' } }), 'fb')).toBe('The file has no data rows.');
    expect(importErrorText(new HttpErrorResponse({ status: 0 }), 'fb')).toContain('could not be reached');
    expect(importErrorText(new HttpErrorResponse({ status: 500 }), 'fb')).toBe('fb');
  });

  it('normalizeProgress accepts camelCase and PascalCase', () => {
    expect(normalizeProgress({ sessionUuid: 'a', processed: 1, total: 2, status: 'IMPORTING' }))
      .toEqual({ sessionUuid: 'a', processed: 1, total: 2, status: 'IMPORTING' });
    expect(normalizeProgress({ SessionUuid: 'b', Processed: 3, Total: 4, Status: 'COMPLETED' }))
      .toEqual({ sessionUuid: 'b', processed: 3, total: 4, status: 'COMPLETED' });
  });
});

describe('watchImportProgress — hub events with polling fallback', () => {
  function fakeHub(connected: boolean) {
    const events = new Subject<ImportProgressEvent>();
    return { events, hub: { connect: jasmine.createSpy('connect'), connected: signal(connected), events$: events.asObservable() } };
  }

  it('follows hub events for its own session and completes on a terminal status', fakeAsync(() => {
    const { events, hub } = fakeHub(true);
    const getProgress = jasmine.createSpy('getProgress').and.returnValue(of({ success: true, message: '', result: { processed: 0, total: 900, status: 'IMPORTING' } }));
    const seen: ImportProgress[] = [];
    let completed = false;
    watchImportProgress(hub, getProgress, 's1', 3000).subscribe({ next: p => seen.push(p), complete: () => completed = true });
    expect(hub.connect).toHaveBeenCalled();
    expect(getProgress).toHaveBeenCalledTimes(1);          // one immediate read
    events.next({ sessionUuid: 'other', processed: 5, total: 10, status: 'IMPORTING' });
    events.next({ sessionUuid: 's1', processed: 300, total: 900, status: 'IMPORTING' });
    tick(3000);
    expect(getProgress).toHaveBeenCalledTimes(1);          // hub connected: no polling at 3 s
    events.next({ sessionUuid: 's1', processed: 900, total: 900, status: 'COMPLETED' });
    expect(seen.map(p => p.processed)).toEqual([0, 300, 900]);
    expect(completed).toBeTrue();
    tick(30000);
    expect(getProgress).toHaveBeenCalledTimes(1);          // unsubscribed after completion
  }));

  it('polls GET /progress every 3 s while the hub is not connected', fakeAsync(() => {
    const { hub } = fakeHub(false);
    const answers: ImportProgress[] = [
      { processed: 0, total: 600, status: 'IMPORTING' }, { processed: 200, total: 600, status: 'IMPORTING' },
      { processed: 600, total: 600, status: 'COMPLETED_WITH_ERRORS' }
    ];
    let i = 0;
    const getProgress = jasmine.createSpy('getProgress').and.callFake(() => of({ success: true, message: '', result: answers[Math.min(i++, 2)] }));
    const seen: ImportProgress[] = [];
    let completed = false;
    watchImportProgress(hub, getProgress, 's1', 3000).subscribe({ next: p => seen.push(p), complete: () => completed = true });
    tick(2999);
    expect(getProgress).toHaveBeenCalledTimes(1);
    tick(1);
    expect(getProgress).toHaveBeenCalledTimes(2);
    tick(3000);
    expect(seen.map(p => p.status)).toEqual(['IMPORTING', 'IMPORTING', 'COMPLETED_WITH_ERRORS']);
    expect(completed).toBeTrue();
    tick(9000);
    expect(getProgress).toHaveBeenCalledTimes(3);
    discardPeriodicTasks();
  }));

  it('still polls every fifth tick while connected, so a missed final event cannot hang the page', fakeAsync(() => {
    const { hub } = fakeHub(true);
    const getProgress = jasmine.createSpy('getProgress').and.returnValues(
      of({ success: true, message: '', result: { processed: 0, total: 10, status: 'IMPORTING' } }),
      of({ success: true, message: '', result: { processed: 10, total: 10, status: 'COMPLETED' } }));
    let completed = false;
    watchImportProgress(hub, getProgress, 's1', 3000).subscribe({ complete: () => completed = true });
    tick(12000);
    expect(getProgress).toHaveBeenCalledTimes(1);
    tick(3000);
    expect(getProgress).toHaveBeenCalledTimes(2);
    expect(completed).toBeTrue();
  }));
});

describe('ImportHubService', () => {
  class FakeConnection {
    handlers: Record<string, (e: any) => void> = {};
    close?: () => void;
    on(name: string, h: (e: any) => void) { this.handlers[name] = h; }
    onreconnecting() {}
    onreconnected() {}
    onclose(h: () => void) { this.close = h; }
    start() { return Promise.resolve(); }
    stop() { return Promise.resolve(); }
  }

  it('connects once (with a token), relays ImportProgress and tracks the connection', async () => {
    const conn = new FakeConnection();
    TestBed.configureTestingModule({ providers: [{ provide: AuthService, useValue: { getToken: () => 'jwt' } }] });
    const hub = TestBed.inject(ImportHubService);
    const build = spyOn<any>(hub, 'build').and.returnValue(conn);
    const got: ImportProgressEvent[] = [];
    hub.events$.subscribe(e => got.push(e));
    hub.connect();
    hub.connect();
    expect(build).toHaveBeenCalledTimes(1);
    await Promise.resolve();
    expect(hub.connected()).toBeTrue();
    conn.handlers['ImportProgress']({ sessionUuid: 's1', processed: 1, total: 2, status: 'IMPORTING' });
    expect(got).toEqual([{ sessionUuid: 's1', processed: 1, total: 2, status: 'IMPORTING' }]);
    conn.close!();
    expect(hub.connected()).toBeFalse();
  });

  it('does not connect without a token', () => {
    TestBed.configureTestingModule({ providers: [{ provide: AuthService, useValue: { getToken: () => null } }] });
    const hub = TestBed.inject(ImportHubService);
    const build = spyOn<any>(hub, 'build');
    hub.connect();
    expect(build).not.toHaveBeenCalled();
  });
});
