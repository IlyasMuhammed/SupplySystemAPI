import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { TimelinePanelComponent } from './timeline-panel.component';
import { TimelineService, TimelineDetail } from '../../services/timeline.service';

function detail(): TimelineDetail {
  return {
    traceId: 't-1', firstEventAt: '2026-09-01T00:00:00Z', lastEventAt: '2026-09-02T00:00:00Z', totalEventCount: 2,
    events: [
      { eventType: 'SO_CREATED', interfaceCode: 'SO', documentId: 'so-1', documentNumber: 'SO-2026-00042', occurredAt: '2026-09-01T09:00:00Z', performedByName: 'Sara' },
      { eventType: 'DELIVERY_ISSUED', interfaceCode: 'DELIVERY', documentId: 'd-1', documentNumber: 'DLV-2026-00001', occurredAt: '2026-09-02T09:00:00Z' }
    ]
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('TimelinePanelComponent', () => {
  let fixture: ComponentFixture<TimelinePanelComponent>;
  let component: TimelinePanelComponent;
  let service: jasmine.SpyObj<TimelineService>;

  async function setup() {
    service = jasmine.createSpyObj<TimelineService>('TimelineService', ['getByTraceId', 'getByDocument']);
    service.getByTraceId.and.returnValue(ok(detail()));
    service.getByDocument.and.returnValue(ok(detail()));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [TimelinePanelComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        { provide: TimelineService, useValue: service }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(TimelinePanelComponent);
    component = fixture.componentInstance;
  }

  // ── Sidebar mode: how the six existing detail screens use it ───────────────

  describe('in the sidebar', () => {
    it('loads nothing while it is closed', async () => {
      await setup();
      fixture.componentRef.setInput('documentId', 'pr-1');
      fixture.componentRef.setInput('interfaceCode', 'PR');
      fixture.detectChanges();

      expect(service.getByDocument).not.toHaveBeenCalled();
      expect(service.getByTraceId).not.toHaveBeenCalled();
    });

    it('loads by document when it is opened', async () => {
      await setup();
      fixture.componentRef.setInput('documentId', 'pr-1');
      fixture.componentRef.setInput('interfaceCode', 'PR');
      fixture.detectChanges();

      fixture.componentRef.setInput('visible', true);
      fixture.detectChanges();

      expect(service.getByDocument).toHaveBeenCalledOnceWith('PR', 'pr-1');
      expect(component.detail!.totalEventCount).toBe(2);
    });

    it('prefers the trace id when it has one', async () => {
      await setup();
      fixture.componentRef.setInput('traceId', 't-1');
      fixture.componentRef.setInput('documentId', 'pr-1');
      fixture.componentRef.setInput('interfaceCode', 'PR');
      fixture.componentRef.setInput('visible', true);
      fixture.detectChanges();

      expect(service.getByTraceId).toHaveBeenCalledOnceWith('t-1');
      expect(service.getByDocument).not.toHaveBeenCalled();
    });

    it('does not render the inline layout', async () => {
      await setup();
      fixture.componentRef.setInput('traceId', 't-1');
      fixture.componentRef.setInput('visible', true);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('[data-testid="timeline-inline"]')).toBeNull();
    });

    it('closes when the user clicks outside it, and not when it is already closed', async () => {
      await setup();
      const emitted: boolean[] = [];
      component.visibleChange.subscribe(v => emitted.push(v));
      component.visible = true;

      component.onDocumentMouseDown({ target: document.body } as unknown as MouseEvent);
      expect(component.visible).toBeFalse();
      expect(emitted).toEqual([false]);

      component.onDocumentMouseDown({ target: document.body } as unknown as MouseEvent);
      expect(emitted).withContext('closed already').toEqual([false]);
    });
  });

  // ── Inline mode: the sale order Timeline tab ───────────────────────────────

  describe('inline', () => {
    it('loads as soon as it is created, whether or not visible was ever set', async () => {
      await setup();
      fixture.componentRef.setInput('inline', true);
      fixture.componentRef.setInput('traceId', 't-1');
      fixture.detectChanges();

      expect(service.getByTraceId).toHaveBeenCalledOnceWith('t-1');
      const inline = fixture.nativeElement.querySelector('[data-testid="timeline-inline"]') as HTMLElement;
      expect(inline).not.toBeNull();
      expect(inline.textContent).toContain('SO-2026-00042');
      expect(inline.textContent).toContain('DLV-2026-00001');
      expect(inline.textContent).toContain('Total Events');
    });

    it('shows no sidebar', async () => {
      await setup();
      fixture.componentRef.setInput('inline', true);
      fixture.componentRef.setInput('traceId', 't-1');
      fixture.detectChanges();

      expect(document.body.querySelector('.timeline-panel-sidebar')).toBeNull();
      expect(fixture.nativeElement.querySelector('p-sidebar')).toBeNull();
    });

    it('loads by document when it has no trace id', async () => {
      await setup();
      fixture.componentRef.setInput('inline', true);
      fixture.componentRef.setInput('documentId', 'so-1');
      fixture.componentRef.setInput('interfaceCode', 'SO');
      fixture.detectChanges();

      expect(service.getByDocument).toHaveBeenCalledOnceWith('SO', 'so-1');
    });

    it('loads once, however often it is checked', async () => {
      await setup();
      fixture.componentRef.setInput('inline', true);
      fixture.componentRef.setInput('traceId', 't-1');
      fixture.detectChanges();
      fixture.detectChanges();
      fixture.componentRef.setInput('visible', true);
      fixture.detectChanges();

      expect(service.getByTraceId).toHaveBeenCalledTimes(1);
    });

    it('loads again when the trace it shows changes', async () => {
      await setup();
      fixture.componentRef.setInput('inline', true);
      fixture.componentRef.setInput('traceId', 't-1');
      fixture.detectChanges();

      fixture.componentRef.setInput('traceId', 't-2');
      fixture.detectChanges();

      expect(service.getByTraceId.calls.allArgs()).toEqual([['t-1'], ['t-2']]);
    });

    it('says so when there is no timeline, without an error', async () => {
      await setup();
      service.getByTraceId.and.returnValue(throwError(() => ({ status: 404 })));
      fixture.componentRef.setInput('inline', true);
      fixture.componentRef.setInput('traceId', 't-1');
      fixture.detectChanges();

      expect(component.isLoading).toBeFalse();
      expect(component.loadFailed).toBeTrue();
      expect(fixture.nativeElement.textContent).toContain('No timeline available');
    });

    it('says so when it has nothing to look up', async () => {
      await setup();
      fixture.componentRef.setInput('inline', true);
      fixture.detectChanges();

      expect(service.getByTraceId).not.toHaveBeenCalled();
      expect(service.getByDocument).not.toHaveBeenCalled();
      expect(component.loadFailed).toBeTrue();
    });

    it('does not close, or emit, when the user clicks elsewhere on the page', async () => {
      await setup();
      const emitted: boolean[] = [];
      component.visibleChange.subscribe(v => emitted.push(v));
      fixture.componentRef.setInput('inline', true);
      fixture.componentRef.setInput('visible', true);
      fixture.componentRef.setInput('traceId', 't-1');
      fixture.detectChanges();

      component.onDocumentMouseDown({ target: document.body } as unknown as MouseEvent);

      expect(emitted).toEqual([]);
    });

    it('opens the document behind an event', async () => {
      await setup();
      const router = TestBed.inject(Router);
      const navigate = spyOn(router, 'navigate').and.resolveTo(true);
      fixture.componentRef.setInput('inline', true);
      fixture.componentRef.setInput('traceId', 't-1');
      fixture.detectChanges();

      component.goToDocument(component.detail!.events[1]);

      expect(navigate).toHaveBeenCalledWith(['/portal/pages/logistics/deliveries', 'd-1']);
    });

    it('filters its events by document type', async () => {
      await setup();
      fixture.componentRef.setInput('inline', true);
      fixture.componentRef.setInput('traceId', 't-1');
      fixture.detectChanges();

      expect(component.filterOptions.map(o => o.value)).toEqual(['ALL', 'SO', 'DELIVERY']);
      component.selectedFilter = 'DELIVERY';
      expect(component.filteredEvents.map(e => e.documentId)).toEqual(['d-1']);
    });
  });
});
