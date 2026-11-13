import { Routes } from '@angular/router';
import { permissionGuard } from '../gaurds/permission.guard';
import { FE_B_ONBOARDING_ROUTES } from './fe-b.routes';

/**
 * A50 — `/portal/pages/onboarding/*`, lazy (pages.routes.ts → 'onboarding'). FE-A50 owns setup + default-data;
 * FE-B50's screens (document / email templates, sample data) come from fe-b.routes.ts. Each route has the same code
 * as its menu entry (onboarding.nav.ts).
 */
export default [
  { path: 'setup', loadComponent: () => import('./setup/setup-wizard.component').then(m => m.SetupWizardComponent),
    canActivate: [permissionGuard('SETUP_WIZARD_RUN')] },
  { path: 'default-data', loadComponent: () => import('./default-data/default-data.component').then(m => m.DefaultDataComponent),
    canActivate: [permissionGuard('SETUP_WIZARD_RUN')] },
  ...FE_B_ONBOARDING_ROUTES
] as Routes;
