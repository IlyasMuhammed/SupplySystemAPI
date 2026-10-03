import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SaleInquiryFormComponent } from './sale-inquiry-form.component';
import { SalesPreorderService, CreateSaleInquiryRequest } from '../../../../services/sales-preorder.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { UserService } from '../../../../services/user.service';
import { AuthService } from '../../../service/auth.service';

describe('SaleInquiryFormComponent (new inquiry)', () => {
  let fixture: ComponentFixture<SaleInquiryFormComponent>;
  let component: SaleInquiryFormComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;
  let users: jasmine.SpyObj<UserService>;
  let navigate: jasmine.Spy;
  let permissions: string[];

  const auth = {
    hasPermission: (code: string) => permissions.includes(code),
    getUserData: () => ({ userId: 7, firstName: 'Ayesha', lastName: 'Khan' })
  } as unknown as AuthService;

  async function setup() {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['createInquiry']);
    service.createInquiry.and.returnValue(of({ success: true, message: '', result: 'inq-new' } as any));
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(of({ success: true, message: '', result: { data: [], totalRecords: 0 } } as any));
    users = jasmine.createSpyObj<UserService>('UserService', ['getUsers']);
    users.getUsers.and.returnValue(of({ success: true, message: '', result: { items: [
      { userID: 7, firstName: 'Ayesha', lastName: 'Khan', isActive: true },
      { userID: 9, firstName: 'John', lastName: 'Smith', isActive: true }
    ] } } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleInquiryFormComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesPreorderService, useValue: service },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: UserService, useValue: users },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleInquiryFormComponent);
    component = fixture.componentInstance;
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture.detectChanges();
  }

  beforeEach(() => { permissions = ['SALE_INQUIRY_CREATE']; });

  it('starts received today and cannot be saved without a customer', async () => {
    await setup();
    const today = new Date();
    expect(component.draft.receivedDate?.toDateString()).toBe(today.toDateString());
    expect(component.problem).toContain('customer');

    component.save();
    expect(service.createInquiry).not.toHaveBeenCalled();
  });

  it('creates the inquiry with the days picked and opens it on its lines', async () => {
    await setup();
    component.draft.customer = { uuid: 'p-1', companyName: 'GlobalTech Co' } as any;
    component.draft.customerReference = '  GT-RFQ-2026-118 ';
    component.draft.customerReferenceDate = new Date(2026, 8, 30);
    component.draft.receivedDate = new Date(2026, 9, 1);
    component.draft.responseDeadline = new Date(2026, 9, 10);
    component.draft.assignedToUserId = 9;

    component.save();

    const sent = service.createInquiry.calls.mostRecent().args[0] as CreateSaleInquiryRequest;
    expect(sent).toEqual(jasmine.objectContaining({
      partnerId: 'p-1', customerReference: 'GT-RFQ-2026-118', customerReferenceDate: '2026-09-30',
      receivedDate: '2026-10-01', responseDeadline: '2026-10-10', assignedToUserId: 9, notes: null
    }));
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/sales/inquiries', 'inq-new'], { queryParams: { tab: 'lines' } });
  });

  it('refuses a deadline before the day it was received', async () => {
    await setup();
    component.draft.customer = { uuid: 'p-1', companyName: 'GlobalTech Co' } as any;
    component.draft.receivedDate = new Date(2026, 9, 10);
    component.draft.responseDeadline = new Date(2026, 9, 1);
    expect(component.problem).toContain('deadline');
  });

  it('shows the server\'s refusal and stays on the page', async () => {
    await setup();
    service.createInquiry.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Partner is not an active customer.' } })));
    component.draft.customer = { uuid: 'p-1', companyName: 'GlobalTech Co' } as any;

    component.save();

    expect(component.saveError).toBe('Partner is not an active customer.');
    expect(navigate).not.toHaveBeenCalled();
    expect(component.isSaving).toBeFalse();
  });

  it('offers the user themself as assignee, and everyone only to a user manager', async () => {
    await setup();
    expect(users.getUsers).not.toHaveBeenCalled();
    expect(component.assigneeOptions.map(o => o.value)).toEqual([7]);

    permissions = ['SALE_INQUIRY_CREATE', 'USER_MANAGE'];
    await setup();
    expect(users.getUsers).toHaveBeenCalled();
    expect(component.assigneeOptions.map(o => o.value)).toEqual([7, 9]);
  });
});
