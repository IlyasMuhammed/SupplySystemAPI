import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { RejectionReasonsComponent } from './rejection-reasons.component';
import { SalesPreorderService, RejectionReason } from '../../../services/sales-preorder.service';
import { AuthService } from '../../service/auth.service';

function reason(overrides: Partial<RejectionReason> = {}): RejectionReason {
  return { uuid: 'r-dis', code: 'DIS', description: 'Product discontinued', isActive: true, isSystem: true, displayOrder: 30, createdDate: '', ...overrides };
}

describe('RejectionReasonsComponent (rejection reasons admin)', () => {
  let fixture: ComponentFixture<RejectionReasonsComponent>;
  let component: RejectionReasonsComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;
  let permissions: string[];
  let el: HTMLElement;

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  async function setup(reasons: RejectionReason[] = [
    reason(),
    reason({ uuid: 'r-old', code: 'OLD', description: 'Old reason', isActive: false, isSystem: false, displayOrder: 110 }),
    reason({ uuid: 'r-cus', code: 'CUS', description: 'Custom', isSystem: false, displayOrder: 120 })
  ]) {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService',
      ['getRejectionReasons', 'createRejectionReason', 'updateRejectionReason', 'deactivateRejectionReason',
       'activateRejectionReason', 'deleteRejectionReason']);
    service.getRejectionReasons.and.returnValue(of({ success: true, message: '', result: reasons } as any));
    service.createRejectionReason.and.callFake(req => of({ success: true, message: '', result: reason({ ...req, uuid: 'r-new', isSystem: false } as any) } as any));
    service.updateRejectionReason.and.returnValue(of({ success: true, message: '', result: reason() } as any));
    service.deactivateRejectionReason.and.returnValue(of({ success: true, message: '', result: reason({ isActive: false }) } as any));
    service.activateRejectionReason.and.returnValue(of({ success: true, message: '', result: reason() } as any));
    service.deleteRejectionReason.and.returnValue(of({ success: true, message: '' } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [RejectionReasonsComponent],
      providers: [
        provideNoopAnimations(),
        { provide: SalesPreorderService, useValue: service },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(RejectionReasonsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  beforeEach(() => { permissions = ['SALE_REJECTION_REASON_MANAGE']; });

  it('lists every reason, deactivated ones included, with code, description, active and order', async () => {
    await setup();
    expect(service.getRejectionReasons).toHaveBeenCalledWith(true);
    const row = q('reason-DIS')!.textContent!;
    expect(row).toContain('DIS');
    expect(row).toContain('Product discontinued');
    expect(row).toContain('Active');
    expect(row).toContain('30');
    expect(q('reason-OLD')!.textContent).toContain('Deactivated');
  });

  it('offers delete only on a custom reason; seeded codes can only be deactivated', async () => {
    await setup();
    expect(q('delete-DIS')).toBeNull();
    expect(q('deactivate-DIS')).not.toBeNull();
    expect(q('delete-CUS')).not.toBeNull();
    expect(q('reactivate-OLD')).not.toBeNull();
  });

  it('creates a reason with the code upper-cased, and refuses a malformed code', async () => {
    await setup();
    component.openCreate();
    component.draft = { code: 'ab c', description: 'Something', displayOrder: null };
    expect(component.problem).toContain('Code');
    component.save();
    expect(service.createRejectionReason).not.toHaveBeenCalled();

    component.draft = { code: ' lat ', description: ' Late request ', displayOrder: 115 };
    expect(component.problem).toBeNull();
    component.save();
    expect(service.createRejectionReason).toHaveBeenCalledWith({ code: 'LAT', description: 'Late request', displayOrder: 115 });
    expect(component.dialogVisible).toBeFalse();
  });

  it('needs a description of at most 200 characters', async () => {
    await setup();
    component.openCreate();
    component.draft = { code: 'LAT', description: ' ', displayOrder: null };
    expect(component.problem).toContain('Description');
    component.draft = { code: 'LAT', description: 'x'.repeat(201), displayOrder: null };
    expect(component.problem).toContain('200');
  });

  it('edits description and order but never the code', async () => {
    await setup();
    component.openEdit(component.reasons[0]);
    component.draft.description = 'Discontinued by maker';
    component.draft.displayOrder = 35;
    component.save();
    expect(service.updateRejectionReason).toHaveBeenCalledWith('r-dis', { description: 'Discontinued by maker', displayOrder: 35 });
  });

  it('shows the server\'s refusal of a duplicate code in the dialog', async () => {
    await setup();
    service.createRejectionReason.and.returnValue(throwError(() => ({ status: 409, error: { message: 'Code LAT already exists.' } })));
    component.openCreate();
    component.draft = { code: 'LAT', description: 'Late', displayOrder: null };
    component.save();
    expect(component.saveError).toBe('Code LAT already exists.');
    expect(component.dialogVisible).toBeTrue();
  });

  it('deactivates and reactivates, then reloads', async () => {
    await setup();
    component.deactivate(component.reasons[0]);
    expect(service.deactivateRejectionReason).toHaveBeenCalledWith('r-dis');
    component.reactivate(component.reasons[1]);
    expect(service.activateRejectionReason).toHaveBeenCalledWith('r-old');
    expect(service.getRejectionReasons).toHaveBeenCalledTimes(3);
  });

  it('deletes a custom reason, and says why when the server refuses (in use)', async () => {
    await setup();
    const toast = spyOn(component['messages'], 'add');
    service.deleteRejectionReason.and.returnValue(throwError(() => ({ status: 409, error: { message: 'In use — deactivate it instead.' } })));
    component.delete(component.reasons[2]);
    expect(service.deleteRejectionReason).toHaveBeenCalledWith('r-cus');
    expect(toast).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'In use — deactivate it instead.' }));
  });

  it('never deletes a seeded reason, even if asked', async () => {
    await setup();
    component.delete(component.reasons[0]);
    expect(service.deleteRejectionReason).not.toHaveBeenCalled();
  });

  it('shows the list read-only without the manage permission', async () => {
    permissions = [];
    await setup();
    expect(q('new-reason')).toBeNull();
    expect(q('deactivate-DIS')).toBeNull();
  });
});
