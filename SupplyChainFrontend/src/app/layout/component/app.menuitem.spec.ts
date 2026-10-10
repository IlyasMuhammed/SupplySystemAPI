import { MenuItem } from 'primeng/api';
import { EMPTY } from 'rxjs';
import { AppMenuitem } from './app.menuitem';

// routeActive only needs Router.isActive; the item is built directly (no TestBed) with a fake router whose current
// URL decides what matches, the same 'subset' (path-prefix, segment-aware) rule the real Router applies.
function menuItemAt(url: string, item: MenuItem, siblings: MenuItem[]): AppMenuitem {
  const segs = (u: string) => u.split('?')[0].split('/').filter((s) => !!s);
  const router = {
    events: EMPTY,
    url,
    parseUrl: (u: string) => ({ queryParams: Object.fromEntries(new URLSearchParams(u.split('?')[1] ?? '')) }),
    isActive: (link: string, opts: { paths: string }) => {
      const cur = segs(url), want = segs(link);
      if (opts.paths === 'exact') return cur.join('/') === want.join('/');
      return want.every((s, i) => cur[i] === s);
    }
  };
  const layout = { menuSource$: EMPTY, onMenuStateChange: jasmine.createSpy('onMenuStateChange') };
  const mi = new AppMenuitem(router as any, layout as any);
  mi.item = item;
  mi.siblings = siblings;
  return mi;
}

describe('AppMenuitem — same path, told apart by query params', () => {
  const newWh: MenuItem = { label: 'New Warehouse', routerLink: ['/portal/pages/inventory/warehouses'], queryParams: { action: 'create' } };
  const allWh: MenuItem = { label: 'All Warehouses', routerLink: ['/portal/pages/inventory/warehouses'] };
  const level = [newWh, allWh];

  it('on the list only All Warehouses is highlighted', () => {
    const url = '/portal/pages/inventory/warehouses';
    expect(menuItemAt(url, allWh, level).routeActive).toBeTrue();
    expect(menuItemAt(url, newWh, level).routeActive).toBeFalse();
  });

  it('on ?action=create only New Warehouse is highlighted', () => {
    const url = '/portal/pages/inventory/warehouses?action=create';
    expect(menuItemAt(url, newWh, level).routeActive).toBeTrue();
    expect(menuItemAt(url, allWh, level).routeActive).toBeFalse();
  });
});

describe('AppMenuitem — one highlighted item per level', () => {
  const newOrder: MenuItem = { label: 'New Order', routerLink: ['/portal/pages/manufacturing/production-orders/new'] };
  const allOrders: MenuItem = { label: 'All Orders', routerLink: ['/portal/pages/manufacturing/production-orders'] };
  const purchaseRequired: MenuItem = { label: 'Purchase Required', routerLink: ['/portal/pages/manufacturing/purchase-required'] };
  const level = [newOrder, allOrders, purchaseRequired];

  it('on "New Order" only New Order is highlighted, not All Orders', () => {
    const url = '/portal/pages/manufacturing/production-orders/new';
    expect(menuItemAt(url, newOrder, level).routeActive).toBeTrue();
    expect(menuItemAt(url, allOrders, level).routeActive).toBeFalse();
    expect(menuItemAt(url, purchaseRequired, level).routeActive).toBeFalse();
  });

  it('on the list, All Orders is highlighted', () => {
    const url = '/portal/pages/manufacturing/production-orders';
    expect(menuItemAt(url, allOrders, level).routeActive).toBeTrue();
    expect(menuItemAt(url, newOrder, level).routeActive).toBeFalse();
  });

  it('on "New Order" only New Order claims the route (so All Orders is not marked open either)', () => {
    const url = '/portal/pages/manufacturing/production-orders/new';
    const claims = (item: MenuItem) => {
      const mi = menuItemAt(url, item, level);
      mi.updateActiveStateFromRoute();
      return ((mi as any).layoutService.onMenuStateChange as jasmine.Spy).calls.count();
    };
    expect(claims(newOrder)).toBe(1);
    expect(claims(allOrders)).toBe(0);
  });

  it('on a record page, its list item stays highlighted', () => {
    const url = '/portal/pages/manufacturing/production-orders/003fea6b-bd90-45a9-844d-8c8874fe4d56';
    expect(menuItemAt(url, allOrders, level).routeActive).toBeTrue();
    expect(menuItemAt(url, newOrder, level).routeActive).toBeFalse();
  });
});
