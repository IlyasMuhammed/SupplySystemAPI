import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, Router } from '@angular/router';
import { of, throwError } from 'rxjs';

import { ProductCreateComponent } from './product-create.component';
import { InventoryService } from '../../../../services/inventory.service';
import { SupplierService } from '../../../../services/supplier.service';
import { AttachmentService } from '../../../../services/attachment.service';

/** A36-P1-05 — the Service settings card on the product create page. */
describe('ProductCreateComponent — A36 service settings', () => {
  let fixture: ComponentFixture<ProductCreateComponent>;
  let component: ProductCreateComponent;
  let el: HTMLElement;
  let inventory: jasmine.SpyObj<InventoryService>;

  const ok = (result: unknown) => of({ success: true, message: '', result } as any);

  beforeEach(async () => {
    inventory = jasmine.createSpyObj<InventoryService>('InventoryService',
      ['getCategories', 'getWarehouses', 'createProduct', 'getProductById', 'patchProduct']);
    inventory.getCategories.and.returnValue(ok([]));
    inventory.getWarehouses.and.returnValue(ok([]));
    inventory.createProduct.and.returnValue(ok({ id: 1, sku: 'SVC-1' }));

    await TestBed.configureTestingModule({
      imports: [ProductCreateComponent],
      providers: [
        provideNoopAnimations(),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => null } } } },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
        { provide: InventoryService, useValue: inventory },
        { provide: SupplierService, useValue: { getSuppliers: () => ok({ data: [] }) } },
        { provide: AttachmentService, useValue: { resolveUrl: (u: string) => u } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ProductCreateComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    el = fixture.nativeElement;
  });

  function setType(type: string) {
    component.productForm.patchValue({ productType: type });
    component.onProductTypeChange();
    fixture.detectChanges();
  }

  function fillRequired() {
    component.productForm.patchValue({ name: 'Pump repair', purchasePrice: 0, sellingPrice: 50 });
  }

  it('shows the Service settings card only for SERVICE products', () => {
    expect(el.querySelector('[data-testid="service-settings-card"]')).toBeNull();
    setType('SERVICE');
    expect(el.querySelector('[data-testid="service-settings-card"]')).not.toBeNull();
    expect(el.textContent).toContain('Invoicing policy');
    setType('FINISHED_GOOD');
    expect(el.querySelector('[data-testid="service-settings-card"]')).toBeNull();
  });

  it('sends the service settings with a SERVICE product', () => {
    setType('SERVICE');
    fillRequired();
    component.productForm.patchValue({
      serviceInvoicingPolicy: 'FIXED_PRICE', serviceBillingModel: 'INCLUSIVE', estimatedDurationHours: 3,
      hasServiceBom: true, isSubcontractable: true
    });
    component.onSubmit();
    expect(inventory.createProduct.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({
      productType: 'SERVICE', serviceInvoicingPolicy: 'FIXED_PRICE', serviceBillingModel: 'INCLUSIVE',
      estimatedDurationHours: 3, hasServiceBom: true, isSubcontractable: true
    }));
  });

  it('clears the service settings when the type moves away from SERVICE', () => {
    setType('SERVICE');
    component.productForm.patchValue({
      serviceInvoicingPolicy: 'COST_PLUS', serviceBillingModel: 'PASS_THROUGH', estimatedDurationHours: 1,
      hasServiceBom: true, isSubcontractable: true
    });
    setType('STOCK_ITEM');
    expect(component.productForm.get('serviceInvoicingPolicy')!.value).toBeNull();
    expect(component.productForm.get('hasServiceBom')!.value).toBeFalse();
    fillRequired();
    component.onSubmit();
    expect(inventory.createProduct.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({
      serviceInvoicingPolicy: null, serviceBillingModel: null, estimatedDurationHours: null,
      hasServiceBom: false, isSubcontractable: false
    }));
  });

  it('refuses a non-positive duration, and shows the server message on refusal', () => {
    setType('SERVICE');
    fillRequired();
    component.productForm.patchValue({ estimatedDurationHours: 0 });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="svc-duration-error"]')).not.toBeNull();
    component.onSubmit();
    expect(inventory.createProduct).not.toHaveBeenCalled();

    component.productForm.patchValue({ estimatedDurationHours: 1, serviceInvoicingPolicy: 'TIME_AND_MATERIAL' });
    const message = 'Hourly rate (selling price of the default variant) is required for Time & Material services';
    inventory.createProduct.and.returnValue(throwError(() => ({ error: { message } })));
    const toast = spyOn((component as any).messageService, 'add');
    component.onSubmit();
    expect(toast).toHaveBeenCalledWith(jasmine.objectContaining({ detail: message }));
  });
});
