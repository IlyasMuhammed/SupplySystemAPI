import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SyncControlTabComponent } from './sync-control-tab.component';
import { QuickBooksIntegrationService, SyncControlRowModel } from '../../../../services/quickbooks-integration.service';

const ROWS: SyncControlRowModel[] = [
  { documentType: 'Customer', isEnabled: true, holdWhenDisabled: false, heldCount: 0 },
  { documentType: 'POSSale', isEnabled: false, holdWhenDisabled: true, heldCount: 12 },
  { documentType: 'PurchaseBill', isEnabled: false, holdWhenDisabled: false, heldCount: 0 },
  { documentType: 'Payment', isEnabled: true, holdWhenDisabled: false, heldCount: 3 }
];

describe('SyncControlTabComponent', () => {
  let fixture: ComponentFixture<SyncControlTabComponent>;
  let component: SyncControlTabComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let toasts: jasmine.Spy;
  let confirm: jasmine.Spy;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(opts: { canManage?: boolean; fail?: boolean } = {}) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', ['getSyncControl', 'updateSyncControl', 'releaseHeld']);
    service.getSyncControl.and.returnValue(opts.fail ? throwError(() => ({ status: 500, error: { message: 'Down.' } })) : of(ROWS.map(r => ({ ...r }))));
    service.updateSyncControl.and.callFake((type, body) => of({
      row: { documentType: type, isEnabled: body.isEnabled, holdWhenDisabled: body.holdWhenDisabled, heldCount: body.isEnabled ? 0 : 12 },
      released: body.confirmRelease ? 12 : 0
    }));
    service.releaseHeld.and.returnValue(of({ documentType: 'Payment', released: 3, stillHeld: 0 }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SyncControlTabComponent],
      providers: [provideNoopAnimations(), MessageService, { provide: QuickBooksIntegrationService, useValue: service }]
    }).compileComponents();

    fixture = TestBed.createComponent(SyncControlTabComponent);
    component = fixture.componentInstance;
    component.canManage = opts.canManage ?? true;
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm');
    fixture.detectChanges();
  }

  it('lists every document type with its status and held count', async () => {
    await setup();
    expect(fixture.nativeElement.querySelectorAll('.sc-row').length).toBe(11);
    expect(query('sc-status-Customer')!.textContent).toContain('On');
    expect(query('sc-status-POSSale')!.textContent).toContain('Held');
    expect(query('sc-status-PurchaseBill')!.textContent).toContain('Off');
    expect(query('sc-held-POSSale')!.textContent).toContain('12 held');
    expect(query('total-held')!.textContent).toContain('15 held');
    // A type with no row from the server is on (BR-29).
    expect(query('sc-status-VendorCredit')!.textContent).toContain('On');
  });

  it('shows the release banner when a held type is turned on, and confirms before saving', async () => {
    await setup();
    const pos = component.rows.find(r => r.documentType === 'POSSale')!;
    component.setEnabled(pos, true);
    fixture.detectChanges();
    expect(query('release-banner')!.textContent).toContain('Enabling POS sales will release 12 queued transactions.');

    component.save();
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(service.updateSyncControl).not.toHaveBeenCalled();
    confirm.calls.mostRecent().args[0].accept();
    expect(service.updateSyncControl).toHaveBeenCalledOnceWith('POSSale', { isEnabled: true, holdWhenDisabled: true, confirmRelease: true });
    expect(toasts.calls.mostRecent().args[0].detail).toContain('12 held transactions were released');
    expect(component.dirty).toBeFalse();
  });

  it('saves without a confirmation when nothing is released', async () => {
    await setup();
    const emitted: number[] = [];
    component.changed.subscribe(() => emitted.push(1));
    const bill = component.rows.find(r => r.documentType === 'PurchaseBill')!;
    component.setHold(bill, true);
    fixture.detectChanges();
    expect(query('release-banner')).toBeNull();
    component.save();
    expect(confirm).not.toHaveBeenCalled();
    expect(service.updateSyncControl).toHaveBeenCalledOnceWith('PurchaseBill', { isEnabled: false, holdWhenDisabled: true, confirmRelease: false });
    expect(emitted.length).toBe(1);
  });

  it('keeps a failed row unsaved and says why', async () => {
    await setup();
    service.updateSyncControl.and.returnValue(throwError(() => ({ status: 409, error: { message: 'Confirm the release.' } })));
    const bill = component.rows.find(r => r.documentType === 'PurchaseBill')!;
    component.setEnabled(bill, true);
    component.save();
    expect(toasts.calls.mostRecent().args[0].severity).toBe('error');
    expect(toasts.calls.mostRecent().args[0].detail).toBe('Confirm the release.');
    expect(component.isChanged(component.rows.find(r => r.documentType === 'PurchaseBill')!)).toBeTrue();
  });

  it('a 409 (more held than shown) keeps the row unsaved and shows the server\'s held count', async () => {
    await setup();
    service.updateSyncControl.and.returnValue(throwError(() => ({
      status: 409, error: { success: false, message: 'Enabling releases 20 held records. Confirm the release.', result: { documentType: 'PurchaseBill', heldCount: 20 } }
    })));
    const bill = component.rows.find(r => r.documentType === 'PurchaseBill')!;
    component.setEnabled(bill, true);
    component.save();
    expect(component.rows.find(r => r.documentType === 'PurchaseBill')!.heldCount).toBe(20);
    expect(component.isChanged(component.rows.find(r => r.documentType === 'PurchaseBill')!)).toBeTrue();
    fixture.detectChanges();
    expect(query('release-banner')!.textContent).toContain('will release 20 queued transactions');
  });

  it('releases what an enabled type still holds', async () => {
    await setup();
    fixture.detectChanges();
    expect(query('sc-release-POSSale')).toBeNull(); // off: turning it on releases
    query('sc-release-Payment')!.querySelector('button')!.click();
    expect(service.releaseHeld).toHaveBeenCalledOnceWith('Payment');
    expect(component.rows.find(r => r.documentType === 'Payment')!.heldCount).toBe(0);
  });

  it('discard restores what was saved', async () => {
    await setup();
    component.setEnabled(component.rows[0], false);
    expect(component.dirty).toBeTrue();
    component.discard();
    expect(component.dirty).toBeFalse();
  });

  it('read-only without manage', async () => {
    await setup({ canManage: false });
    component.setEnabled(component.rows[0], false);
    expect(component.dirty).toBeFalse();
    expect(query('sc-actions')).toBeNull();
  });

  it('shows the error with a retry when it cannot load', async () => {
    await setup({ fail: true });
    expect(query('load-failed')!.textContent).toContain('Down.');
  });
});
