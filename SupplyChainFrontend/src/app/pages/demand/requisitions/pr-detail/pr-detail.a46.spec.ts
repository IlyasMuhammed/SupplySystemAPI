import { signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { PrDetailComponent } from './pr-detail.component';
import { DemandService } from '../../../../services/demand.service';
import { SupplierService } from '../../../../services/supplier.service';
import { MaterialService } from '../../../../services/material.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AttachmentPolicyService } from '../../../../services/attachment-policy.service';
import { TimelineService } from '../../../../services/timeline.service';
import { TenantService } from '../../../service/tenant.service';

// A46 C5 / BR-30 - PR -> PO conversion hands the pricing summary to wherever it lands.

describe('PrDetailComponent - A46 pricing summary', () => {
  let fixture: ComponentFixture<PrDetailComponent>;
  let component: PrDetailComponent;
  let demand: jasmine.SpyObj<DemandService>;
  let nav: jasmine.Spy;

  beforeEach(async () => {
    demand = jasmine.createSpyObj<DemandService>('DemandService', ['getPrById', 'convertPrToSingleVendorPo', 'convertPrToSplitPos']);
    demand.getPrById.and.returnValue(of({ success: false, message: '', result: null } as any));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl']);
    attachments.getAttachments.and.returnValue(of({ success: true, result: [] } as any));
    const policy = jasmine.createSpyObj<AttachmentPolicyService>('AttachmentPolicyService', ['ruleFor']);
    policy.ruleFor.and.returnValue(of(null));
    const timeline = jasmine.createSpyObj<TimelineService>('TimelineService', ['getByTraceId', 'getByDocument']);
    timeline.getByDocument.and.returnValue(of({ success: true, result: null } as any));
    await TestBed.configureTestingModule({
      imports: [PrDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        { provide: DemandService, useValue: demand },
        { provide: SupplierService, useValue: jasmine.createSpyObj('SupplierService', ['getSuppliers']) },
        { provide: MaterialService, useValue: jasmine.createSpyObj('MaterialService', ['getPrLineDisbursements']) },
        { provide: AttachmentService, useValue: attachments },
        { provide: AttachmentPolicyService, useValue: policy },
        { provide: TimelineService, useValue: timeline },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } },
        { provide: ActivatedRoute, useValue: { params: of({ uuid: 'pr-1' }), snapshot: { paramMap: new Map([['uuid', 'pr-1']]) } } }
      ]
    }).compileComponents();
    nav = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(PrDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('single-vendor conversion forwards the summary to the PO page as router state', fakeAsync(() => {
    const summary = { totalLines: 8, pricedCount: 5, unpricedCount: 3 };
    demand.convertPrToSingleVendorPo.and.returnValue(of({ success: true, message: '', result: 'po-9', pricingSummary: summary } as any));
    component.convertSupplierName = 'Acme';
    component.confirmConvert();
    tick(1500);
    const [cmds, extras] = nav.calls.mostRecent().args as any[];
    expect(cmds).toEqual(['/portal/pages/demand/purchase-orders', 'po-9']);
    expect(extras.state).toEqual({ pricingSummary: summary });
  }));

  it('single-vendor conversion from an older server (no summary) navigates with no state', fakeAsync(() => {
    demand.convertPrToSingleVendorPo.and.returnValue(of({ success: true, message: '', result: 'po-9' } as any));
    component.convertSupplierName = 'Acme';
    component.confirmConvert();
    tick(1500);
    expect((nav.calls.mostRecent().args as any[])[1].state).toBeUndefined();
  }));

  it('split conversion keeps the summary here and View Unpriced Lines opens the first PO highlighted', () => {
    const summary = { totalLines: 4, pricedCount: 1, unpricedCount: 3, warnings: ['x'] };
    demand.convertPrToSplitPos.and.returnValue(of({ success: true, message: '', result: ['po-1', 'po-2'], pricingSummary: summary } as any));
    component.splitAssignments = [{ prLineUuid: 'l1', itemDescription: 'A', supplierId: 's-1', supplierName: 'Acme' } as any];
    component.confirmSplit();
    expect(component.splitPricingSummary).toEqual(summary);
    component.viewUnpricedPos();
    const [cmds, extras] = nav.calls.mostRecent().args as any[];
    expect(cmds).toEqual(['/portal/pages/demand/purchase-orders', 'po-1']);
    expect(extras.queryParams).toEqual({ unpriced: 1 });
    expect(extras.state).toEqual({ pricingSummary: summary });
  });
});
