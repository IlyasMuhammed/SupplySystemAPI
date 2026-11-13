import { TestBed } from '@angular/core/testing';
import { Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';

import { TenantService } from '../service/tenant.service';
import { isPlatformPath, platformScopeGuard } from './platform-scope.guard';

describe('platformScopeGuard', () => {
  function run(url: string, isSuperAdmin: boolean, loaded = true): boolean | UrlTree | Observable<boolean | UrlTree> {
    const tenant = {
      tenant: () => (loaded ? { isSuperAdmin } : null),
      isSuperAdmin: () => isSuperAdmin,
      loadCurrent: () => of({})
    };
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: TenantService, useValue: tenant }] });
    return TestBed.runInInjectionContext(() =>
      platformScopeGuard({} as any, { url } as RouterStateSnapshot) as boolean | UrlTree | Observable<boolean | UrlTree>);
  }

  it('lets an organization user open any page', () => {
    expect(run('/portal/pages/sales/orders', false)).toBeTrue();
  });

  it('sends the super admin from an organization page to the organizations list', () => {
    const result = run('/portal/pages/finance/invoices', true) as UrlTree;
    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/portal/pages/organizations');
  });

  it('sends the super admin from the dashboard to the organizations list', () => {
    const result = run('/portal/dashboard', true) as UrlTree;
    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/portal/pages/organizations');
  });

  it('lets the super admin open tenant management, platform settings and the profile', () => {
    for (const url of ['/portal/pages/organizations', '/portal/pages/organizations/abc/features?tab=history',
                       '/portal/pages/system/quickbooks-app', '/portal/pages/profile']) {
      expect(run(url, true)).toBeTrue();
      TestBed.resetTestingModule();
    }
  });

  it('waits for the tenant profile before judging the first navigation', done => {
    (run('/portal/pages/finance/invoices', true, false) as Observable<boolean | UrlTree>).subscribe(r => {
      expect(r instanceof UrlTree).toBeTrue();
      done();
    });
  });

  it('does not treat a look-alike path as a platform page', () => {
    expect(isPlatformPath('/portal/pages/organizations-report')).toBeFalse();
  });
});
