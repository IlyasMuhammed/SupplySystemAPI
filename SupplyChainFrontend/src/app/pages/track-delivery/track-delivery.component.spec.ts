import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { TrackDeliveryComponent } from './track-delivery.component';
import { PublicTrackingService, PublicTrackingModel } from '../../services/public-tracking.service';

function tracking(overrides: Partial<PublicTrackingModel> = {}): PublicTrackingModel {
  return {
    reference: 'SHP-2026-00042',
    status: 'Out for delivery',
    carrier: 'Beta Road',
    estimatedArrival: '2026-09-20T14:00:00Z',
    events: [
      { status: 'Collected',       occurredAt: '2026-09-18T09:00:00Z', location: 'Lahore hub' },
      { status: 'In transit',      occurredAt: '2026-09-18T15:00:00Z' },
      { status: 'Out for delivery', occurredAt: '2026-09-19T07:00:00Z', location: 'Karachi' }
    ],
    lastUpdatedAt: '2026-09-19T07:00:00Z',
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('TrackDeliveryComponent', () => {
  let fixture: ComponentFixture<TrackDeliveryComponent>;
  let component: TrackDeliveryComponent;
  let api: jasmine.SpyObj<PublicTrackingService>;

  async function setup(
    response: any = ok(tracking()),
    token: string | null = 'a'.repeat(64)) {

    api = jasmine.createSpyObj<PublicTrackingService>('PublicTrackingService', ['track']);
    api.track.and.returnValue(response);

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [TrackDeliveryComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: PublicTrackingService, useValue: api },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: { get: () => token } } }
        }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(TrackDeliveryComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function text(testId: string): string {
    return el(testId)?.textContent?.trim() ?? '';
  }

  // ── The answer ──────────────────────────────────────────────────────────────

  it('looks the delivery up by the token in the URL', async () => {
    await setup();

    expect(api.track).toHaveBeenCalledWith('a'.repeat(64));
    expect(component.tracking?.reference).toBe('SHP-2026-00042');
  });

  it('leads with where the goods are, in the words the server chose', async () => {
    await setup();

    expect(text('status')).toBe('Out for delivery');
    expect(text('reference')).toBe('SHP-2026-00042');
    expect(text('carrier')).toContain('Beta Road');
  });

  it('says when a delivery arrived, which is what most people open the page to learn', async () => {
    await setup(ok(tracking({
      status: 'Delivered', deliveredAt: '2026-09-20T11:30:00Z', estimatedArrival: undefined
    })));

    expect(component.isDelivered).toBeTrue();
    expect(text('delivered-at')).toContain('Delivered on');
    expect(el('eta')).withContext('an estimate is pointless once it has arrived').toBeNull();
  });

  it('shows the estimate while it is still moving', async () => {
    await setup();

    expect(text('eta')).toContain('Expected');
    expect(el('delivered-at')).toBeNull();
  });

  it('says plainly when there is no estimate, rather than leaving a blank', async () => {
    await setup(ok(tracking({ estimatedArrival: undefined })));

    expect(text('no-eta')).toContain('No delivery date has been estimated');
  });

  it('shows no estimate for something already returned or cancelled', async () => {
    await setup(ok(tracking({ status: 'Returned to sender', estimatedArrival: '2026-09-20T14:00:00Z' })));

    expect(component.isSettled).toBeTrue();
    expect(el('eta')).toBeNull();
    expect(el('no-eta')).toBeNull();
  });

  it('lists the timeline oldest first, with the place where the carrier gave one', async () => {
    await setup();

    const rows = fixture.nativeElement.querySelectorAll('[data-testid="timeline"] li');

    expect(rows.length).toBe(3);
    expect(rows[0].textContent).toContain('Collected');
    expect(rows[0].textContent).toContain('Lahore hub');
    expect(rows[2].textContent).toContain('Out for delivery');
  });

  it('says so when the carrier has reported nothing yet', async () => {
    await setup(ok(tracking({ events: [], lastUpdatedAt: undefined })));

    expect(text('no-events')).toContain('Nothing has been reported');
    expect(el('timeline')).toBeNull();
  });

  // ── Failing ─────────────────────────────────────────────────────────────────

  it('gives one message for every kind of bad link', async () => {
    await setup(throwError(() => ({ status: 404 })));

    expect(component.notFound).toBeTrue();
    expect(text('not-found')).toContain('could not find that delivery');

    // Distinguishing "no such link" from "that link was revoked" would confirm which, to
    // somebody guessing.
    expect(text('not-found')).not.toContain('revoked');
    expect(text('not-found')).not.toContain('expired link');
  });

  it('tells somebody to retry when the failure is ours, not their link’s', async () => {
    await setup(throwError(() => ({ status: 500 })));

    expect(component.failed).toBeTrue();
    expect(component.notFound).toBeFalse();
    expect(text('failed')).toContain('Your link is fine');
  });

  it('does not call the server at all when there is no token', async () => {
    await setup(ok(tracking()), null);

    expect(api.track).not.toHaveBeenCalled();
    expect(component.notFound).toBeTrue();
  });

  it('stops loading whatever happens', async () => {
    await setup(throwError(() => ({ status: 404 })));
    expect(component.isLoading).toBeFalse();

    await setup();
    expect(component.isLoading).toBeFalse();
  });

  // ── What the page must not be ───────────────────────────────────────────────

  it('has nowhere to click through to', async () => {
    await setup();

    const links = fixture.nativeElement.querySelectorAll('a, button, [routerLink]');

    expect(links.length).withContext(
      'a page served to strangers should not offer them anywhere to go').toBe(0);
  });

  it('renders nothing the server did not send', async () => {
    await setup();

    const rendered: string = fixture.nativeElement.textContent;

    // The server whitelists its payload; this asserts the page does not reintroduce anything by
    // reaching for a field that is not on the model.
    expect(rendered).not.toContain('undefined');
    expect(rendered).not.toContain('[object Object]');
    expect(rendered).not.toContain('AWB');
  });
});
