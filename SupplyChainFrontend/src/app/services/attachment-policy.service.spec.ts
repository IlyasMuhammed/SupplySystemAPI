import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { AttachmentAccessRule, AttachmentPolicyService } from './attachment-policy.service';
import { environment } from '../../environments/environment';

const POLICY_URL = `${environment.apiOrigin}/api/attachments/policy`;

const PO: AttachmentAccessRule = {
  interfaceCode: 'PO', view: ['PO_VIEW', 'PO_CREATE'], upload: ['PO_CREATE'], delete: ['PO_EDIT'], deleteOwn: ['PO_CREATE']
};
const SALES_INVOICE: AttachmentAccessRule = {
  interfaceCode: 'SALES_INVOICE', view: ['SALES_INVOICE_VIEW'], upload: [], delete: [], deleteOwn: []
};

describe('AttachmentPolicyService', () => {
  let service: AttachmentPolicyService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(AttachmentPolicyService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads each kind of document\'s rule from the server', () => {
    let rule: AttachmentAccessRule | null | undefined;
    service.ruleFor('PO').subscribe(r => rule = r);

    const req = http.expectOne(POLICY_URL);
    expect(req.request.method).toBe('GET');
    req.flush({ success: true, message: '', result: [PO, SALES_INVOICE] });

    expect(rule).toEqual(PO);
  });

  it('asks the server once, however many attachment panels a page has', () => {
    const seen: (AttachmentAccessRule | null)[] = [];
    service.ruleFor('PO').subscribe(r => seen.push(r));
    service.ruleFor('SALES_INVOICE').subscribe(r => seen.push(r));

    http.expectOne(POLICY_URL).flush({ success: true, message: '', result: [PO, SALES_INVOICE] });
    service.ruleFor('PO').subscribe(r => seen.push(r));

    http.expectNone(POLICY_URL);
    expect(seen).toEqual([PO, SALES_INVOICE, PO]);
  });

  it('has no rule for a kind of document the server does not name', () => {
    let rule: AttachmentAccessRule | null | undefined;
    service.ruleFor('SUPPLIER_QUOTATION').subscribe(r => rule = r);

    http.expectOne(POLICY_URL).flush({ success: true, message: '', result: [PO] });

    expect(rule).toBeNull();
  });

  it('answers no rule when the policy cannot be loaded, so a panel stays read-only', () => {
    let rule: AttachmentAccessRule | null | undefined;
    service.ruleFor('PO').subscribe(r => rule = r);

    http.expectOne(POLICY_URL).flush({ message: 'boom' }, { status: 500, statusText: 'Server Error' });

    expect(rule).toBeNull();
  });

  it('treats an unsuccessful or malformed answer as no policy at all', () => {
    let first: AttachmentAccessRule | null | undefined;
    service.ruleFor('PO').subscribe(r => first = r);
    http.expectOne(POLICY_URL).flush({ success: false, message: 'nope', result: null });
    expect(first).toBeNull();

    let second: AttachmentAccessRule | null | undefined;
    service.ruleFor('PO').subscribe(r => second = r);
    http.expectOne(POLICY_URL).flush({ success: true, message: '', result: { PO } });
    expect(second).toBeNull();
  });

  it('does not keep a failure: the next panel asks again', () => {
    service.ruleFor('PO').subscribe();
    http.expectOne(POLICY_URL).flush(null, { status: 0, statusText: 'Network error' });

    let rule: AttachmentAccessRule | null | undefined;
    service.ruleFor('PO').subscribe(r => rule = r);
    http.expectOne(POLICY_URL).flush({ success: true, message: '', result: [PO] });

    expect(rule).toEqual(PO);
  });
});
