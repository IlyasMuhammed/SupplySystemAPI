import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CanActivateFn, Route, Router, Routes } from '@angular/router';
import routes from '../../pages.routes';
import { AppMenu } from '../../../layout/component/app.menu';
import { AuthService } from '../../service/auth.service';

import { SaleInquiryListComponent } from './sale-inquiry-list/sale-inquiry-list.component';
import { SaleInquiryFormComponent } from './sale-inquiry-form/sale-inquiry-form.component';
import { SaleInquiryDetailComponent } from './sale-inquiry-detail/sale-inquiry-detail.component';
import { RejectionReasonsComponent } from '../rejection-reasons/rejection-reasons.component';

/**
 * A32 — the inquiry screens and the rejection-reasons admin are routed, and guarded with exactly the codes the
 * server requires (contract §2: list/detail = SALE_INQUIRY_VIEW, new = SALE_INQUIRY_CREATE; admin =
 * SALE_REJECTION_REASON_MANAGE), and reachable from the menu.
 */
describe('A32 sale inquiry routes and menu', () => {
  const flat = routes as Routes;
  const find = (path: string) => flat.find(r => r.path === path);

  /** The codes a route's guard asks for, read by running it against a recording AuthService. */
  function guardCodes(route: Route | undefined): string[] {
    const asked: string[] = [];
    TestBed.resetTestingModule().configureTestingModule({
      providers: [
        { provide: AuthService, useValue: { isAuthenticated: () => true, hasAnyPermission: (...codes: string[]) => { asked.push(...codes); return true; } } },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } }
      ]
    });
    for (const guard of (route?.canActivate ?? []) as CanActivateFn[]) {
      TestBed.runInInjectionContext(() => guard({} as any, {} as any));
    }
    return asked;
  }

  it('routes list, new and detail, with "new" before ":uuid"', () => {
    expect(find('sales/inquiries')?.component).toBe(SaleInquiryListComponent);
    expect(find('sales/inquiries/new')?.component).toBe(SaleInquiryFormComponent);
    expect(find('sales/inquiries/:uuid')?.component).toBe(SaleInquiryDetailComponent);
    expect(flat.findIndex(r => r.path === 'sales/inquiries/new'))
      .toBeLessThan(flat.findIndex(r => r.path === 'sales/inquiries/:uuid'));
  });

  it('guards each inquiry route with the server\'s code', () => {
    expect(guardCodes(find('sales/inquiries'))).toEqual(['SALE_INQUIRY_VIEW']);
    expect(guardCodes(find('sales/inquiries/:uuid'))).toEqual(['SALE_INQUIRY_VIEW']);
    expect(guardCodes(find('sales/inquiries/new'))).toEqual(['SALE_INQUIRY_CREATE']);
  });

  it('routes the rejection-reasons admin behind the manage code', () => {
    expect(find('sales/rejection-reasons')?.component).toBe(RejectionReasonsComponent);
    expect(guardCodes(find('sales/rejection-reasons'))).toEqual(['SALE_REJECTION_REASON_MANAGE']);
  });

  // ── Menu ───────────────────────────────────────────────────────────────────

  function menuLabels(permissions: string[], features = ['MODULE_DEMAND']): string[] {
    const tenant = signal({ id: 'o', orgCode: 'O', orgName: 'O', plan: 'ENTERPRISE', enabledFeatureCodes: features, isSuperAdmin: false, roleName: 'X', permissions });
    const menu = new AppMenu(
      { hasAnyPermission: (...codes: string[]) => codes.some(c => permissions.includes(c)) } as any,
      { tenant, hasFeature: (c: string) => features.includes(c), isSuperAdmin: () => false } as any
    );
    const labels: string[] = [];
    const walk = (items: any[]) => items.forEach(i => { labels.push(i.label); if (i.items) walk(i.items); });
    walk(menu.model());
    return labels;
  }

  it('shows Inquiries under Sales to a SALE_INQUIRY_VIEW holder in a Demand org, and to no one else', () => {
    expect(menuLabels(['SALE_INQUIRY_VIEW'])).toContain('Inquiries');
    expect(menuLabels(['SALE_INQUIRY_CREATE'])).not.toContain('Inquiries');
    expect(menuLabels(['SALE_INQUIRY_VIEW'], [])).not.toContain('Inquiries');
  });

  it('shows Rejection Reasons only to the manage code holder', () => {
    expect(menuLabels(['SALE_REJECTION_REASON_MANAGE'])).toContain('Rejection Reasons');
    expect(menuLabels(['SALE_INQUIRY_VIEW'])).not.toContain('Rejection Reasons');
  });
});
