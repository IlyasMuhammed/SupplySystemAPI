import { MenuItem } from 'primeng/api';

// A50 D-29 — the shared Settings › Organization menu group, agreed between FE-A50 and FE-B50 (one definition, asserted by
// app.menu.a50.spec.ts). FE-A owns entries 1–2 (Setup Wizard, Default Data); FE-B owns entries 3–6. Every entry's
// permission is the same code as its route. app.menu.ts puts this group first under Administration › Settings.

export const ONBOARDING_NAV_ROUTE = '/portal/pages/onboarding';

type NavItem = MenuItem & { permRequired?: string[]; featureCode?: string | string[]; items?: NavItem[] };

export function organizationSettingsGroup(): NavItem {
  return {
    label: 'Organization',
    icon: 'pi pi-fw pi-building',
    items: [
      // FE-A50
      { label: 'Setup Wizard', icon: 'pi pi-fw pi-compass',
        routerLink: [`${ONBOARDING_NAV_ROUTE}/setup`], permRequired: ['SETUP_WIZARD_RUN'] },
      { label: 'Default Data', icon: 'pi pi-fw pi-database',
        routerLink: [`${ONBOARDING_NAV_ROUTE}/default-data`], permRequired: ['SETUP_WIZARD_RUN'] },
      // FE-B50
      { label: 'Data Import', icon: 'pi pi-fw pi-file-import',
        routerLink: ['/portal/pages/settings/import'], featureCode: 'MODULE_DATA_IMPORT', permRequired: ['IMPORT_PROFILE_VIEW'] },
      { label: 'Document Templates', icon: 'pi pi-fw pi-file-edit',
        routerLink: [`${ONBOARDING_NAV_ROUTE}/document-templates`], permRequired: ['DOC_TEMPLATE_MANAGE'] },
      { label: 'Email Templates', icon: 'pi pi-fw pi-envelope',
        routerLink: [`${ONBOARDING_NAV_ROUTE}/email-templates`], permRequired: ['EMAIL_TEMPLATE_MANAGE'] },
      { label: 'Sample Data', icon: 'pi pi-fw pi-box',
        routerLink: [`${ONBOARDING_NAV_ROUTE}/sample-data`], permRequired: ['SAMPLE_DATA_MANAGE'] }
    ]
  };
}
