import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SupplierCreateComponent } from './supplier-create.component';

import { ReactiveFormsModule } from '@angular/forms';
import { HttpClientTestingModule } from '@angular/common/http/testing';
import { RouterTestingModule } from '@angular/router/testing';

import { SupplierService } from '../../../services/supplier.service';

import { of } from 'rxjs';

describe('SupplierCreateComponent', () => {
  let component: SupplierCreateComponent;
  let fixture: ComponentFixture<SupplierCreateComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [SupplierCreateComponent],
      imports: [
        ReactiveFormsModule,
        HttpClientTestingModule,
        RouterTestingModule
      ],
      providers: [
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
