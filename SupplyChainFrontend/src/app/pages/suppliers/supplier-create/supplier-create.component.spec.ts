import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { SupplierCreateComponent } from './supplier-create.component';
import { SupplierService } from '../../../services/supplier.service';

describe('SupplierCreateComponent', () => {
  let component: SupplierCreateComponent;
  let fixture: ComponentFixture<SupplierCreateComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // The component is standalone, so it belongs in imports. It was in `declarations`, which is
      // for components owned by an NgModule — that is what this spec was actually failing on, not
      // anything about the component itself.
      imports: [SupplierCreateComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(),
        provideRouter([]), provideNoopAnimations(),
        {
          provide: SupplierService,
          useValue: {
            createSupplier: jasmine.createSpy('createSupplier').and.returnValue(of({}))
          }
        }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SupplierCreateComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
