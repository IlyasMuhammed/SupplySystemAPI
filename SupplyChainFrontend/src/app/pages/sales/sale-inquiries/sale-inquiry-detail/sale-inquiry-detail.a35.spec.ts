import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { SaleInquiryDetailComponent } from './sale-inquiry-detail.component';
import { SalesPreorderService, SaleInquiry } from '../../../../services/sales-preorder.service';
import { UserService } from '../../../../services/user.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { InventoryService } from '../../../../services/inventory.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AttachmentPolicyService } from '../../../../services/attachment-policy.service';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { TEST_AED, TEST_PKR, TEST_USD, provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';

// A35 D-1 — the inquiry's "Create quotation" dialog offers the organization's active currencies, plus the inquiry's own
// (the server's default for the quotation) even when it has been deactivated since.

const INQUIRY = {
  uuid: 'inq-1', traceId: 't', inquiryNumber: 'INQ-2026-00042', partnerId: 'p-1', partnerName: 'Al Rashid', status: 'REVIEW_COMPLETE',
  receivedDate: '2026-10-01T00:00:00', createdBy: 7, createdDate: '2026-10-01T08:00:00', allowedNextStatuses: ['DECLINED'],
  isEditable: true, lines: [], quotations: [], currencyId: 'cur-usd', currencyCode: 'USD'
} as unknown as SaleInquiry;

const CATALOG = [
  { id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR' }, { id: 'cur-aed', name: 'UAE Dirham', code: 'AED' },
  { id: 'cur-usd', name: 'US Dollar', code: 'USD' }, { id: 'cur-eur', name: 'Euro', code: 'EUR' }
];

describe('SaleInquiryDetailComponent — A35 quotation currency options', () => {
  async function open(orgList: boolean) {
    const service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['getInquiry', 'getRejectionReasons']);
    service.getInquiry.and.returnValue(of({ success: true, message: '', result: INQUIRY } as any));
    service.getRejectionReasons.and.returnValue(of({ success: true, message: '', result: [] } as any));
    const users = jasmine.createSpyObj<UserService>('UserService', ['getUsers']);
    users.getUsers.and.returnValue(of({ success: true, message: '', result: { items: [] } } as any));
    const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(of({ success: true, message: '', result: CATALOG } as any));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts']);
    inventory.getProducts.and.returnValue(of({ success: true, message: '', result: { data: [], totalRecords: 0 } } as any));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl']);
    attachments.getAttachments.and.returnValue(of({ success: true, message: '', result: [] } as any));
    const policy = jasmine.createSpyObj<AttachmentPolicyService>('AttachmentPolicyService', ['ruleFor']);
    policy.ruleFor.and.returnValue(of(null));

    await TestBed.configureTestingModule({
      imports: [SaleInquiryDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        // USD — the inquiry's currency — was deactivated; EUR was never set up for the org.
        ...(orgList ? [provideTestOrgCurrencies([TEST_PKR, TEST_AED, { ...TEST_USD, isActive: false }])] : []),
        { provide: SalesPreorderService, useValue: service },
        { provide: UserService, useValue: users },
        { provide: CurrenciesService, useValue: currencies },
        { provide: InventoryService, useValue: inventory },
        { provide: AttachmentService, useValue: attachments },
        { provide: AttachmentPolicyService, useValue: policy },
        { provide: AuthService, useValue: { hasPermission: () => true, hasAnyPermission: () => true, getUserData: () => ({ userId: 7 }) } },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ uuid: 'inq-1' }), queryParamMap: convertToParamMap({}) } } }
      ]
    }).compileComponents();
    const fixture = TestBed.createComponent(SaleInquiryDetailComponent);
    fixture.detectChanges();
    fixture.componentInstance.openQuotationDialog();
    return fixture.componentInstance;
  }

  it('offers the active org currencies and keeps the inquiry\'s own, deactivated since', async () => {
    const component = await open(true);
    expect(component.currencyOptions.map(o => o.value)).toEqual(['cur-pkr', 'cur-aed', 'cur-usd']);
  });

  it('offers the whole catalogue while the org list is not known', async () => {
    const component = await open(false);
    expect(component.currencyOptions.map(o => o.value)).toEqual(['cur-pkr', 'cur-aed', 'cur-usd', 'cur-eur']);
  });
});
