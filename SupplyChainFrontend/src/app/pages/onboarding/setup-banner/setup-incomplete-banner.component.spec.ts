import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { SetupIncompleteBannerComponent } from './setup-incomplete-banner.component';
import { OnboardingSetupService, SetupStatusInfo } from '../../../services/onboarding-setup.service';
import { TenantService } from '../../service/tenant.service';

/** A50 task 318 / D-7 — the dashboard banner. */
describe('SetupIncompleteBannerComponent', () => {
  let fixture: ComponentFixture<SetupIncompleteBannerComponent>;
  let status: ReturnType<typeof signal<SetupStatusInfo | null | undefined>>;
  let loads: number;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const info = (setupStatus: any, canRunWizard: boolean): SetupStatusInfo =>
    ({ setupStatus, canRunWizard, setupStep: 0, countryCode: null, industryType: null, completedAt: null });

  async function setup(s: SetupStatusInfo | null, superAdmin = false) {
    status = signal<SetupStatusInfo | null | undefined>(s);
    loads = 0;
    await TestBed.configureTestingModule({
      imports: [SetupIncompleteBannerComponent],
      providers: [
        provideRouter([]),
        { provide: OnboardingSetupService, useValue: { status, loadStatus: () => { loads++; return of(s); } } },
        { provide: TenantService, useValue: { isSuperAdmin: () => superAdmin } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(SetupIncompleteBannerComponent);
    fixture.detectChanges();
  }

  it('a setup admin gets a "Start setup" link while NotStarted', async () => {
    await setup(info('NotStarted', true));
    expect(loads).toBe(1);
    expect(q('setup-banner').textContent).toContain('Setup incomplete');
    expect(q('setup-banner-link').textContent).toContain('Start setup');
    expect(q('setup-banner-link').getAttribute('href')).toBe('/portal/pages/onboarding/setup');
  });

  it('"Continue setup" while InProgress', async () => {
    await setup(info('InProgress', true));
    expect(q('setup-banner-link').textContent).toContain('Continue setup');
  });

  it('other users are told to ask their administrator', async () => {
    await setup(info('NotStarted', false));
    expect(q('setup-banner-link')).toBeNull();
    expect(q('setup-banner-ask').textContent).toContain('ask your administrator');
  });

  it('hidden once Completed or Skipped, without an organization, and for the super admin', async () => {
    await setup(info('Completed', true));
    expect(q('setup-banner')).toBeNull();
    status.set(info('Skipped', true));
    fixture.detectChanges();
    expect(q('setup-banner')).toBeNull();
    status.set(null);
    fixture.detectChanges();
    expect(q('setup-banner')).toBeNull();
  });

  it('the super admin never loads the status', async () => {
    await setup(info('NotStarted', true), true);
    expect(loads).toBe(0);
    expect(q('setup-banner')).toBeNull();
  });
});
