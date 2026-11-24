import { ComponentFixture, TestBed, discardPeriodicTasks, fakeAsync, flush, tick } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { OrganizationsListComponent } from './organizations-list.component';
import {
  CreateOrganizationResult, OrgDefaultsCategoryResult, OrgDefaultsStatus, OrganizationListItemModel, OrganizationsService,
  setupStatusLabel, setupStatusSeverity
} from '../../../services/organizations.service';
import { CountriesService } from '../../../services/countries.service';
import { CurrenciesService } from '../../../services/currencies.service';
import { UserService } from '../../../services/user.service';
import { FeaturesService } from '../../../services/features.service';
import { a51Providers } from './organizations-list.testing';

/** A51 P7 - background default-data apply (polling), invitation e-mail result, list columns and the read-only facts of the edit form. */
describe('OrganizationsListComponent - A51 P7', () => {
  let fixture: ComponentFixture<OrganizationsListComponent>;
  let component: OrganizationsListComponent;
  let service: jasmine.SpyObj<OrganizationsService>;

  const q = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const cat = (category: string, status: string, over: Partial<OrgDefaultsCategoryResult> = {}): OrgDefaultsCategoryResult =>
    ({ category, status, ok: status === 'Applied', created: status === 'Applied' ? 3 : 0, skipped: 0, packLevel: 'Country', ...over });
  const status = (s: OrgDefaultsStatus['status'], categories: OrgDefaultsCategoryResult[], over: Partial<OrgDefaultsStatus> = {}): OrgDefaultsStatus =>
    ({ organizationId: 'org-9', status: s, setupStatus: s === 'Completed' ? 'Completed' : 'InProgress', packLevel: 'Country', fallbackUsed: true, categories, ...over });
  const running = (): CreateOrganizationResult => ({
    organizationId: 'org-9', adminUserId: 5, plan: 'BASIC', country: 'Pakistan', countryCode: 'PK', industryType: 'TRADING', timeZone: 'Asia/Karachi',
    defaults: { status: 'Running', attempted: true, completed: false, setupStatus: 'NotStarted', packLevel: 'None', fallbackUsed: false, categories: [] }
  });

  async function setup(createResult: CreateOrganizationResult = running(), orgs: OrganizationListItemModel[] = []) {
    service = jasmine.createSpyObj<OrganizationsService>('OrganizationsService',
      ['getList', 'getById', 'update', 'create', 'getOrgUsers', 'updateAdmin', 'resendInvitation', 'getDefaultsStatus']);
    service.getList.and.returnValue(of({ success: true, message: '', result: { data: orgs, totalRecords: orgs.length, page: 1, pageSize: 100, totalPages: 1 } }));
    service.create.and.returnValue(of({ success: true, message: '', result: createResult }));
    const countries = jasmine.createSpyObj<CountriesService>('CountriesService', ['getAllCountries']);
    countries.getAllCountries.and.returnValue(of({ success: true, message: '', result: [] } as any));
    const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(of({ success: true, message: '', result: [] }));
    const features = jasmine.createSpyObj<FeaturesService>('FeaturesService', ['getCatalog', 'getPlanTemplates']);
    features.getCatalog.and.returnValue(of({ success: true, message: '', result: [] }));
    features.getPlanTemplates.and.returnValue(of({ success: true, message: '', result: [] }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [OrganizationsListComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: OrganizationsService, useValue: service },
        { provide: UserService, useValue: jasmine.createSpyObj<UserService>('UserService', ['setUserPassword']) },
        { provide: CountriesService, useValue: countries },
        { provide: CurrenciesService, useValue: currencies },
        { provide: FeaturesService, useValue: features },
        ...a51Providers()
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(OrganizationsListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function create() {
    component.openNew();
    component.form.patchValue({ orgCode: 'ACME', orgName: 'Acme', adminFirstName: 'Jane', adminEmail: 'jane@acme.test' });
    component.save();
    component.confirmCreate();
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  // ── polling ─────────────────────────────────────────────────────────────────

  it('shows "Applying default data…" without a result table while the apply runs, then polls every 3 s until it completes', async () => {
    await setup();
    fakeAsync(() => {
        service.getDefaultsStatus.and.returnValues(
          of({ success: true, message: '', result: status('Running', [cat('CURRENCY', 'Pending'), cat('TAX', 'Pending')]) }),
          of({ success: true, message: '', result: status('Running', [cat('CURRENCY', 'Applied'), cat('TAX', 'Pending')]) }),
          of({ success: true, message: '', result: status('Completed', [cat('CURRENCY', 'Applied'), cat('TAX', 'Applied')]) })
        );
        create();

        expect(component.showResultDialog).toBeTrue();
        expect(q('res-status')?.textContent).toContain('Applying default data');
        expect(q('res-running')).not.toBeNull();
        expect(q('res-categories')).toBeNull();
        expect(service.getDefaultsStatus).not.toHaveBeenCalled();

        tick(OrganizationsListComponent.POLL_FIRST_MS);
        fixture.detectChanges();
        expect(service.getDefaultsStatus).toHaveBeenCalledOnceWith('org-9');
        expect(q('chk-CURRENCY')).not.toBeNull();
        expect(q('chk-TAX')).not.toBeNull();
        expect(component.checklistIcon(component.defaultsView!.categories[0], 0)).toContain('spinner');
        expect(component.checklistIcon(component.defaultsView!.categories[1], 1)).toBe('pi pi-circle');

        tick(OrganizationsListComponent.POLL_MS);
        fixture.detectChanges();
        expect(q('chk-CURRENCY')?.classList).toContain('done');
        expect(component.checklistIcon(component.defaultsView!.categories[0], 0)).toBe('pi pi-check-circle');
        expect(component.checklistIcon(component.defaultsView!.categories[1], 1)).toContain('spinner');

        tick(OrganizationsListComponent.POLL_MS);
        fixture.detectChanges();
        expect(q('res-status')?.textContent).toContain('Setup complete');
        expect(q('res-running')).toBeNull();
        expect(q('res-categories')).not.toBeNull();
        expect(q('res-TAX')?.textContent).toContain('Applied');
        expect(q('res-problems')).toBeNull();

        // Terminal: no more reads, and the list was reloaded so its Setup column is current.
        const calls = service.getDefaultsStatus.calls.count();
        tick(OrganizationsListComponent.POLL_MS * 4);
        expect(service.getDefaultsStatus.calls.count()).toBe(calls);
        expect(service.getList.calls.count()).toBeGreaterThanOrEqual(3);
      flush();
    })();
  });

  it('ends in the same summary table with the problem note when a category failed in the background', async () => {
    await setup();
    fakeAsync(() => {
        service.getDefaultsStatus.and.returnValue(of({ success: true, message: '', result: status('InProgress',
          [cat('CURRENCY', 'Applied'), cat('TAX', 'Failed', { ok: false, message: 'boom' }), cat('UOM', 'NotRun', { ok: false })],
          { message: 'Default data was applied with problems (TAX, UOM).' }) }));
        create();
        tick(OrganizationsListComponent.POLL_FIRST_MS);
        fixture.detectChanges();

        expect(q('res-status')?.textContent).toContain('Needs attention');
        expect(q('res-TAX')?.textContent).toContain('Failed');
        expect(q('res-TAX')?.textContent).toContain('boom');
        expect(component.categoryProblems.map(c => c.category)).toEqual(['TAX', 'UOM']);
        expect(q('res-problems')).not.toBeNull();
        expect(q('res-message')?.textContent).toContain('problems');
      flush();
    })();
  });

  it('shows a failed apply as "Could not apply" with the server message', async () => {
    await setup();
    fakeAsync(() => {
        service.getDefaultsStatus.and.returnValue(of({ success: true, message: '', result: status('Failed', [cat('CURRENCY', 'Pending')], { message: 'The default data could not be applied (down).' }) }));
        create();
        tick(OrganizationsListComponent.POLL_FIRST_MS);
        fixture.detectChanges();
        expect(q('res-status')?.textContent).toContain('Could not apply');
        expect(q('res-failed')?.textContent).toContain('could not be applied');
      flush();
    })();
  });

  it('closing the dialog stops the polling', async () => {
    await setup();
    fakeAsync(() => {
        service.getDefaultsStatus.and.returnValue(of({ success: true, message: '', result: status('Running', [cat('CURRENCY', 'Pending')]) }));
        create();
        tick(OrganizationsListComponent.POLL_FIRST_MS);
        expect(service.getDefaultsStatus).toHaveBeenCalledTimes(1);

        component.closeResult();
        tick(OrganizationsListComponent.POLL_MS * 5);

        expect(service.getDefaultsStatus).toHaveBeenCalledTimes(1);
        expect(component.showResultDialog).toBeFalse();
      flush();
    })();
  });

  it('destroying the component stops the polling too', async () => {
    await setup();
    fakeAsync(() => {
        service.getDefaultsStatus.and.returnValue(of({ success: true, message: '', result: status('Running', [cat('CURRENCY', 'Pending')]) }));
        create();
        tick(OrganizationsListComponent.POLL_FIRST_MS);
        fixture.destroy();
        tick(OrganizationsListComponent.POLL_MS * 5);
        expect(service.getDefaultsStatus).toHaveBeenCalledTimes(1);
      flush();
    })();
  });

  it('gives up after five failed reads in a row and says the apply is still running', async () => {
    await setup();
    fakeAsync(() => {
        service.getDefaultsStatus.and.returnValue(throwError(() => ({ status: 500 })));
        create();
        tick(OrganizationsListComponent.POLL_FIRST_MS + OrganizationsListComponent.POLL_MS * 5);
        fixture.detectChanges();

        expect(service.getDefaultsStatus).toHaveBeenCalledTimes(OrganizationsListComponent.POLL_MAX_FAILURES);
        expect(component.pollStopped).toBeTrue();
        expect(q('res-poll-stopped')).not.toBeNull();
        tick(OrganizationsListComponent.POLL_MS * 5);
        expect(service.getDefaultsStatus).toHaveBeenCalledTimes(OrganizationsListComponent.POLL_MAX_FAILURES);
      flush();
    })();
  });

  it('a single failed read is survived (the next read still counts)', async () => {
    await setup();
    fakeAsync(() => {
        service.getDefaultsStatus.and.returnValues(
          throwError(() => ({ status: 502 })),
          of({ success: true, message: '', result: status('Completed', [cat('CURRENCY', 'Applied')]) }));
        create();
        tick(OrganizationsListComponent.POLL_FIRST_MS + OrganizationsListComponent.POLL_MS);
        fixture.detectChanges();
        expect(q('res-status')?.textContent).toContain('Setup complete');
        expect(component.pollStopped).toBeFalse();
      flush();
    })();
  });

  it('does not poll when no country was chosen (no defaults) and an answer without a status still shows the old summary', async () => {
    await setup({
      organizationId: 'org-9', adminUserId: 5, country: 'Pakistan', timeZone: 'Asia/Karachi', plan: 'BASIC',
      defaults: { attempted: true, completed: true, setupStatus: 'Completed', packLevel: 'Exact', fallbackUsed: false, categories: [cat('TAX', 'Applied')] }
    });
    fakeAsync(() => {
        create();
        tick(OrganizationsListComponent.POLL_MS * 3);
        fixture.detectChanges();
        expect(service.getDefaultsStatus).not.toHaveBeenCalled();
        expect(q('res-status')?.textContent).toContain('Setup complete');
        expect(q('res-TAX')).not.toBeNull();
      flush();
    })();
  });

  // ── skipped features + invitation ────────────────────────────────────────────

  it('says which plan features were not licensed because they are Coming Soon', async () => {
    await setup({
      ...running(), defaults: null,
      skippedFeatures: [{ featureCode: 'MODULE_POS', featureName: 'Point of Sale', reason: 'ComingSoon' }, { featureCode: 'MODULE_HRM', featureName: 'Human Resources', reason: 'ComingSoon' }]
    });
    fakeAsync(() => {
        create();
        expect(component.showResultDialog).toBeTrue();
        expect(q('res-skipped')?.textContent).toContain('Not licensed because Coming Soon');
        expect(q('res-skipped')?.textContent).toContain('Point of Sale, Human Resources');
      flush();
    })();
  });

  it('shows "Invitation email sent" and never a link when the mail went out', async () => {
    await setup({ ...running(), defaults: null, invitation: { adminEmail: 'jane@acme.test', emailSent: true, emailConfigured: true } });
    fakeAsync(() => {
        create();
        expect(q('res-invite-ok')?.textContent).toContain('Invitation email sent');
        expect(q('res-invite-failed')).toBeNull();
        expect(q('res-invite-link')).toBeNull();
        expect(q('res-invite-copy')).toBeNull();
      flush();
    })();
  });

  it('shows "could NOT be sent - copy invite link" with the link and a copy button when the send failed', fakeAsync(() => {
    const link = 'https://app.test/auth/accept-invite?token=abc123';
    setup({ ...running(), defaults: null, invitation: { adminEmail: 'jane@acme.test', emailSent: false, emailConfigured: false, error: 'E-mail is not configured.', inviteLink: link } }).then(() => {
      create();
      expect(q('res-invite-failed')?.textContent).toContain('could NOT be sent');
      expect(q('res-invite-error')?.textContent).toContain('not configured');
      expect((q('res-invite-link') as HTMLInputElement).value).toBe(link);

      const write = jasmine.createSpy('writeText').and.returnValue(Promise.resolve());
      spyOnProperty(navigator, 'clipboard', 'get').and.returnValue({ writeText: write } as unknown as Clipboard);
      component.copyInviteLink(link);
      expect(write).toHaveBeenCalledOnceWith(link);
    });
    flush();
  }));

  // ── resend invitation (org users dialog) ─────────────────────────────────────

  describe('resend invitation', () => {
    const admin = () => ({ userId: 7, firstName: 'Ada', lastName: 'L', email: 'ada@acme.test', roleId: 10, roleName: 'Org Admin', isActive: false, invitePending: true, inviteExpiresAt: '2099-01-01T00:00:00Z' });
    const ORG: OrganizationListItemModel = { id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, createdDate: '' };

    async function open() {
      await setup(running(), [ORG]);
      service.getOrgUsers.and.returnValue(of({ success: true, message: '', result: [admin()] }));
      component.manageAdmin(ORG);
      fixture.detectChanges();
    }

    it('shows "Invitation email sent" and no link when it went out', async () => {
      await open();
      service.resendInvitation.and.returnValue(of({ success: true, message: 'sent', result: { emailSent: true, emailConfigured: true, user: admin() } }));
      component.sendReinvite();
      fixture.detectChanges();

      expect(q('resend-sent')?.textContent).toContain('Invitation email sent');
      expect(q('resend-failed')).toBeNull();
      expect(q('resend-link')).toBeNull();
    });

    it('shows the failure and the copyable link only when the send failed', async () => {
      await open();
      service.resendInvitation.and.returnValue(of({ success: true, message: 'x', result: {
        emailSent: false, emailConfigured: true, error: 'SMTP send failed: refused', inviteLink: 'https://app.test/auth/accept-invite?token=zzz', user: admin() } }));
      component.sendReinvite();
      fixture.detectChanges();

      expect(q('resend-failed')?.textContent).toContain('could NOT be sent');
      expect(q('resend-error')?.textContent).toContain('refused');
      expect((q('resend-link') as HTMLInputElement).value).toContain('token=zzz');
      expect(q('resend-copy')).not.toBeNull();
    });

    it('forgets the last result when the dialog is opened again', async () => {
      await open();
      service.resendInvitation.and.returnValue(of({ success: true, message: 'x', result: { emailSent: false, emailConfigured: false, inviteLink: 'https://x/y?token=1', user: admin() } }));
      component.sendReinvite();
      expect(component.resendResult).not.toBeNull();

      component.manageAdmin(ORG);
      expect(component.resendResult).toBeNull();
    });
  });

  // ── list columns + edit facts ────────────────────────────────────────────────

  it('lists country, industry and the setup status badge per organization (dashes without a setup row)', async () => {
    await setup(running(), [
      { id: 'o1', orgCode: 'PKTR', orgName: 'Alpha', plan: 'BASIC', isActive: true, createdDate: '', countryCode: 'PK', industryType: 'TRADING', setupStatus: 'Completed' },
      { id: 'o2', orgCode: 'PLAIN', orgName: 'Beta', plan: 'BASIC', isActive: true, createdDate: '', setupStatus: 'NotStarted' },
      { id: 'o3', orgCode: 'NONE', orgName: 'Gamma', plan: 'BASIC', isActive: true, createdDate: '' }
    ]);
    fixture.detectChanges();

    expect(q('country-PKTR')?.textContent).toContain('PK');
    expect(q('industry-PKTR')?.textContent).toContain('TRADING');
    expect(q('setup-PKTR')?.textContent).toContain('Completed');
    expect(q('setup-PLAIN')?.textContent).toContain('Not started');
    expect(q('country-PLAIN')?.textContent).toContain('—');
    expect(q('setup-NONE')?.textContent).toContain('—');
  });

  it('maps every setup status to a label and a badge colour', () => {
    expect(['Completed', 'InProgress', 'NotStarted', 'Skipped', null].map(setupStatusLabel)).toEqual(['Completed', 'In progress', 'Not started', 'Skipped', '—']);
    expect(setupStatusSeverity('Completed')).toBe('success');
    expect(setupStatusSeverity('InProgress')).toBe('info');
    expect(setupStatusSeverity('NotStarted')).toBe('warn');
    expect(setupStatusSeverity('Skipped')).toBe('secondary');
  });

  it('the edit form shows the industry and setup status read-only with the hint, and still sends only the editable fields', async () => {
    await setup();
    service.getById.and.returnValue(of({ success: true, message: '', result: {
      id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, country: 'Pakistan', timeZone: 'Asia/Karachi', createdBy: 1, createdDate: '',
      countryCode: 'PK', industryType: 'TRADING', setupStatus: 'InProgress'
    } }));
    service.update.and.returnValue(of({ success: true, message: 'ok', result: null }));

    component.openEdit({ id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, createdDate: '' });
    fixture.detectChanges();

    expect(q('edit-industry')?.textContent).toContain('TRADING');
    expect(q('edit-setup')?.textContent).toContain('In progress');
    expect(q('edit-defaults-hint')?.textContent).toContain('Changing country or industry later does not re-run default data');
    component.form.patchValue({ country: 'United Arab Emirates', timeZone: 'Asia/Dubai' });
    component.save();

    const body = service.update.calls.mostRecent().args[1];
    expect(body.country).toBe('United Arab Emirates');
    expect(body.timeZone).toBe('Asia/Dubai');
    expect('industryType' in body).toBeFalse();
    expect('countryCode' in body).toBeFalse();
    expect('setupStatus' in body).toBeFalse();
  });

  it('the create form does not show the read-only facts', async () => {
    await setup();
    component.openNew();
    fixture.detectChanges();
    expect(q('edit-industry')).toBeNull();
    expect(q('edit-setup')).toBeNull();
  });
});

// keep the timer helper imported for the fakeAsync cleanup in case a spec leaves a periodic timer behind
void discardPeriodicTasks;
