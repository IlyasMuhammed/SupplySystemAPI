import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { CrmService, activityIcon, crmLabel, leadStatusTone, opportunityStatusTone, ratingTone } from './crm.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/crm`;

// A41 — docs/crm/API-CONTRACT.md §1–§5.
describe('CrmService', () => {
  let service: CrmService;
  let http: HttpTestingController;
  const flushOk = (req: ReturnType<HttpTestingController['expectOne']>, result: unknown = null) =>
    req.flush({ success: true, message: '', result });

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(CrmService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('§1 meta, users (search + limit) and account contacts', () => {
    service.getMeta().subscribe();
    flushOk(http.expectOne(`${BASE}/meta`), {});
    service.getUsers('ali').subscribe();
    const users = http.expectOne(r => r.url === `${BASE}/users`);
    expect(users.request.params.get('search')).toBe('ali');
    expect(users.request.params.get('limit')).toBe('100');
    flushOk(users, []);
    service.getAccountContacts('acc-1').subscribe();
    flushOk(http.expectOne(`${BASE}/accounts/acc-1/contacts`), []);
  });

  it('§3 lists leads with comma-joined statuses, every filter, 25 a page by default', () => {
    service.getLeads({ status: ['NEW', 'CONTACTED'], mine: true, rating: 'HOT', search: 'acme', leadSourceUuid: 's' }).subscribe();
    const req = http.expectOne(r => r.url === `${BASE}/leads`);
    const p = req.request.params;
    expect(p.get('status')).toBe('NEW,CONTACTED');
    expect(p.get('mine')).toBe('true');
    expect(p.get('rating')).toBe('HOT');
    expect(p.get('search')).toBe('acme');
    expect(p.get('leadSourceUuid')).toBe('s');
    expect(p.get('page')).toBe('1');
    expect(p.get('pageSize')).toBe('25');
    flushOk(req, { data: [] });
  });

  it('§3 leaves out unset, empty and empty-array filters', () => {
    service.getLeads({ status: [], search: '', campaignUuid: undefined }).subscribe();
    const req = http.expectOne(r => r.url === `${BASE}/leads`);
    expect(req.request.params.keys().sort()).toEqual(['page', 'pageSize']);
    flushOk(req, { data: [] });
  });

  it('§3 lead lifecycle verbs and bodies', () => {
    service.createLead({ topic: 'T', lastName: 'L' }).subscribe();
    let r = http.expectOne(`${BASE}/leads`);
    expect(r.request.method).toBe('POST');
    flushOk(r);

    service.assignLead('l1', 7, 't1').subscribe();
    r = http.expectOne(`${BASE}/leads/l1/assign`);
    expect(r.request.method).toBe('PATCH');
    expect(r.request.body).toEqual({ userId: 7, salesTeamUuid: 't1' });
    flushOk(r);

    service.changeLeadStatus('l1', 'CONTACTED').subscribe();
    r = http.expectOne(`${BASE}/leads/l1/status`);
    expect(r.request.body.status).toBe('CONTACTED');
    flushOk(r);

    service.moveLeadStage('l1', 'st').subscribe();
    r = http.expectOne(`${BASE}/leads/l1/stage`);
    expect(r.request.body).toEqual({ stageUuid: 'st' });
    flushOk(r);

    service.disqualifyLead('l1', 'No budget').subscribe();
    r = http.expectOne(`${BASE}/leads/l1/disqualify`);
    expect(r.request.method).toBe('POST');
    expect(r.request.body).toEqual({ reason: 'No budget' });
    flushOk(r);

    service.reopenLead('l1').subscribe();
    flushOk(http.expectOne(`${BASE}/leads/l1/reopen`));
    service.qualifyLead('l1').subscribe();
    flushOk(http.expectOne(`${BASE}/leads/l1/qualify`));

    service.deleteLead('l1').subscribe();
    r = http.expectOne(`${BASE}/leads/l1`);
    expect(r.request.method).toBe('DELETE');
    flushOk(r);
  });

  it('§3 duplicate check, kanban, convert', () => {
    service.checkLeadDuplicates({ email: 'a@b.c', phone: null, excludeUuid: 'l1' }).subscribe();
    const dup = http.expectOne(r => r.url === `${BASE}/leads/duplicate-check`);
    expect(dup.request.params.get('email')).toBe('a@b.c');
    expect(dup.request.params.has('phone')).toBeFalse();
    expect(dup.request.params.get('excludeUuid')).toBe('l1');
    flushOk(dup, { leads: [], accounts: [] });

    service.getLeadKanban({ includeClosed: false, salesTeamUuid: 't' }).subscribe();
    const kb = http.expectOne(r => r.url === `${BASE}/leads/kanban`);
    expect(kb.request.params.get('includeClosed')).toBe('false');
    expect(kb.request.params.get('salesTeamUuid')).toBe('t');
    flushOk(kb, { columns: [], unstaged: [], mixedCurrencies: false });

    service.convertLead('l1', { existingAccountUuid: 'acc' }).subscribe();
    const cv = http.expectOne(`${BASE}/leads/l1/convert`);
    expect(cv.request.method).toBe('POST');
    expect(cv.request.body.existingAccountUuid).toBe('acc');
    flushOk(cv, { opportunityUuid: 'o1' });
  });

  it('§3/§4 timelines page, exports ask for xlsx as a blob', () => {
    service.getLeadTimeline('l1', 2).subscribe();
    const tl = http.expectOne(r => r.url === `${BASE}/leads/l1/timeline`);
    expect(tl.request.params.get('page')).toBe('2');
    expect(tl.request.params.get('pageSize')).toBe('50');
    flushOk(tl, { timeline: [] });

    service.exportOpportunityTimeline('o1').subscribe();
    const ex = http.expectOne(r => r.url === `${BASE}/opportunities/o1/timeline/export`);
    expect(ex.request.params.get('format')).toBe('xlsx');
    expect(ex.request.responseType).toBe('blob');
    ex.flush(new Blob(['x']));
  });

  it('§4 opportunity outcome and sales-document actions', () => {
    service.markLost('o1', 'Price', 'Rival Co').subscribe();
    let r = http.expectOne(`${BASE}/opportunities/o1/lost`);
    expect(r.request.body).toEqual({ reason: 'Price', competitor: 'Rival Co' });
    flushOk(r);
    service.markWon('o1').subscribe();
    flushOk(http.expectOne(`${BASE}/opportunities/o1/won`));
    service.reopenOpportunity('o1').subscribe();
    flushOk(http.expectOne(`${BASE}/opportunities/o1/reopen`));
    service.moveOpportunityStage('o1', 's2').subscribe();
    r = http.expectOne(`${BASE}/opportunities/o1/stage`);
    expect(r.request.method).toBe('PATCH');
    flushOk(r);
    service.createInquiry('o1').subscribe();
    flushOk(http.expectOne(`${BASE}/opportunities/o1/create-inquiry`));
    service.createQuotation('o1').subscribe();
    flushOk(http.expectOne(`${BASE}/opportunities/o1/create-quotation`));
    let prefill: any;
    service.createOrder('o1').subscribe(x => prefill = x.result);
    flushOk(http.expectOne(`${BASE}/opportunities/o1/create-order`), { accountUuid: 'acc', opportunityUuid: 'o1' });
    expect(prefill.accountUuid).toBe('acc');
  });

  it('§5 activities: pending, complete, document upload as multipart', () => {
    service.getPendingActivities({ mine: true }).subscribe();
    const pend = http.expectOne(r => r.url === `${BASE}/activities/pending`);
    expect(pend.request.params.get('mine')).toBe('true');
    flushOk(pend, []);

    service.completeActivity('a1').subscribe();
    const done = http.expectOne(`${BASE}/activities/a1/complete`);
    expect(done.request.method).toBe('PATCH');
    flushOk(done);

    const file = new File(['hello'], 'brief.pdf', { type: 'application/pdf' });
    service.attachDocument('a1', file, { direction: 'SENT_TO_CUSTOMER', documentType: 'PROPOSAL' }).subscribe();
    const up = http.expectOne(`${BASE}/activities/a1/documents`);
    const form = up.request.body as FormData;
    expect(form.get('direction')).toBe('SENT_TO_CUSTOMER');
    expect(form.get('documentType')).toBe('PROPOSAL');
    expect(form.has('notes')).toBeFalse();
    expect((form.get('file') as File).name).toBe('brief.pdf');
    flushOk(up);

    service.downloadDocument('d1').subscribe();
    const dl = http.expectOne(`${BASE}/documents/d1/content`);
    expect(dl.request.responseType).toBe('blob');
    dl.flush(new Blob(['x']));
  });

  it('§2 configuration endpoints', () => {
    service.getLeadSources(true).subscribe();
    const ls = http.expectOne(r => r.url === `${BASE}/lead-sources`);
    expect(ls.request.params.get('includeInactive')).toBe('true');
    flushOk(ls, []);
    service.addTeamMember('t1', 9).subscribe();
    const add = http.expectOne(`${BASE}/sales-teams/t1/members`);
    expect(add.request.body).toEqual({ userId: 9, role: 'MEMBER' });
    flushOk(add);
    service.removeTeamMember('t1', 9).subscribe();
    flushOk(http.expectOne(`${BASE}/sales-teams/t1/members/9`));
    service.getPipelineStages({ stageType: 'OPPORTUNITY' }).subscribe();
    const ps = http.expectOne(r => r.url === `${BASE}/pipeline-stages`);
    expect(ps.request.params.get('stageType')).toBe('OPPORTUNITY');
    flushOk(ps, []);
    service.createApiKey('Website', 30).subscribe();
    const key = http.expectOne(`${BASE}/api-keys`);
    expect(key.request.body).toEqual({ name: 'Website', requestsPerMinute: 30 });
    flushOk(key);
  });

  it('helpers: labels, tones, icons', () => {
    expect(crmLabel('PHONE_CALL')).toBe('Phone call');
    expect(crmLabel(null)).toBe('');
    expect(leadStatusTone('CONVERTED')).toBe('ok');
    expect(leadStatusTone('UNKNOWN')).toBe('');
    expect(opportunityStatusTone('LOST')).toBe('er');
    expect(ratingTone('HOT')).toBe('er');
    expect(activityIcon('MEETING')).toBe('pi-users');
    expect(activityIcon('NOPE')).toBe('pi-circle');
  });
});
