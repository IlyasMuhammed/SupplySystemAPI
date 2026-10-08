import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { ProductionOrderListComponent } from './production-order-list.component';
import { ProductionOrderListItem, ProductionOrderService } from '../../../../services/production-order.service';
import { AuthService } from '../../../service/auth.service';

// A34 PE-06 — the production order list's Route column: a make-to-order order names its route; others show none.

function item(overrides: Partial<ProductionOrderListItem> = {}): ProductionOrderListItem {
  return {
    uuid: 'po-1', productionNumber: 'PROD-2026-00085', productUuid: 'p-1', productName: 'Custom Gear', productSku: 'CG',
    productVariantUuid: 'v-1', variantName: 'A', bomNumber: 'BOM-1', bomVersion: 1, plannedQuantity: 50, producedQuantity: 0,
    acceptedQuantity: 0, rejectedQuantity: 0, warehouseUuid: 'wh', warehouseName: 'Plant', sourceType: 'SALES_ORDER',
    priority: 2, requiredDate: '2026-10-16T00:00:00', status: 'PLANNED', materialReadiness: 'READY', materialCount: 1,
    shortMaterialCount: 0, createdAt: '2026-10-03T00:00:00Z', updatedAt: '2026-10-03T00:00:00Z',
    isMakeToOrder: true, fulfillmentRouteName: 'Manufacture → Pick & Ship',
    ...overrides
  };
}

describe('ProductionOrderListComponent — A34 route column', () => {
  let fixture: ComponentFixture<ProductionOrderListComponent>;

  beforeEach(async () => {
    const service = jasmine.createSpyObj<ProductionOrderService>('ProductionOrderService', ['getList']);
    service.getList.and.returnValue(of({ success: true, message: '', result: {
      data: [item(), item({ uuid: 'po-2', productionNumber: 'PROD-2026-00086', isMakeToOrder: false, fulfillmentRouteName: null, sourceType: 'MANUAL' })],
      totalRecords: 2, page: 1, pageSize: 20, totalPages: 1 } } as any));

    await TestBed.configureTestingModule({
      imports: [ProductionOrderListComponent],
      providers: [
        provideRouter([]), provideNoopAnimations(),
        { provide: ProductionOrderService, useValue: service },
        { provide: AuthService, useValue: { hasPermission: () => true } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ProductionOrderListComponent);
    fixture.detectChanges();
  });

  it('names the route of a make-to-order order, and none for the others', () => {
    const cells = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="order-route"]')) as HTMLElement[];
    const texts = cells.map(c => c.textContent!.replace(/\s+/g, ' ').trim());
    expect(texts.length).toBe(2);
    expect(texts[0]).toContain('Make to order');
    expect(texts[0]).toContain('Manufacture → Pick & Ship');
    expect(texts[1]).toBe('—');
  });
});
