import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { OnboardingChecklistWidgetComponent } from './onboarding-checklist-widget.component';
import { OnboardingChecklistModel, OnboardingDemoService } from '../../../services/onboarding-demo.service';
import { AuthService } from '../../service/auth.service';

function checklist(over: Partial<OnboardingChecklistModel> = {}): OnboardingChecklistModel {
  return {
    dismissed: false, completedCount: 3, totalCount: 8, isComplete: false,
    steps: [
      { stepCode: 'ADD_PRODUCT', title: 'Add your first product', link: '/portal/pages/inventory/products/new', sortOrder: 30, completed: false },
      { stepCode: 'COMPLETE_SETUP', title: 'Complete the setup wizard', link: '/portal/pages/onboarding/setup', sortOrder: 10, completed: true, autoCompleted: true },
      { stepCode: 'ADD_CUSTOMER', title: 'Add your first customer', link: '/portal/pages/customers', sortOrder: 20, completedAt: '2026-11-09T10:00:00Z' }
    ],
    ...over
  };
}

describe('OnboardingChecklistWidgetComponent (A50 tasks 313–317)', () => {
  let fixture: ComponentFixture<OnboardingChecklistWidgetComponent>;
  let api: jasmine.SpyObj<OnboardingDemoService>;
  let perms: string[];

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  async function setup(model: OnboardingChecklistModel | 'error', permissions = ['ONBOARDING_VIEW']) {
    perms = permissions;
    api = jasmine.createSpyObj<OnboardingDemoService>('OnboardingDemoService', ['getChecklist', 'completeStep', 'dismissChecklist']);
    api.getChecklist.and.returnValue(model === 'error'
      ? throwError(() => ({ status: 403 }))
      : of({ success: true, message: '', result: model }));
    await TestBed.configureTestingModule({
      imports: [OnboardingChecklistWidgetComponent],
      providers: [
        provideRouter([]),
        { provide: OnboardingDemoService, useValue: api },
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(OnboardingChecklistWidgetComponent);
    fixture.detectChanges();
  }

  it('shows "3 of 8 done", steps in sortOrder, and "Do this now" links only on open steps', async () => {
    await setup(checklist());
    expect(q('checklist-progress')?.textContent?.trim()).toBe('3 of 8 done');
    const codes = Array.from(fixture.nativeElement.querySelectorAll('.ocl-step')).map((e: any) => e.getAttribute('data-testid'));
    expect(codes).toEqual(['step-COMPLETE_SETUP', 'step-ADD_CUSTOMER', 'step-ADD_PRODUCT']);
    expect(q('do-ADD_PRODUCT')?.getAttribute('href')).toBe('/portal/pages/inventory/products/new');
    expect(q('do-COMPLETE_SETUP')).toBeNull();
    expect(q('do-ADD_CUSTOMER')).toBeNull();
  });

  it('ticks a step by hand and shows the returned status', async () => {
    await setup(checklist());
    api.completeStep.and.returnValue(of({ success: true, message: '', result: checklist({ completedCount: 4 }) }));
    (q('tick-ADD_PRODUCT') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(api.completeStep).toHaveBeenCalledWith('ADD_PRODUCT');
    expect(q('checklist-progress')?.textContent?.trim()).toBe('4 of 8 done');
  });

  it('dismiss hides it', async () => {
    await setup(checklist());
    api.dismissChecklist.and.returnValue(of({ success: true, message: '', result: checklist({ dismissed: true }) }));
    (q('checklist-dismiss')?.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(q('onboarding-checklist')).toBeNull();
  });

  it('renders nothing when dismissed, when complete, or when the call fails (REV50-4)', async () => {
    await setup(checklist({ dismissed: true }));
    expect(q('onboarding-checklist')).toBeNull();
    TestBed.resetTestingModule();
    await setup(checklist({ isComplete: true, completedCount: 8 }));
    expect(q('onboarding-checklist')).toBeNull();
    TestBed.resetTestingModule();
    await setup('error');
    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  it('never calls the API without ONBOARDING_VIEW', async () => {
    await setup(checklist(), []);
    expect(api.getChecklist).not.toHaveBeenCalled();
    expect(q('onboarding-checklist')).toBeNull();
  });
});
