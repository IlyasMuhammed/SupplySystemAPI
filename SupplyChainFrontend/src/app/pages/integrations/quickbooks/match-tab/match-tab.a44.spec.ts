import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { MatchTabComponent } from './match-tab.component';
import { MatchCandidateModel, QuickBooksIntegrationService, RemoteEntityModel } from '../../../../services/quickbooks-integration.service';

function candidate(overrides: Partial<MatchCandidateModel>): MatchCandidateModel {
  return {
    id: 'x', kind: 'Customer', externalId: 'p', sourceSystem: 'SCM', localLabel: 'Acme', remoteId: null, remoteName: null,
    confidence: 'None', reason: null, decision: 'Pending', ...overrides
  };
}

const CANDIDATES = [
  candidate({ id: 'c1', externalId: 'bp-1', localLabel: 'Gamma Traders' }),
  candidate({ id: 'c2', externalId: 'bp-2', localLabel: 'Delta', remoteId: '70', decision: 'Link' })
];

const REMOTE: RemoteEntityModel[] = [
  { remoteId: '91', kind: 'Customer', name: 'Gamma Traders', email: 'g@x.pk', taxId: null, active: true, linked: false },
  { remoteId: '92', kind: 'Customer', name: 'Old customer', active: false, linked: false, deepLink: 'https://app.qbo.intuit.com/app/customerdetail?nameId=92' }
];

/** A44 D-17 — import QuickBooks records as remote-only and link one to its SCM record. */
describe('MatchTabComponent — A44 remote-only records', () => {
  let fixture: ComponentFixture<MatchTabComponent>;
  let component: MatchTabComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let toasts: jasmine.Spy;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(opts: { remote404?: boolean; canManage?: boolean } = {}) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', [
      'getMatchCandidates', 'scanMatches', 'confirmMatches', 'confirmMatchingComplete', 'getRemote', 'importRemote', 'linkRemote'
    ]);
    service.getMatchCandidates.and.returnValue(of(CANDIDATES.map(c => ({ ...c }))));
    service.getRemote.and.returnValue(opts.remote404 ? throwError(() => ({ status: 404 })) : of(REMOTE.map(r => ({ ...r }))));
    service.importRemote.and.returnValue(of({ kind: 'Customer', read: 40, added: 38, updated: 2, removed: 0, linked: 38, unlinked: 2 }));
    service.linkRemote.and.returnValue(of({}));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [MatchTabComponent],
      providers: [provideNoopAnimations(), MessageService, { provide: QuickBooksIntegrationService, useValue: service }]
    }).compileComponents();

    fixture = TestBed.createComponent(MatchTabComponent);
    component = fixture.componentInstance;
    component.canManage = opts.canManage ?? true;
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    fixture.detectChanges();
  }

  it('lists the unlinked QuickBooks records of the kind', async () => {
    await setup();
    expect(service.getRemote).toHaveBeenCalledWith('Customer', false);
    const rows = fixture.nativeElement.querySelectorAll('[data-testid="remote-row"]');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Gamma Traders');
    expect(rows[0].textContent).toContain('Email: g@x.pk');
    expect(rows[1].textContent).toContain('inactive in QuickBooks');
    expect(rows[1].querySelector('a')!.getAttribute('href')).toContain('nameId=92');
    component.remoteSearch = 'old';
    expect(component.filteredRemote.map(r => r.remoteId)).toEqual(['92']);
  });

  it('"Import from QuickBooks" reads them, then lists them again', async () => {
    await setup();
    service.getRemote.calls.reset();
    query('import-remote')!.querySelector('button')!.click();
    expect(service.importRemote).toHaveBeenCalledOnceWith('Customer');
    expect(service.getRemote).toHaveBeenCalledTimes(1);
    expect(toasts.calls.mostRecent().args[0].detail).toContain('40 QuickBooks customers read; 2 are not linked');
  });

  it('Link proposes the SCM record with the same name and links it', async () => {
    await setup();
    // Only SCM records not linked yet are targets.
    expect(component.linkTargets.map(t => t.value)).toEqual(['bp-1']);
    component.openLink(component.remote![0]);
    expect(component.linkVisible).toBeTrue();
    expect(component.linkExternalId).toBe('bp-1');
    service.getMatchCandidates.calls.reset();
    component.link();
    expect(service.linkRemote).toHaveBeenCalledOnceWith('Customer', '91', { externalId: 'bp-1' });
    expect(component.linkVisible).toBeFalse();
    expect(service.getMatchCandidates).toHaveBeenCalled();
  });

  it('no proposal when no SCM record has the same name', async () => {
    await setup();
    component.openLink(component.remote![1]);
    expect(component.linkExternalId).toBeNull();
    component.link();
    expect(service.linkRemote).not.toHaveBeenCalled();
  });

  it('switching kind loads that kind\'s remote records', async () => {
    await setup();
    service.getRemote.calls.reset();
    component.selectKind('Vendor');
    expect(service.getRemote).toHaveBeenCalledOnceWith('Vendor', false);
  });

  it('hides the section and the import button on a server without remote records', async () => {
    await setup({ remote404: true });
    fixture.detectChanges();
    expect(query('remote-card')).toBeNull();
    expect(query('import-remote')).toBeNull();
  });

  it('no Link or Import without manage', async () => {
    await setup({ canManage: false });
    expect(query('import-remote')).toBeNull();
    expect(query('link-remote-91')).toBeNull();
  });
});
