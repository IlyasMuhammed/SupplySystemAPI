import { Routes } from '@angular/router';
import routes from './pages.routes';

import { DeliveryListComponent } from './logistics/deliveries/delivery-list/delivery-list.component';
import { DeliveryDetailComponent } from './logistics/deliveries/delivery-detail/delivery-detail.component';
import { ShipmentListComponent } from './logistics/shipments/shipment-list/shipment-list.component';
import { ShipmentDetailComponent } from './logistics/shipments/shipment-detail/shipment-detail.component';
import { CarrierListComponent } from './logistics/carriers/carrier-list/carrier-list.component';

/**
 * T-21 — the two new screens are only reachable if they are routed, and only safe if they are
 * guarded. Both are single lines in a 300-line route table, and nothing else in the build would
 * notice if either were dropped in a merge.
 */
describe('Logistics routes', () => {
  const flat = routes as Routes;

  const find = (path: string) => flat.find(r => r.path === path);

  // ── TC-21.1 ────────────────────────────────────────────────────────────────

  it('routes the delivery cockpit and the delivery detail', () => {
    expect(find('logistics/deliveries')?.component).toBe(DeliveryListComponent);
    expect(find('logistics/deliveries/:uuid')?.component).toBe(DeliveryDetailComponent);
  });

  it('puts the list before the detail so the static path is not shadowed', () => {
    const list = flat.findIndex(r => r.path === 'logistics/deliveries');
    const detail = flat.findIndex(r => r.path === 'logistics/deliveries/:uuid');

    expect(list).toBeGreaterThan(-1);
    expect(detail).toBeGreaterThan(list);
  });

  // ── TC-21.2 ────────────────────────────────────────────────────────────────

  it('guards both delivery routes', () => {
    for (const path of ['logistics/deliveries', 'logistics/deliveries/:uuid']) {
      const guards = find(path)?.canActivate ?? [];
      expect(guards.length).withContext(`${path} must be guarded`).toBe(1);
    }
  });

  it('guards every logistics route without exception', () => {
    // A screen that reaches the router unguarded is reachable by typing its URL.
    const unguarded = flat
      .filter(r => r.path?.startsWith('logistics/'))
      .filter(r => !(r.canActivate?.length))
      .map(r => r.path);

    expect(unguarded).toEqual([]);
  });

  // ── TC-21.5 ────────────────────────────────────────────────────────────────

  it('leaves the existing carrier and shipment routes alone', () => {
    // Four screens still depend on these until the cockpit replaces them.
    expect(find('logistics/carriers')?.component).toBe(CarrierListComponent);
    expect(find('logistics/shipments')?.component).toBe(ShipmentListComponent);
    expect(find('logistics/shipments/:uuid')?.component).toBe(ShipmentDetailComponent);
    expect(find('logistics/carriers/create')).toBeDefined();
    expect(find('logistics/shipments/create')).toBeDefined();
  });

  it('does not route two components at the same path', () => {
    const logisticsPaths = flat.filter(r => r.path?.startsWith('logistics/')).map(r => r.path);
    const unique = new Set(logisticsPaths);

    expect(unique.size).toBe(logisticsPaths.length);
  });
});
