import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { SupplierPaymentListComponent } from './supplier-payment-list.component';
import { FinanceService } from '../../../../services/finance.service';
import { AuthService } from '../../../service/auth.service';

describe('SupplierPaymentListComponent', () => {
  let fixture: ComponentFixture<SupplierPaymentListComponent>;
  let permissions: string[];

  const auth = { hasPermission: (c: string) => permissions.includes(c) } as unknown as AuthService;

  async function setup() {
    const finance = jasmine.createSpyObj<FinanceService>('FinanceService', ['getSupplierPayments']);
    finance.getSupplierPayments.and.returnValue(of({ success: true, message: '', result: { data: [], totalRecords: 0, page: 1, pageSize: 20, totalPages: 0 } }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SupplierPaymentListComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        { provide: FinanceService, useValue: finance },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SupplierPaymentListComponent);
    fixture.detectChanges();
  }

  function recordPayment(): HTMLElement | null {
    return fixture.nativeElement.querySelector('p-button[label="Record Payment"]');
  }

  it('offers Record Payment to a PAYMENT_PROCESS holder (the create route and POST need it)', async () => {
    permissions = ['PAYMENT_VIEW', 'PAYMENT_PROCESS'];
    await setup();

    expect(recordPayment()).not.toBeNull();
  });

  it('does not offer Record Payment to a PAYMENT_VIEW-only reader, whom the create route would turn away', async () => {
    permissions = ['PAYMENT_VIEW'];
    await setup();

    expect(recordPayment()).toBeNull();
  });
});
