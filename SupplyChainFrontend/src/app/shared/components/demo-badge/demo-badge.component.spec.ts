import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DemoBadgeComponent } from './demo-badge.component';
import { DemoRefsStore } from './demo-refs.store';
import { environment } from '../../../../environments/environment';

const REFS = `${environment.apiUrl}/onboarding/demo/refs`;

@Component({
  standalone: true,
  imports: [DemoBadgeComponent],
  template: `
    @for (r of rows; track r) { <span class="row"><app-demo-badge entityType="Customer" [entityRef]="r"></app-demo-badge></span> }
    <app-demo-badge entityType="Product" [entityRef]="null"></app-demo-badge>
  `
})
class HostComponent { rows = ['AAA-1', 'bbb-2', 'ccc-3']; }

describe('DemoBadgeComponent + DemoRefsStore (A50 task 312)', () => {
  let fixture: ComponentFixture<HostComponent>;
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
  });

  const badges = () => Array.from(fixture.nativeElement.querySelectorAll('[data-testid="demo-badge"]')) as HTMLElement[];

  it('asks once per entity type for the whole list, and marks only the demo rows (ids compared ignoring case)', () => {
    const reqs = http.match(r => r.url === REFS);
    expect(reqs.length).toBe(1);
    expect(reqs[0].request.params.get('entityType')).toBe('Customer');
    reqs[0].flush({ success: true, message: '', result: ['aaa-1', 'CCC-3'] });
    fixture.detectChanges();
    expect(badges().length).toBe(2);
    expect(badges()[0].textContent?.trim()).toBe('Demo');
    http.verify();      // the null ref never asked for Product
  });

  it('shows nothing when the lookup fails', () => {
    http.expectOne(r => r.url === REFS).flush({ message: 'boom' }, { status: 500, statusText: 'Error' });
    fixture.detectChanges();
    expect(badges().length).toBe(0);
  });

  it('reuses the answer, and asks again after invalidate()', () => {
    http.expectOne(r => r.url === REFS).flush({ success: true, message: '', result: [] });
    const store = TestBed.inject(DemoRefsStore);
    store.refs('Customer').subscribe();
    http.expectNone(r => r.url === REFS);
    store.invalidate();
    store.refs('Customer').subscribe();
    http.expectOne(r => r.url === REFS).flush({ success: true, message: '', result: [] });
  });
});

describe('DemoRefsStore without HttpClient', () => {
  it('answers "no demo records" instead of throwing', (done) => {
    TestBed.configureTestingModule({});
    TestBed.inject(DemoRefsStore).isDemo('Customer', 'x').subscribe(v => { expect(v).toBeFalse(); done(); });
  });
});
