import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { PoDetailComponent } from './po-detail.component';
import { DemandService, PoDetailModel } from '../../../../services/demand.service';
import { WorkflowService } from '../../../../services/workflow.service';
import { SupplierService } from '../../../../services/supplier.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AttachmentPolicyService } from '../../../../services/attachment-policy.service';
import { TimelineService } from '../../../../services/timeline.service';
import { TenantService } from '../../../service/tenant.service';
import { AuthService } from '../../../service/auth.service';
import { provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';
import { environment } from '../../../../../environments/environment';

// A50 D-26 — "Email to supplier" on a PO: any of PO_CREATE / PO_EDIT / PO_APPROVE, on an approved or later PO.

function po(overrides: Partial<PoDetailModel> = {}): PoDetailModel {
  return {
    uuid: 'po-50', poNumber: 'PO-2026-0500', supplierId: 's-1', supplierName: 'Gulf Metals', status: 'APPROVED',
    totalAmount: 1000, createdBy: 1, createdDate: '2026-11-01T00:00:00', lines: [], linkedPrUuids: [], ...overrides
  } as PoDetailModel;
}

describe('PoDetailComponent — A50 email to supplier', () => {
  let fixture: ComponentFixture<PoDetailComponent>;
  let http: HttpTestingController;
  let toasts: jasmine.Spy;
  let perms: string[];

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  async function setup(model: PoDetailModel, permissions: string[]) {
    perms = permissions;
    const demand = jasmine.createSpyObj<DemandService>('DemandService', ['getPoById']);
    demand.getPoById.and.returnValue(of({ success: true, message: '', result: model } as any));
    const timeline = jasmine.createSpyObj<TimelineService>('TimelineService', ['getByTraceId', 'getByDocument']);
    timeline.getByDocument.and.returnValue(of({ success: true, result: null } as any));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl']);
    attachments.getAttachments.and.returnValue(of({ success: true, result: [] } as any));
    attachments.resolveUrl.and.callFake((u: string) => u);
    const policy = jasmine.createSpyObj<AttachmentPolicyService>('AttachmentPolicyService', ['ruleFor']);
    policy.ruleFor.and.returnValue(of(null));

    await TestBed.configureTestingModule({
      imports: [PoDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), provideTestOrgCurrencies(),
        { provide: DemandService, useValue: demand },
        { provide: WorkflowService, useValue: jasmine.createSpyObj('WorkflowService', ['getHistory', 'getApprovalDetail']) },
        { provide: SupplierService, useValue: jasmine.createSpyObj('SupplierService', ['getEligibleContacts']) },
        { provide: AttachmentService, useValue: attachments },
        { provide: AttachmentPolicyService, useValue: policy },
        { provide: TimelineService, useValue: timeline },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }), hasFeature: () => false } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c), hasAnyPermission: (...c: string[]) => c.some(x => perms.includes(x)) } },
        { provide: ActivatedRoute, useValue: { params: of({ uuid: 'po-50' }), snapshot: { paramMap: new Map([['uuid', 'po-50']]), queryParams: {} } } }
      ]
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(PoDetailComponent);
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  it('is offered on an approved PO to PO_APPROVE, and posts to the PO email route', async () => {
    await setup(po(), ['PO_APPROVE']);
    expect(q('action-email')).not.toBeNull();
    fixture.componentInstance.emailVisible = true;
    fixture.detectChanges();
    const cc = document.querySelector('[data-testid="email-cc"]') as HTMLInputElement;
    cc.value = 'me@us.com; boss@us.com'; cc.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (document.querySelector('[data-testid="email-send"] button') as HTMLButtonElement).click();
    const req = http.expectOne(`${environment.apiUrl}/purchase-orders/po-50/email`);
    expect(req.request.body).toEqual({ cc: 'me@us.com; boss@us.com' });
    req.flush({ success: true, message: '', result: { sent: false, sentTo: ['s@gulf.com'], cc: ['me@us.com'], failed: [{ email: 'boss@us.com', error: 'Mailbox full' }] } });
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'warn', summary: 'Partly sent' }));
  });

  it('is not offered on a draft, or with PO_VIEW only', async () => {
    await setup(po({ status: 'DRAFT' }), ['PO_EDIT']);
    expect(q('action-email')).toBeNull();
    TestBed.resetTestingModule();
    await setup(po(), ['PO_VIEW']);
    expect(q('action-email')).toBeNull();
  });
});
