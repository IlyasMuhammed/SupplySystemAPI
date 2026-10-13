import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { AccountTreeNode, AccountingService, summarize } from './accounting.service';
import { environment } from '../../environments/environment';

const API = `${environment.apiUrl}/finance`;

function node(code: string, isGroup: boolean, children: AccountTreeNode[] = []): AccountTreeNode {
  return {
    uuid: code, accountCode: code, accountName: code, materializedPath: code, level: code.length, rootType: 'Asset', isGroup,
    isSystemAccount: false, isActive: true, hasTransactions: false, openingBalanceDebit: 0, openingBalanceCredit: 0, currentBalance: 0,
    sortOrder: 0, children
  };
}

// A42 — docs/general-ledger/API-CONTRACT.md.
describe('AccountingService (A42)', () => {
  let service: AccountingService;
  let http: HttpTestingController;
  const ok = (req: ReturnType<HttpTestingController['expectOne']>, result: unknown = null) => req.flush({ success: true, message: '', result });

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(AccountingService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('templates: filters only when given, filters endpoint, preview, downloads as blobs with the response', () => {
    service.getTemplates({ countryCode: 'PAK', businessType: null, search: '' }).subscribe();
    const list = http.expectOne(r => r.url === `${API}/templates`);
    expect(list.request.params.get('countryCode')).toBe('PAK');
    expect(list.request.params.has('businessType')).toBeFalse();
    expect(list.request.params.has('search')).toBeFalse();
    ok(list, []);

    service.getTemplateFilters().subscribe();
    ok(http.expectOne(`${API}/templates/filters`), { countries: [], businessTypes: [] });
    service.previewTemplate('t1').subscribe();
    ok(http.expectOne(`${API}/templates/t1/preview`), []);

    let fileName: string | null = null;
    service.downloadTemplate('t1', 'csv').subscribe(res => fileName = res.headers.get('Content-Disposition'));
    const dl = http.expectOne(r => r.url === `${API}/templates/t1/download`);
    expect(dl.request.params.get('format')).toBe('csv');
    expect(dl.request.responseType).toBe('blob');
    dl.flush(new Blob(['a']), { headers: { 'Content-Disposition': 'attachment; filename=PK_GENERAL.csv' } });
    expect(fileName!).toContain('PK_GENERAL.csv');

    service.downloadBlankTemplate('json').subscribe();
    const blank = http.expectOne(r => r.url === `${API}/templates/blank`);
    expect(blank.request.params.get('format')).toBe('json');
    blank.flush(new Blob(['{}']));
  });

  it('getChart accepts { roots, summary } and a bare array (summary computed)', () => {
    let tree: any;
    service.getChart().subscribe(t => tree = t);
    ok(http.expectOne(`${API}/chart-of-accounts`), { roots: [node('1', true)], summary: { totalAccounts: 9, postableAccounts: 4, lastModifiedAt: '2026-10-01' } });
    expect(tree.summary.totalAccounts).toBe(9);

    service.getChart().subscribe(t => tree = t);
    ok(http.expectOne(`${API}/chart-of-accounts`), [node('1', true, [node('11', true, [node('1101', false), node('1102', false)])])]);
    expect(tree.roots.length).toBe(1);
    expect(tree.summary).toEqual({ totalAccounts: 4, postableAccounts: 2, lastModifiedAt: null });
    expect(summarize([])).toEqual({ totalAccounts: 0, postableAccounts: 0, lastModifiedAt: null });
  });

  it('chart CRUD, flat filters, suggest-code, apply, upload (multipart), export', () => {
    service.getAccounts({ isGroup: false, isActive: true, rootType: 'Asset' }).subscribe();
    const flat = http.expectOne(r => r.url === `${API}/chart-of-accounts/flat`);
    expect(flat.request.params.get('isGroup')).toBe('false');
    expect(flat.request.params.get('isActive')).toBe('true');
    expect(flat.request.params.get('rootType')).toBe('Asset');
    ok(flat, []);

    service.suggestCode('p1').subscribe();
    const sug = http.expectOne(r => r.url === `${API}/chart-of-accounts/suggest-code`);
    expect(sug.request.params.get('parentUuid')).toBe('p1');
    ok(sug, { code: '1113' });

    service.createAccount({ parentUuid: 'p1', accountCode: '1113', accountName: 'HBL' }).subscribe();
    const post = http.expectOne(`${API}/chart-of-accounts`);
    expect(post.request.method).toBe('POST');
    expect(post.request.body.accountCode).toBe('1113');
    ok(post, {});

    service.updateAccount('a1', { accountName: 'X' }).subscribe();
    expect(http.expectOne(`${API}/chart-of-accounts/a1`).request.method).toBe('PUT');
    service.deleteAccount('a1').subscribe();
    expect(http.expectOne(`${API}/chart-of-accounts/a1`).request.method).toBe('DELETE');

    service.applyTemplate('t1').subscribe();
    const apply = http.expectOne(`${API}/chart-of-accounts/apply-template`);
    expect(apply.request.body).toEqual({ templateUuid: 't1' });
    ok(apply, { accountsCreated: 65, defaultsAssigned: 12, status: 'AccountsCreated' });

    service.startBlankChart().subscribe();
    const blank = http.expectOne(`${API}/chart-of-accounts/start-blank`);
    expect(blank.request.method).toBe('POST');
    ok(blank, { accountsCreated: 8, defaultsAssigned: 1, status: 'AccountsCreated' });

    service.uploadChart(new File(['x'], 'coa.csv')).subscribe();
    const up = http.expectOne(`${API}/chart-of-accounts/upload`);
    expect(up.request.body instanceof FormData).toBeTrue();
    expect((up.request.body as FormData).get('file')).toBeTruthy();
    ok(up, {});

    service.exportChart('json').subscribe();
    const ex = http.expectOne(r => r.url === `${API}/chart-of-accounts/export`);
    expect(ex.request.params.get('format')).toBe('json');
    ex.flush(new Blob(['[]']));
  });

  it('fiscal years, periods, defaults, opening balances', () => {
    service.createFiscalYear({ fiscalYearName: 'FY', startDate: '2026-07-01', endDate: '2027-06-30', reportingCurrencyCode: 'PKR' }).subscribe();
    expect(http.expectOne(`${API}/fiscal-years`).request.body.startDate).toBe('2026-07-01');
    service.closeFiscalYear('y1').subscribe();
    expect(http.expectOne(`${API}/fiscal-years/y1/close`).request.method).toBe('POST');
    service.updatePeriod('p1', { status: 'Locked' }).subscribe();
    const per = http.expectOne(`${API}/fiscal-periods/p1`);
    expect(per.request.method).toBe('PUT');
    expect(per.request.body).toEqual({ status: 'Locked' });

    service.saveDefaults([{ defaultKey: 'DEFAULT_AR', accountUuid: 'a1' }]).subscribe();
    expect(http.expectOne(`${API}/accounting-defaults`).request.body).toEqual({ items: [{ defaultKey: 'DEFAULT_AR', accountUuid: 'a1' }] });
    service.setDefault('DEFAULT_AR', null).subscribe();
    expect(http.expectOne(`${API}/accounting-defaults/DEFAULT_AR`).request.body).toEqual({ accountUuid: null });
    service.validateDefaults().subscribe();
    expect(http.expectOne(`${API}/accounting-defaults/validate`).request.method).toBe('POST');

    service.saveOpeningBalances([{ accountUuid: 'a', debitAmount: 10, creditAmount: 0 }]).subscribe();
    expect(http.expectOne(`${API}/opening-balances/batch`).request.body.entries.length).toBe(1);
    service.postOpeningBalances({ acknowledgeDifference: true }).subscribe();
    expect(http.expectOne(`${API}/opening-balances/post`).request.body).toEqual({ acknowledgeDifference: true });
  });

  it('setup status is remembered for the menu; ensureSetupState loads once', async () => {
    expect(service.setupState()).toBeNull();
    service.ensureSetupState();
    service.ensureSetupState();
    await Promise.resolve();
    const req = http.expectOne(`${API}/setup/status`);
    ok(req, { status: 'FiscalYearSet', nextStep: 4, steps: [], accountsCreatedCount: 60 });
    expect(service.setupState()!.status).toBe('FiscalYearSet');
    service.ensureSetupState();
    await Promise.resolve();
    http.expectNone(`${API}/setup/status`);

    service.resetSetup().subscribe();
    ok(http.expectOne(`${API}/setup/reset`), { status: 'NotStarted', nextStep: 1, steps: [] });
    expect(service.setupState()!.status).toBe('NotStarted');
    service.completeSetup().subscribe();
    ok(http.expectOne(`${API}/setup/complete`), { status: 'Complete', nextStep: 6, steps: [] });
    expect(service.setupState()!.status).toBe('Complete');
  });

  it('dashboard and external mappings (import is multipart with systemType + systemName)', () => {
    service.getDashboard().subscribe();
    ok(http.expectOne(`${API}/gl/dashboard`), {});
    service.getMappings('XERO').subscribe();
    const list = http.expectOne(r => r.url === `${API}/external-mappings`);
    expect(list.request.params.get('systemType')).toBe('XERO');
    ok(list, []);
    service.importMappings(new File(['x'], 'm.csv'), 'XERO', 'Books').subscribe();
    const imp = http.expectOne(`${API}/external-mappings/import`);
    const form = imp.request.body as FormData;
    expect(form.get('systemType')).toBe('XERO');
    expect(form.get('systemName')).toBe('Books');
    ok(imp, { created: 1, updated: 0, errors: [] });
    service.exportMappings('XERO').subscribe();
    http.expectOne(r => r.url === `${API}/external-mappings/export`).flush(new Blob(['']));
  });
});
