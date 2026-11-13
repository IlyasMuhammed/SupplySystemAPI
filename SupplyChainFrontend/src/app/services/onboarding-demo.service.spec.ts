import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { OnboardingDemoService, demoEntityLabel, isStepDone } from './onboarding-demo.service';
import { environment } from '../../environments/environment';

const ONB = `${environment.apiUrl}/onboarding`;

describe('OnboardingDemoService (A50 SAMPLE-DATA.md §1)', () => {
  let svc: OnboardingDemoService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    svc = TestBed.inject(OnboardingDemoService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('sample data: plan, load, delete, refs', () => {
    svc.getSampleData().subscribe();
    expect(http.expectOne(`${ONB}/sample-data`).request.method).toBe('GET');
    svc.loadSampleData().subscribe();
    expect(http.expectOne(`${ONB}/sample-data/load`).request.method).toBe('POST');
    svc.deleteSampleData().subscribe();
    expect(http.expectOne(`${ONB}/sample-data`).request.method).toBe('DELETE');
    svc.getDemoRefs('Customer').subscribe();
    expect(http.expectOne(r => r.url === `${ONB}/demo/refs` && r.params.get('entityType') === 'Customer')).toBeTruthy();
  });

  it('checklist: get, complete a step, dismiss', () => {
    svc.getChecklist().subscribe();
    http.expectOne(`${ONB}/checklist`);
    svc.completeStep('ADD_CUSTOMER').subscribe();
    expect(http.expectOne(`${ONB}/checklist/steps/ADD_CUSTOMER/complete`).request.method).toBe('POST');
    svc.dismissChecklist().subscribe();
    expect(http.expectOne(`${ONB}/checklist/dismiss`).request.method).toBe('POST');
  });

  it('helpers', () => {
    expect(demoEntityLabel('vendors')).toBe('Suppliers');
    expect(demoEntityLabel('openingStock')).toBe('Products with opening stock');
    expect(demoEntityLabel('SomethingNew')).toBe('Something New');
    expect(isStepDone({ stepCode: 'X', title: 'X', completed: true })).toBeTrue();
    expect(isStepDone({ stepCode: 'X', title: 'X', completedAt: '2026-11-09' })).toBeTrue();
    expect(isStepDone({ stepCode: 'X', title: 'X', completed: false })).toBeFalse();
  });
});
