import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { MatchTabComponent } from './match-tab.component';
import {
  IntegrationSettingsModel, MatchCandidateModel, QuickBooksIntegrationService
} from '../../../../services/quickbooks-integration.service';

function candidate(overrides: Partial<MatchCandidateModel>): MatchCandidateModel {
  return {
    id: 'x', kind: 'Customer', externalId: 'p', sourceSystem: 'SCM', localLabel: 'Acme', remoteId: null, remoteName: null,
    confidence: 'None', reason: null, decision: 'Pending', ...overrides
  };
}

const CUSTOMERS: MatchCandidateModel[] = [
  candidate({ id: 'c1', localLabel: 'Acme Ltd', remoteId: '58', remoteName: 'Acme Ltd', confidence: 'Exact', reason: 'Same name' }),
  candidate({ id: 'c2', localLabel: 'Beta Co', remoteId: '61', remoteName: 'Beta Company', confidence: 'Probable', reason: 'Similar name' }),
  candidate({ id: 'c3', localLabel: 'Gamma', confidence: 'None' }),
  candidate({ id: 'c4', localLabel: 'Delta', remoteId: '70', remoteName: 'Delta', confidence: 'Exact', decision: 'Link', reason: 'Same tax id' }),
  candidate({ id: 'c5', localLabel: 'Epsilon', remoteId: '72', remoteName: 'Epsilon', confidence: 'Exact', decision: 'Skip' })
];

describe('MatchTabComponent', () => {
  let fixture: ComponentFixture<MatchTabComponent>;
  let component: MatchTabComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let toasts: jasmine.Spy;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(canManage = true) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', [
      'getMatchCandidates', 'scanMatches', 'confirmMatches', 'confirmMatchingComplete'
    ]);
    service.getMatchCandidates.and.callFake(kind => of(kind === 'Customer' ? CUSTOMERS.map(c => ({ ...c })) : [
      candidate({ id: 'v1', kind: 'Vendor', localLabel: 'Supplier A', remoteId: '9', confidence: 'Exact' })
    ]));
    service.scanMatches.and.returnValue(of({ kind: 'Customer', localCount: 5, remoteCount: 40, exact: 3, probable: 1, unmatched: 1 }));
    service.confirmMatches.and.callFake((kind, body) => of(CUSTOMERS.map(c => {
      const d = body.decisions.find(x => x.candidateId === c.id);
      return d ? { ...c, decision: d.decision, remoteId: d.remoteId ?? c.remoteId } : { ...c };
    })));
    service.confirmMatchingComplete.and.returnValue(of({ mode: 'DryRun', matchingConfirmedAt: '2026-09-30T12:00:00Z' } as IntegrationSettingsModel));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [MatchTabComponent],
      providers: [provideNoopAnimations(), MessageService, { provide: QuickBooksIntegrationService, useValue: service }]
    }).compileComponents();

    fixture = TestBed.createComponent(MatchTabComponent);
    component = fixture.componentInstance;
    component.canManage = canManage;
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    fixture.detectChanges();
  }

  it('loads the customer proposals first and shows each with its confidence and reason', async () => {
    await setup();
    expect(service.getMatchCandidates).toHaveBeenCalledOnceWith('Customer');
    const rows = fixture.nativeElement.querySelectorAll('[data-testid="candidate-row"]');
    expect(rows.length).toBe(5);
    expect(rows[0].textContent).toContain('Acme Ltd');
    expect(rows[0].textContent).toContain('QuickBooks id 58');
    expect(rows[0].textContent).toContain('Exact');
    expect(rows[0].textContent).toContain('Same name');
    expect(rows[2].textContent).toContain('No match found');
  });

  it('switches kind from the Customer / Vendor / Item sub-tabs', async () => {
    await setup();
    query('kind-Vendor')!.click();
    fixture.detectChanges();
    expect(component.kind).toBe('Vendor');
    expect(service.getMatchCandidates).toHaveBeenCalledWith('Vendor');
    expect(fixture.nativeElement.querySelectorAll('[data-testid="candidate-row"]').length).toBe(1);
  });

  it('filters by confidence', async () => {
    await setup();
    component.setConfidenceFilter('Exact');
    fixture.detectChanges();
    expect(component.filtered.map(c => c.id)).toEqual(['c1', 'c4', 'c5']);
    expect(fixture.nativeElement.querySelectorAll('[data-testid="candidate-row"]').length).toBe(3);
    component.setConfidenceFilter('None');
    expect(component.filtered.map(c => c.id)).toEqual(['c3']);
    // A new kind starts unfiltered.
    component.selectKind('Vendor');
    expect(component.filtered.map(c => c.id)).toEqual(['v1']);
  });

  it('scans QuickBooks, shows the counts and reloads the proposals', async () => {
    await setup();
    (query('scan')!.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(service.scanMatches).toHaveBeenCalledOnceWith('Customer');
    expect(query('scan-summary')!.textContent).toContain('3 exact');
    expect(service.getMatchCandidates).toHaveBeenCalledTimes(2);
  });

  it('"Link all exact matches" links every exact match with a QuickBooks record, and saving sends exactly those changes', async () => {
    await setup();
    component.linkAllExact();
    fixture.detectChanges();

    // c1 was undecided, c5 was Skip: both become Link to their proposed record. c4 is already Link. Probable/None untouched.
    expect(component.draft(CUSTOMERS[0]).decision).toBe('Link');
    expect(component.draft(CUSTOMERS[1]).decision).toBeNull();
    expect(component.draft(CUSTOMERS[2]).decision).toBeNull();
    expect(component.pendingChanges).toBe(2);
    expect(query('pending-changes')!.textContent).toContain('2 unsaved decisions');

    component.save();

    expect(service.confirmMatches).toHaveBeenCalledOnceWith('Customer', {
      decisions: [
        { candidateId: 'c1', decision: 'Link', remoteId: '58' },
        { candidateId: 'c5', decision: 'Link', remoteId: '72' }
      ]
    });
    expect(component.pendingChanges).toBe(0);
    expect(component.candidates.find(c => c.id === 'c1')!.decision).toBe('Link');
  });

  it('sends Create new and Skip without a QuickBooks id', async () => {
    await setup();
    component.setDecision(component.candidates[1], 'CreateNew');
    component.setDecision(component.candidates[2], 'Skip');
    component.save();
    expect(service.confirmMatches).toHaveBeenCalledOnceWith('Customer', {
      decisions: [
        { candidateId: 'c2', decision: 'CreateNew', remoteId: null },
        { candidateId: 'c3', decision: 'Skip', remoteId: null }
      ]
    });
  });

  it('does not offer Link where there is nothing to link to', async () => {
    await setup();
    const withRemote = component.decisionOptions(component.candidates[0]);
    const without = component.decisionOptions(component.candidates[2]);
    expect(withRemote.find(o => o.value === 'Link')!.disabled).toBeFalsy();
    expect(without.find(o => o.value === 'Link')!.disabled).toBeTrue();
  });

  it('keeps each kind\'s unsaved decisions while another kind is shown', async () => {
    await setup();
    component.linkAllExact();
    component.selectKind('Vendor');
    expect(service.getMatchCandidates).toHaveBeenCalledWith('Vendor');
    expect(component.pendingChanges).toBe(0);

    component.selectKind('Customer');
    expect(component.pendingChanges).toBe(2);
  });

  it('says so when there is nothing to save', async () => {
    await setup();
    component.save();
    expect(service.confirmMatches).not.toHaveBeenCalled();
    expect(toasts.calls.mostRecent().args[0].summary).toBe('Nothing to save');
  });

  it('asks before marking matching complete, then reports the new settings', async () => {
    await setup();
    const emitted: IntegrationSettingsModel[] = [];
    component.settingsChange.subscribe(s => emitted.push(s));
    const confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm');

    component.linkAllExact();
    (query('mark-complete')!.querySelector('button') as HTMLButtonElement).click();
    const options = confirm.calls.mostRecent().args[0];
    expect(options.message).toContain('2 unsaved decisions');
    expect(service.confirmMatchingComplete).not.toHaveBeenCalled();

    options.accept!();
    expect(service.confirmMatchingComplete).toHaveBeenCalledOnceWith(true);
    expect(emitted[0].matchingConfirmedAt).toBe('2026-09-30T12:00:00Z');
    fixture.detectChanges();
    expect(query('matching-confirmed')).not.toBeNull();
  });

  it('only shows the proposals without the manage permission', async () => {
    await setup(false);
    expect(query('scan')).toBeNull();
    expect(query('link-all-exact')).toBeNull();
    expect(query('save-decisions')).toBeNull();
    expect(query('mark-complete')).toBeNull();
    component.linkAllExact();
    component.setDecision(component.candidates[0], 'Skip');
    expect(component.pendingChanges).toBe(0);
  });
});
