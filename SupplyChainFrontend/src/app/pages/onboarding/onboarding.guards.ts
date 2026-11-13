import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { OnboardingSetupService } from '../../services/onboarding-setup.service';
import { AuthService } from '../service/auth.service';
import { TenantService } from '../service/tenant.service';
import { SETUP_WIZARD_ROUTE, SETUP_WIZARD_RUN } from './onboarding.shared';

/**
 * A50 D-7 / BR-50-001 / task 144 — on `/portal/dashboard`: a user who holds SETUP_WIZARD_RUN, of an organization
 * whose setup is NotStarted, is sent to the setup wizard (first login). Everyone else passes (they see the
 * "Setup incomplete" banner instead). The status is read once per session; any error lets the dashboard open.
 * Existing organizations were backfilled as Completed (D-6), so nobody already working is redirected.
 */
export const setupWizardRedirectGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const tenant = inject(TenantService);
  const api = inject(OnboardingSetupService);
  const router = inject(Router);

  if (tenant.isSuperAdmin() || !auth.hasPermission(SETUP_WIZARD_RUN)) return true;
  return api.loadStatus().pipe(
    map(s => (s && s.setupStatus === 'NotStarted' && s.canRunWizard !== false) ? router.createUrlTree([SETUP_WIZARD_ROUTE]) : true),
    catchError(() => of(true))
  );
};
