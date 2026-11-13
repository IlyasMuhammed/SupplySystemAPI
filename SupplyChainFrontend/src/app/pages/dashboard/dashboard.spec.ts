import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError, Subject } from 'rxjs';
import { Dashboard } from './dashboard';
import { DashboardSummary, DashboardSummaryService } from '../../services/dashboard-summary.service';
import { DemandService } from '../../services/demand.service';
import { WarehouseService } from '../../services/warehouse.service';
import { InventoryService } from '../../services/inventory.service';
import { SaleOrderService } from '../../services/sale-order.service';
import { AuthService } from '../service/auth.service';
import { LayoutService } from '../../layout/service/layout.service';
import { signal } from '@angular/core';
import { OnboardingSetupService } from '../../services/onboarding-setup.service';
import { TenantService } from '../service/tenant.service';
import { OnboardingDemoService } from '../../services/onboarding-demo.service';

describe('Dashboard', () => {
  let fixture: ComponentFixture<Dashboard>;
  let component: Dashboard;
  let summarySvc: jasmine.SpyObj<DashboardSummaryService>;
  let demand: jasmine.SpyObj<DemandService>;
  let saleOrders: jasmine.SpyObj<SaleOrderService>;
  let permissions: string[];
  let checklistSvc: jasmine.SpyObj<OnboardingDemoService>;

  const page = { result: { data: [], totalRecords: 0 } } as any;

  function full(): DashboardSummary {
    return {
      generatedAt: '2026-10-07T10:00:00Z',
      procurement: {
        purchaseOrders: [{ status: 'SENT', count: 3 }, { status: 'APPROVED', count: 2 }, { status: 'CLOSED', count: 5 }],
        requisitions: [{ status: 'SUBMITTED', count: 1 }],
        activeSuppliers: 4, totalSuppliers: 6
      },
      sales: {
        inquiries: [{ status: 'RECEIVED', count: 2 }],
        quotations: [{ status: 'SENT', count: 3 }, { status: 'ACCEPTED', count: 1 }],
        saleOrders: [{ status: 'CONFIRMED', count: 4 }, { status: 'PARTIALLY_FULFILLED', count: 1 }, { status: 'DRAFT', count: 2 }],
        lateSaleOrders: 2, ordersThisMonth: 7, activeCustomers: 9
      },
      deliveries: {
        outbound: [{ status: 'PICKING', count: 2 }, { status: 'IN_TRANSIT', count: 1 }, { status: 'DELIVERED', count: 8 }],
        inboundExpected: 1, late: 1, shippedThisMonth: 5
      },
      production: {
        orders: [{ status: 'IN_PROGRESS', count: 2 }, { status: 'COMPLETED', count: 3 }],
        late: 1, openMakeToOrder: 1, boms: [{ status: 'ACTIVE', count: 4 }]
      },
      receivables: {
        invoices: [{ status: 'ISSUED', count: 3 }],
        outstanding: [{ currency: 'PKR', amount: 1_250_000, count: 2 }, { currency: 'USD', amount: 900, count: 1 }],
        overdue: [{ currency: 'PKR', amount: 250_000, count: 1 }]
      },
      payables: null,
      inventory: { activeProducts: 40, manufacturedProducts: 3 },
      attention: [
        { key: 'SO_DRAFT', count: 2 },
        { key: 'SO_LATE', count: 2 },
        { key: 'SOMETHING_NEW', count: 1 }
      ]
    };
  }

  async function setup(summary: DashboardSummary | 'error', perms: string[] = []) {
    permissions = perms;
    summarySvc = jasmine.createSpyObj<DashboardSummaryService>('DashboardSummaryService', ['getSummary']);
    summarySvc.getSummary.and.returnValue(summary === 'error' ? throwError(() => ({ status: 500 })) : of({ success: true, result: summary } as any));

    demand     = jasmine.createSpyObj<DemandService>('DemandService', ['getPos']);
    demand.getPos.and.returnValue(of(page));
    saleOrders = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', ['getSaleOrders']);
    saleOrders.getSaleOrders.and.returnValue(of(page));
    const warehouse = jasmine.createSpyObj<WarehouseService>('WarehouseService', ['getGrns']);
    warehouse.getGrns.and.returnValue(of(page));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getReorderAlerts']);
    inventory.getReorderAlerts.and.returnValue(of({ result: [] } as any));
    const auth = jasmine.createSpyObj<AuthService>('AuthService', ['hasPermission', 'hasAnyPermission']);
    auth.hasPermission.and.callFake((c: string) => permissions.includes(c));
    auth.hasAnyPermission.and.callFake((...c: string[]) => c.some(x => permissions.includes(x)));
    checklistSvc = jasmine.createSpyObj<OnboardingDemoService>('OnboardingDemoService', ['getChecklist', 'completeStep', 'dismissChecklist']);
    checklistSvc.getChecklist.and.returnValue(of({ success: true, message: '', result: {
      dismissed: false, completedCount: 1, totalCount: 2, isComplete: false,
      steps: [
        { stepCode: 'COMPLETE_SETUP', title: 'Complete the setup wizard', link: '/portal/pages/onboarding/setup', sortOrder: 10, completed: true },
        { stepCode: 'ADD_CUSTOMER', title: 'Add your first customer', link: '/portal/pages/customers', sortOrder: 20, completed: false }
      ]
    } } as any));

    await TestBed.configureTestingModule({
      imports: [Dashboard],
      providers: [
        provideRouter([]),
        provideNoopAnimations(),
        { provide: DashboardSummaryService, useValue: summarySvc },
        { provide: DemandService, useValue: demand },
        { provide: WarehouseService, useValue: warehouse },
        { provide: InventoryService, useValue: inventory },
        { provide: SaleOrderService, useValue: saleOrders },
        { provide: AuthService, useValue: auth },
        { provide: LayoutService, useValue: { configUpdate$: new Subject<void>() } },
        // A50 — the "Setup incomplete" banner reads the setup status (none here: hidden).
        { provide: OnboardingSetupService, useValue: { status: signal(null), loadStatus: () => of(null) } },
        { provide: TenantService, useValue: { isSuperAdmin: () => false, tenant: signal(null), hasFeature: () => true } },
        // A50 — the getting-started checklist (FE-B50); shown only with ONBOARDING_VIEW.
        { provide: OnboardingDemoService, useValue: checklistSvc }
      ]
    }).compileComponents();

    fixture   = TestBed.createComponent(Dashboard);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  const q = (testId: string) => fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);

  it('loads every count in one summary call', async () => {
    await setup(full(), ['SALE_ORDER_VIEW', 'PO_VIEW']);
    expect(summarySvc.getSummary).toHaveBeenCalledTimes(1);
    expect(demand.getPos).toHaveBeenCalledOnceWith({ pageSize: 5 });
    expect(saleOrders.getSaleOrders).toHaveBeenCalledOnceWith({ pageSize: 5 });
  });

  it('does not fetch recent lists the user cannot open', async () => {
    await setup(full(), []);
    expect(demand.getPos).not.toHaveBeenCalled();
    expect(saleOrders.getSaleOrders).not.toHaveBeenCalled();
  });

  it('shows a hero card for each area the server returned, and none for the rest', async () => {
    await setup(full(), ['SALE_ORDER_VIEW']);

    const value = (key: string) => component.heroCards.find(c => c.key === key)?.value;

    expect(value('sales')).toBe('5');        // CONFIRMED 4 + PARTIALLY_FULFILLED 1
    expect(q('hero-sales').textContent).toContain('7 this month · 2 late');
    expect(value('deliveries')).toBe('2');   // picking only: in transit and delivered have left
    expect(value('production')).toBe('2');   // in progress; completed is not active
    expect(value('purchasing')).toBe('5');   // APPROVED 2 + SENT 3
    expect(q('hero-payables')).toBeNull('payables came back null: the user cannot see supplier invoices');
  });

  it('headlines receivables in the largest currency, compactly, and never sums currencies', async () => {
    await setup(full());

    expect(q('hero-receivables').textContent).toContain('PKR 1.25M');
    const panel = q('receivables-panel').textContent as string;
    expect(panel).toContain('PKR');
    expect(panel).toContain('USD');
    expect(panel).toContain('1,250,000');
    expect(panel).toContain('900');
  });

  it('lists attention items in priority order, late before drafts, and keeps unknown keys', async () => {
    await setup(full());

    expect(component.attention.map(a => a.key)).toEqual(['SO_LATE', 'SO_DRAFT', 'SOMETHING_NEW']);
    expect(component.attentionTotal).toBe(5);
    expect(q('attention-SO_LATE').classList).toContain('att-danger');
    expect(q('attention-SOMETHING_NEW').textContent).toContain('Something New');
  });

  it('builds the sales funnel from inquiries to fulfilled', async () => {
    await setup(full());

    expect(component.salesFunnel.map(f => [f.label, f.count])).toEqual([
      ['Inquiries open', 2], ['Quotations sent', 3], ['Quotations accepted', 1],
      ['Orders in draft', 2], ['Orders confirmed', 4], ['Partly fulfilled', 1], ['Fulfilled / invoiced', 0]
    ]);
  });

  it('hides the whole sales section when the server sent none', async () => {
    const s = full();
    s.sales = null;
    await setup(s);

    expect(q('sales-pipeline')).toBeNull();
    expect(q('hero-sales')).toBeNull();
  });

  it('says so when the summary cannot be loaded, instead of showing zeros', async () => {
    await setup('error');

    expect(q('dashboard-error')).not.toBeNull();
    expect(component.heroCards.length).toBe(0);
  });

  // A50 tasks 313–317 (FE-B50)
  it('shows the getting-started checklist under the setup banner for ONBOARDING_VIEW', async () => {
    await setup(full(), ['ONBOARDING_VIEW']);
    expect(checklistSvc.getChecklist).toHaveBeenCalledTimes(1);
    expect(q('checklist-progress').textContent).toContain('1 of 2 done');
    const body = fixture.nativeElement.querySelector('.sf-body') as HTMLElement;
    const tags = Array.from(body.children).map(c => c.tagName.toLowerCase());
    expect(tags.indexOf('app-onboarding-checklist-widget')).toBe(tags.indexOf('app-setup-incomplete-banner') + 1);
  });

  it('does not ask for the checklist without ONBOARDING_VIEW', async () => {
    await setup(full(), []);
    expect(checklistSvc.getChecklist).not.toHaveBeenCalled();
    expect(q('onboarding-checklist')).toBeNull();
  });
});
