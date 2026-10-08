import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { FulfillmentRouteAssignComponent } from './fulfillment-route-assign.component';
import { FulfillmentRouteModel, FulfillmentRoutesService } from '../../../../services/fulfillment-routes.service';
import { CategoryModel, InventoryService } from '../../../../services/inventory.service';

const ROUTE: FulfillmentRouteModel = {
  uuid: 'r-ps', code: 'PICK_AND_SHIP', name: 'Pick & Ship', description: null, isDefault: true, isActive: true, isSystem: true,
  requiresPacking: false, requiresShipping: true, displayOrder: 20,
  steps: [], stepsText: 'Pick → Goods Issue → Ship', statusPath: [], createdDate: ''
};

const CATEGORIES: CategoryModel[] = [
  { id: 15, name: 'Steel', code: 'STL', isActive: true, createdDate: '', subCategories: [
    { id: 151, categoryId: 15, name: 'Rods', isActive: true },
    { id: 152, categoryId: 15, name: 'Old sheets', isActive: false },
    { id: 153, categoryId: 15, name: 'Pipes', isActive: true }
  ] },
  { id: 16, name: 'Retired', code: 'RET', isActive: false, createdDate: '', subCategories: [] },
  { id: 17, name: 'Chemicals', code: 'CHM', isActive: true, createdDate: '', subCategories: [
    { id: 171, categoryId: 17, name: 'Solvents', isActive: true }
  ] }
];

describe('FulfillmentRouteAssignComponent (A33 D-14 bulk assign)', () => {
  let fixture: ComponentFixture<FulfillmentRouteAssignComponent>;
  let component: FulfillmentRouteAssignComponent;
  let routes: jasmine.SpyObj<FulfillmentRoutesService>;
  let inventory: jasmine.SpyObj<InventoryService>;
  let el: HTMLElement;
  let closed: number;

  async function setup(categories$ = of({ success: true, message: '', result: CATEGORIES } as any)) {
    routes = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService', ['assignByCategory']);
    routes.assignByCategory.and.returnValue(of({ success: true, message: '', result: { updated: 12, skipped: 3, total: 15 } } as any));
    inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getCategories']);
    inventory.getCategories.and.returnValue(categories$);

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [FulfillmentRouteAssignComponent],
      providers: [
        provideNoopAnimations(),
        { provide: FulfillmentRoutesService, useValue: routes },
        { provide: InventoryService, useValue: inventory }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(FulfillmentRouteAssignComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('route', ROUTE);
    closed = 0;
    component.closed.subscribe(() => closed++);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const click = (id: string) => ((q(id)!.querySelector('button') ?? q(id)!) as HTMLElement).click();
  const refresh = async () => { fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges(); };

  it('A34: a MANUFACTURE route says only manufactured products are assigned, and counts the rest as skipped (§4.2)', async () => {
    await setup();
    expect(q('mto-note')).toBeNull();
    fixture.componentRef.setInput('route', { ...ROUTE, code: 'MFG_PICK_SHIP', isDefault: false, routeCategory: 'MANUFACTURE' });
    await refresh();
    expect(q('mto-note')!.textContent).toContain('supply method is Manufacture');
    component.onCategoryChange(15);
    component.next();
    component.assign();
    await refresh();
    expect(q('result-skipped')!.textContent).toContain('not manufactured');
  });

  it('offers the active categories, and the chosen category\'s active sub-categories', async () => {
    await setup();
    expect(inventory.getCategories).toHaveBeenCalled();
    expect(component.categoryOptions.map(o => o.value)).toEqual([15, 17]);
    expect(component.subCategoryOptions).toEqual([]);
    component.onCategoryChange(15);
    expect(component.subCategoryOptions.map(o => o.value)).toEqual([151, 153]);
  });

  it('clears the sub-category when the category changes', async () => {
    await setup();
    component.onCategoryChange(15);
    component.subCategoryId = 151;
    component.onCategoryChange(17);
    expect(component.subCategoryId).toBeNull();
  });

  it('needs a category before going on', async () => {
    await setup();
    expect((q('next')!.querySelector('button') as HTMLButtonElement).disabled).toBeTrue();
    component.next();
    expect(component.step).toBe('choose');
  });

  it('asks for confirmation naming the route and category, then assigns to every sub-category and shows the counts', async () => {
    await setup();
    component.onCategoryChange(15);
    await refresh();
    click('next');
    await refresh();
    const text = q('confirm-text')!.textContent!;
    expect(text).toContain('Pick & Ship');
    expect(text).toContain('Steel');
    expect(text).toContain('all its sub-categories');
    expect(text).toContain('no route yet');
    expect(routes.assignByCategory).not.toHaveBeenCalled();

    click('assign');
    await refresh();
    expect(routes.assignByCategory).toHaveBeenCalledWith('r-ps', { categoryId: 15 });
    expect(q('result-updated')!.textContent).toContain('12');
    expect(q('result-skipped')!.textContent).toContain('3');
    expect(q('result-total')!.textContent).toContain('15');
    click('close');
    expect(closed).toBe(1);
  });

  it('sends the optional sub-category and names it in the confirmation', async () => {
    await setup();
    component.onCategoryChange(15);
    component.subCategoryId = 153;
    component.next();
    await refresh();
    expect(q('confirm-text')!.textContent).toContain('Pipes');
    component.assign();
    expect(routes.assignByCategory).toHaveBeenCalledWith('r-ps', { categoryId: 15, subCategoryId: 153 });
  });

  it('shows the server\'s refusal and stays on the confirmation, with no counts', async () => {
    await setup();
    routes.assignByCategory.and.returnValue(throwError(() => ({ status: 400, error: { message: 'The route is inactive.' } })));
    component.onCategoryChange(15);
    component.next();
    component.assign();
    await refresh();
    expect(q('assign-error')!.textContent).toContain('The route is inactive.');
    expect(component.step).toBe('confirm');
    expect(component.result).toBeNull();
  });

  it('says so when the categories cannot be loaded', async () => {
    await setup(throwError(() => ({ status: 500 })) as any);
    expect(q('categories-failed')).not.toBeNull();
  });
});
