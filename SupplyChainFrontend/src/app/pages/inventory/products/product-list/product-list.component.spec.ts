import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router } from '@angular/router';
import { of } from 'rxjs';

import { ProductListComponent } from './product-list.component';
import { InventoryService } from '../../../../services/inventory.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { TenantService } from '../../../service/tenant.service';

/** A34-PA-10 — the product list's route-category filter (GET api/products?routeCategory=, contract §4.3). */
describe('ProductListComponent — route category filter (A34-PA-10)', () => {
  let fixture: ComponentFixture<ProductListComponent>;
  let component: ProductListComponent;
  let inventory: jasmine.SpyObj<InventoryService>;
  let features: string[];
  let el: HTMLElement;

  const ok = (result: unknown) => of({ success: true, message: '', result } as any);

  async function setup() {
    inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getCategories', 'getProducts', 'deleteProduct']);
    inventory.getCategories.and.returnValue(ok([]));
    inventory.getProducts.and.returnValue(ok({ data: [], totalRecords: 0 }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ProductListComponent],
      providers: [
        provideNoopAnimations(),
        { provide: InventoryService, useValue: inventory },
        { provide: AttachmentService, useValue: { resolveUrl: (u: string) => u } },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
        { provide: TenantService, useValue: { hasFeature: (c: string) => features.includes(c) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ProductListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const lastFilter = () => inventory.getProducts.calls.mostRecent().args[0]!;

  beforeEach(() => { features = ['MODULE_INVENTORY', 'MODULE_LOGISTICS', 'MODULE_MANUFACTURING']; });

  it('offers All / Stock / Manufacture routes in a Logistics organization that manufactures', async () => {
    await setup();
    expect(el.querySelector('[data-testid="route-category-filter"]')).not.toBeNull();
    expect(component.routeCategoryOptions.map(o => o.value)).toEqual([null, 'STOCK', 'MANUFACTURE']);
    expect(lastFilter().routeCategory).toBeUndefined();
  });

  it('leaves Manufacture out without MODULE_MANUFACTURING, and the whole filter out without Logistics', async () => {
    features = ['MODULE_INVENTORY', 'MODULE_LOGISTICS'];
    await setup();
    expect(component.routeCategoryOptions.map(o => o.value)).toEqual([null, 'STOCK']);
    features = ['MODULE_INVENTORY'];
    await setup();
    expect(el.querySelector('[data-testid="route-category-filter"]')).toBeNull();
  });

  it('asks the server for products with a route of that category, from page 1; reset clears it', async () => {
    await setup();
    component.currentPage = 3;
    component.selectedRouteCategory = 'MANUFACTURE';
    component.onFilterChange();
    expect(lastFilter().routeCategory).toBe('MANUFACTURE');
    expect(lastFilter().page).toBe(1);
    component.resetFilters();
    expect(component.selectedRouteCategory).toBeNull();
    expect(lastFilter().routeCategory).toBeUndefined();
  });
});
