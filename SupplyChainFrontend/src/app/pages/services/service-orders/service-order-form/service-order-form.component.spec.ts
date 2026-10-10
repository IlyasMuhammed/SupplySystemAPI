import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';

import { ServiceOrderFormComponent } from './service-order-form.component';
import { ServiceOrderService } from '../../../../services/service-order.service';
import { InventoryService } from '../../../../services/inventory.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { UserService } from '../../../../services/user.service';
import { AuthService } from '../../../service/auth.service';

const ok = <T>(result: T) => of({ success: true, message: '', result } as any);

// A36-P2-12/13 — creating a service order by hand.
describe('ServiceOrderFormComponent', () => {
  let fixture: ComponentFixture<ServiceOrderFormComponent>;
  let component: ServiceOrderFormComponent;
  let service: jasmine.SpyObj<ServiceOrderService>;
  let inventory: jasmine.SpyObj<InventoryService>;

  beforeEach(async () => {
    service = jasmine.createSpyObj<ServiceOrderService>('ServiceOrderService', ['create']);
    inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getWarehouses', 'getProductById']);
    inventory.getProducts.and.returnValue(ok({ data: [
      { id: 1, uuid: 'p-svc', sku: 'SVC-AC', name: 'AC installation', productType: 'SERVICE' },
      { id: 2, uuid: 'p-stock', sku: 'PIPE', name: 'Copper pipe', productType: 'STOCK' }
    ], totalRecords: 2 }));
    inventory.getWarehouses.and.returnValue(ok([{ id: 1, uuid: 'w-1', code: 'MAIN', name: 'Main', isActive: true }]));
    inventory.getProductById.and.returnValue(ok({ id: 1, estimatedDurationHours: 2.5, variants: [] }));
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(ok({ data: [{ uuid: 'c-1', companyName: 'Cool Air Ltd' }] }));
    const users = jasmine.createSpyObj<UserService>('UserService', ['getUsers']);
    const auth = {
      hasPermission: () => false, getUserData: () => ({ userId: 7, firstName: 'Usman' }),
      getRoles: () => ok([{ id: 4, value: 'Warehouse Operator' }])
    };

    await TestBed.configureTestingModule({
      imports: [ServiceOrderFormComponent],
      providers: [
        provideRouter([]), provideNoopAnimations(), provideHttpClient(), provideHttpClientTesting(),
        { provide: ServiceOrderService, useValue: service },
        { provide: InventoryService, useValue: inventory },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: UserService, useValue: users },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ServiceOrderFormComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('asks for service products and only offers SERVICE ones', () => {
    expect(inventory.getProducts.calls.mostRecent().args[0]!.productType).toBe('SERVICE');
    expect(component.serviceProducts.map(p => p.uuid)).toEqual(['p-svc']);
    expect(component.warehouseUuid).withContext('the only warehouse is preselected').toBe('w-1');
    expect(component.roleOptions).toEqual([{ label: 'Warehouse Operator', value: 4 }]);
  });

  it('prefills estimated hours from the product duration × quantity until the person types their own', () => {
    component.quantity = 2;
    component.onProductPicked({ productUuid: 'p-svc', variantUuid: 'v-1' } as any);
    expect(component.estimatedHours).toBe(5);
    component.quantity = 3;
    component.onQuantityChange();
    expect(component.estimatedHours).toBe(7.5);
    component.onHoursEdited();
    component.estimatedHours = 4;
    component.quantity = 4;
    component.onQuantityChange();
    expect(component.estimatedHours).toBe(4);
  });

  it('assigns a person or a team, not both', () => {
    component.assignedRoleId = 4;
    component.assignedUserId = 7;
    component.onUserChange();
    expect(component.assignedRoleId).toBeNull();
    component.assignedRoleId = 4;
    component.onRoleChange();
    expect(component.assignedUserId).toBeNull();
  });

  it('needs product, customer, warehouse and a positive quantity', () => {
    expect(component.canSave).toBeFalse();
    component.onProductPicked({ productUuid: 'p-svc', variantUuid: 'v-1' } as any);
    component.customerUuid = 'c-1';
    expect(component.canSave).toBeTrue();
    component.quantity = 0;
    expect(component.canSave).toBeFalse();
  });

  it('creates with date and time as yyyy-MM-dd / HH:mm, then opens the new order', () => {
    const router = TestBed.inject(Router);
    spyOn(router, 'navigate');
    service.create.and.returnValue(ok('so-9'));
    component.onProductPicked({ productUuid: 'p-svc', variantUuid: 'v-1' } as any);
    component.customerUuid = 'c-1';
    component.scheduledDate = new Date(2026, 9, 12);
    component.scheduledTime = new Date(2026, 9, 12, 9, 5);
    component.assignedUserId = 7;
    component.notes = '  ground floor ';
    component.save();
    const body = service.create.calls.mostRecent().args[0];
    expect(body).toEqual(jasmine.objectContaining({
      serviceProductUuid: 'p-svc', serviceVariantUuid: 'v-1', customerUuid: 'c-1', quantity: 1, warehouseUuid: 'w-1',
      assignedUserId: 7, scheduledDate: '2026-10-12', scheduledTime: '09:05', estimatedHours: 2.5, priority: 1, notes: 'ground floor'
    }));
    expect(body.assignedRoleId).toBeUndefined();
    expect(router.navigate).toHaveBeenCalledWith(['/portal/pages/services/service-orders', 'so-9']);
  });

  it('keeps the form on a refusal', () => {
    service.create.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Customer is not a customer' } })));
    component.onProductPicked({ productUuid: 'p-svc', variantUuid: 'v-1' } as any);
    component.customerUuid = 'c-1';
    component.save();
    expect(component.isSaving).toBeFalse();
  });
});
