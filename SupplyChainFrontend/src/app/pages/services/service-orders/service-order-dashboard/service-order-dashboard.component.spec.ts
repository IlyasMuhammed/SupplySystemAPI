import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { ServiceOrderDashboardComponent } from './service-order-dashboard.component';
import { ServiceDashboard, ServiceOrderListItem, ServiceOrderService } from '../../../../services/service-order.service';
import { AuthService } from '../../../service/auth.service';

const ok = <T>(result: T) => of({ success: true, message: '', result } as any);

function item(n: number, status: ServiceOrderListItem['status']): ServiceOrderListItem {
  return {
    uuid: 'so-' + n, serviceNumber: 'SVC-' + n, serviceProductName: 'AC installation', customerUuid: 'c', customerName: 'Cool Air Ltd',
    status, priority: 1, materialReadiness: 'READY', quantity: 1, scheduledTime: '10:00'
  };
}

const DATA: ServiceDashboard = {
  today: [item(1, 'READY'), item(2, 'IN_PROGRESS'), item(3, 'READY')],
  waitingForMaterials: [item(4, 'WAITING')],
  mine: [item(2, 'IN_PROGRESS')],
  completionRate: { completedThisWeek: 3, scheduledThisWeek: 4, percent: 75 }
};

// A36-P5-09 — the service dashboard.
describe('ServiceOrderDashboardComponent', () => {
  let fixture: ComponentFixture<ServiceOrderDashboardComponent>;
  let component: ServiceOrderDashboardComponent;
  let service: jasmine.SpyObj<ServiceOrderService>;
  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  async function setup(response: any) {
    service = jasmine.createSpyObj<ServiceOrderService>('ServiceOrderService', ['getDashboard']);
    service.getDashboard.and.returnValue(response);
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ServiceOrderDashboardComponent],
      providers: [
        provideRouter([]), provideNoopAnimations(),
        { provide: ServiceOrderService, useValue: service },
        { provide: AuthService, useValue: { hasPermission: () => true } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ServiceOrderDashboardComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('shows the tiles and the four cards', async () => {
    await setup(ok(DATA));
    expect(text(q('tile-today'))).toBe('3');
    expect(text(q('tile-waiting'))).toBe('1');
    expect(text(q('tile-mine'))).toBe('1');
    expect(text(q('tile-rate'))).toBe('75%');
    for (const id of ['card-today', 'card-waiting', 'card-mine', 'card-rate']) expect(q(id)).withContext(id).not.toBeNull();
    expect(q('card-waiting')!.querySelector('[data-testid="dash-order-SVC-4"]')).not.toBeNull();
  });

  it('groups today\'s services by status in lifecycle order', async () => {
    await setup(ok(DATA));
    expect(component.todayGroups.map(g => [g.status, g.orders.length])).toEqual([['READY', 2], ['IN_PROGRESS', 1]]);
    expect(q('today-group-READY')).not.toBeNull();
  });

  it('draws the completion bar at the percentage, clamped to 0–100', async () => {
    await setup(ok({ ...DATA, completionRate: { completedThisWeek: 5, scheduledThisWeek: 4, percent: 125 } }));
    expect(component.percent).toBe(100);
    expect((q('rate-bar') as HTMLElement).style.width).toBe('100%');
    expect(text(q('rate-figures'))).toContain('5 of 4 completed');
  });

  it('copes with empty lists', async () => {
    await setup(ok({ today: [], waitingForMaterials: [], mine: [], completionRate: { completedThisWeek: 0, scheduledThisWeek: 0, percent: 0 } }));
    expect(text(q('card-today'))).toContain('Nothing scheduled for today.');
    expect(text(q('card-mine'))).toContain('Nothing assigned to you.');
  });

  it('says so when it cannot load', async () => {
    await setup(throwError(() => ({ status: 500 })));
    expect(q('load-failed')).not.toBeNull();
  });
});
