import { FE_B_ONBOARDING_ROUTES } from './fe-b.routes';

describe('FE_B_ONBOARDING_ROUTES (A50 D-29)', () => {
  it('has the template, email and sample data pages, each behind its own permission', () => {
    expect(FE_B_ONBOARDING_ROUTES.map(r => r.path)).toEqual([
      'document-templates', 'document-templates/:uuid', 'email-templates', 'email-templates/:uuid', 'sample-data'
    ]);
    FE_B_ONBOARDING_ROUTES.forEach(r => {
      expect(r.canActivate?.length).withContext(r.path!).toBe(1);
      expect(typeof r.loadComponent).toBe('function');
    });
  });

  it('every route loads its component', async () => {
    const names = await Promise.all(FE_B_ONBOARDING_ROUTES.map(r => (r.loadComponent as any)().then((c: any) => c.name)));
    expect(names).toEqual([
      'DocumentTemplateListComponent', 'DocumentTemplateEditorComponent', 'EmailTemplateListComponent',
      'EmailTemplateEditorComponent', 'SampleDataManagerComponent'
    ]);
  });
});
