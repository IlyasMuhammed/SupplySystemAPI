import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { SupplierPaymentDetailComponent } from './supplier-payment-detail.component';
import { FinanceService, SupplierPaymentDetailModel } from '../../../../services/finance.service';
import { AuthService } from '../../../service/auth.service';

function payment(overrides: Partial<SupplierPaymentDetailModel> = {}): SupplierPaymentDetailModel {
  return {
    uuid: 'pay-1', paymentNumber: 'SPAY-2026-00001', supplierId: 'sup-1', supplierName: 'Karachi Steel',
    paymentDate: '2026-10-02T00:00:00', paymentMethod: 'CHEQUE', totalAmount: 1170, chequeNo: '000123', chequeDate: '2026-10-05T00:00:00',
    status: 'DRAFT', createdBy: 1, createdDate: '2026-10-02T09:00:00', paymentType: 'STANDARD',
    lines: [{ uuid: 'l-1', invoiceUuid: 'inv-1', invoiceNumber: 'INV-2026-00042', allocatedAmount: 1170, outstandingBeforeAllocation: 1170 }],
    ...overrides
  };
}

/**
 * Server: approve needs PAYMENT_APPROVE; post, bounce and cancel need PAYMENT_PROCESS; reading needs PAYMENT_VIEW.
 * Each button shows only in the status the action applies to, and only to whom the server would let do it.
 */
describe('SupplierPaymentDetailComponent', () => {
  let fixture: ComponentFixture<SupplierPaymentDetailComponent>;
  let permissions: string[];

  const auth = { hasPermission: (c: string) => permissions.includes(c) } as unknown as AuthService;

  async function setup(p: SupplierPaymentDetailModel) {
    const finance = jasmine.createSpyObj<FinanceService>('FinanceService',
      ['getSupplierPaymentById', 'approveSupplierPayment', 'cancelSupplierPayment', 'postSupplierPayment', 'bounceSupplierPayment']);
    finance.getSupplierPaymentById.and.returnValue(of({ success: true, message: '', result: p }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SupplierPaymentDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        { provide: FinanceService, useValue: finance },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ uuid: p.uuid }) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SupplierPaymentDetailComponent);
    fixture.detectChanges();
  }

  function actions(): string[] {
    return Array.from(fixture.nativeElement.querySelectorAll('p-button[label]') as NodeListOf<HTMLElement>)
      .map(b => b.getAttribute('label') ?? '')
      .filter(l => ['Approve', 'Post', 'Bounce', 'Cancel'].includes(l));
  }

  const ALL = ['PAYMENT_VIEW', 'PAYMENT_PROCESS', 'PAYMENT_APPROVE'];
  const OFFERED: Record<string, string[]> = {
    DRAFT:     ['Approve', 'Cancel'],
    APPROVED:  ['Post', 'Cancel'],
    POSTED:    ['Bounce'],          // a cheque
    CANCELLED: [],
    BOUNCED:   []
  };

  for (const status of Object.keys(OFFERED)) {
    it(`${status}: someone holding every payment permission is offered ${OFFERED[status].join(', ') || 'nothing'}`, async () => {
      permissions = ALL;
      await setup(payment({ status }));

      expect(actions()).toEqual(jasmine.arrayWithExactContents(OFFERED[status]));
    });

    it(`${status}: a PAYMENT_VIEW-only reader is offered nothing`, async () => {
      permissions = ['PAYMENT_VIEW'];
      await setup(payment({ status }));

      expect(actions()).toEqual([]);
      expect(fixture.nativeElement.textContent).toContain('SPAY-2026-00001');
    });
  }

  it('Post, Bounce and Cancel need PAYMENT_PROCESS; Approve needs PAYMENT_APPROVE', async () => {
    permissions = ['PAYMENT_VIEW', 'PAYMENT_APPROVE'];
    await setup(payment({ status: 'DRAFT' }));
    expect(actions()).toEqual(['Approve']);

    permissions = ['PAYMENT_VIEW', 'PAYMENT_PROCESS'];
    await setup(payment({ status: 'DRAFT' }));
    expect(actions()).toEqual(['Cancel']);

    await setup(payment({ status: 'APPROVED' }));
    expect(actions()).toEqual(jasmine.arrayWithExactContents(['Post', 'Cancel']));
  });

  it('offers Bounce only on a posted cheque', async () => {
    permissions = ALL;
    await setup(payment({ status: 'POSTED', paymentMethod: 'BANK_TRANSFER', chequeNo: undefined, chequeDate: undefined }));

    expect(actions()).toEqual([]);
  });
});
