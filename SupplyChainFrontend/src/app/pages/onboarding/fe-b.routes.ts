import { Routes } from '@angular/router';
import { permissionGuard } from '../gaurds/permission.guard';

/**
 * A50 FE-B — document / email templates and sample data (D-25..D-27, D-29), spread into onboarding.routes.ts
 * children, so they live under /portal/pages/onboarding/….
 */
export const FE_B_ONBOARDING_ROUTES: Routes = [
  {
    path: 'document-templates',
    loadComponent: () => import('./templates/document-template-list.component').then(m => m.DocumentTemplateListComponent),
    canActivate: [permissionGuard('DOC_TEMPLATE_MANAGE')]
  },
  {
    path: 'document-templates/:uuid',
    loadComponent: () => import('./templates/document-template-editor.component').then(m => m.DocumentTemplateEditorComponent),
    canActivate: [permissionGuard('DOC_TEMPLATE_MANAGE')]
  },
  {
    path: 'email-templates',
    loadComponent: () => import('./templates/email-template-list.component').then(m => m.EmailTemplateListComponent),
    canActivate: [permissionGuard('EMAIL_TEMPLATE_MANAGE')]
  },
  {
    path: 'email-templates/:uuid',
    loadComponent: () => import('./templates/email-template-editor.component').then(m => m.EmailTemplateEditorComponent),
    canActivate: [permissionGuard('EMAIL_TEMPLATE_MANAGE')]
  },
  {
    path: 'sample-data',
    loadComponent: () => import('./sample-data/sample-data-manager.component').then(m => m.SampleDataManagerComponent),
    canActivate: [permissionGuard('SAMPLE_DATA_MANAGE')]
  }
];
