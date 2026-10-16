import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, Routes, UrlTree, provideRouter } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable, of, throwError } from 'rxjs';

import { GlPostingComponent } from './gl-posting.component';
import { FinancialPostingService, PostingDetail, PostingLogRow, TenantFinanceConfig } from '../../../../services/financial-posting.service';

// GET log/{uuid} as CORE answers it: an update (v2) reversed JE-7 and posted JE-9; the server may also send journal lines.
const DETAIL = (uuid: string): PostingDetail => ({
  ...ROWS[0], uuid, description: 'Sales invoice SINV-0001', partnerType: 'Customer',
  lines: [
    { lineNumber: 1, accountRole: 'DEFAULT_RECEIVABLE', accountCode: null, debit: 1234.5, credit: 0, label: 'Acme' },
    { lineNumber: 2, accountRole: 'DEFAULT_REVENUE', accountCode: null, debit: 0, credit: 1046.19, label: 'Sales' },
    { lineNumber: 3, accountRole: 'DEFAULT_TAX_PAYABLE', accountCode: '2210', debit: 0, credit: 188.31, label: 'GST' }
  ],
  journals: [
    { uuid: 'j7', entryNumber: 'JE-2026-00007', entryType: 'SALES_INVOICE', entryDate: '2026-10-15', status: 'Posted',
      totalDebit: 1200, totalCredit: 1200, currencyCode: 'PKR', reversesEntryNumber: null, isReversed: true },
    { uuid: 'j8', entryNumber: 'JE-2026-00008', entryType: 'SALES_INVOICE', entryDate: '2026-10-16', status: 'Posted',
      totalDebit: 1200, totalCredit: 1200, currencyCode: 'PKR', reversesEntryNumber: 'JE-2026-00007', isReversed: false },
    { uuid: 'j9', entryNumber: 'JE-2026-00009', entryType: 'SALES_INVOICE', entryDate: '2026-10-16', status: 'Draft',
      totalDebit: 1234.5, totalCredit: 1234.5, currencyCode: 'PKR', reversesEntryNumber: null, isReversed: false,
      lines: [
        { lineNumber: 1, accountCode: '1200', accountName: 'Trade debtors', memo: 'Acme', debit: 1234.5, credit: 0 },
        { lineNumber: 2, accountCode: '4100', accountName: 'Sales', description: 'Sales', debit: 0, credit: 1046.19 },
        { lineNumber: 3, accountCode: '2210', accountName: 'Output tax', debit: 0, credit: 188.31 }
      ] }
  ]
});
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { ModuleService } from '../../../../services/module.service';
import { postingFeatureGuard } from '../gl.guards';
import pageRoutes from '../../../pages.routes';

// Shapes as in API-CONTRACT "Details" (CORE).
const CONFIG: TenantFinanceConfig = {
  internalAccountingEnabled: false, autoPostToGL: true, defaultCurrencyCode: 'PKR', fiscalYearStartMonth: 1,
  glFeatureEnabled: true, setupStatus: 'Complete', glReady: true, glActive: false, notReadyReason: null
};

const ROWS: PostingLogRow[] = [
  {
    uuid: 'r1', kind: 'Post', documentType: 'SalesInvoice', sourceModule: 'finance', sourceDocumentUuid: 'inv-1', sourceDocumentNumber: 'SINV-0001',
    transactionDate: '2026-10-15', partnerName: 'Acme', currencyCode: 'PKR', totalAmount: 1234.5, version: 2, overallStatus: 'Queued',
    targets: [
      { targetCode: 'internal_gl', status: 'Posted', attempts: 1, internalId: 'JE-2026-00012', lastError: null },
      { targetCode: 'quickbooks', status: 'Queued', attempts: 0, internalId: 'inv-1', externalId: null }
    ]
  },
  {
    uuid: 'r2', kind: 'Post', documentType: 'Payment', sourceDocumentUuid: 'pay-1', sourceDocumentNumber: 'CPAY-0002', totalAmount: 500,
    overallStatus: 'DeadLetter',
    targets: [{ targetCode: 'internal_gl', status: 'DeadLetter', attempts: 5, lastError: 'PERIOD_CLOSED: 2026-10-15 is not in an open fiscal period.' }]
  }
];

describe('GlPostingComponent (A44 D-30)', () => {
  let fixture: ComponentFixture<GlPostingComponent>;
  let component: GlPostingComponent;
  let api: jasmine.SpyObj<FinancialPostingService>;
  let permissions: string[];
  let features: string[];
  let toasts: jasmine.Spy;
  let confirm: jasmine.Spy;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(opts: { config?: TenantFinanceConfig; logFails?: boolean; rows?: PostingLogRow[]; detail?: PostingDetail } = {}) {
    api = jasmine.createSpyObj<FinancialPostingService>('FinancialPostingService', ['getConfig', 'updateConfig', 'getLog', 'getSummary', 'retry', 'skip', 'getEntry']);
    api.getEntry.and.callFake(uuid => of(opts.detail ?? DETAIL(uuid)));
    api.getConfig.and.returnValue(of({ ...(opts.config ?? CONFIG) }));
    api.updateConfig.and.callFake(body => of({ ...CONFIG, ...body }));
    const rows = opts.rows ?? ROWS;
    api.getLog.and.returnValue(opts.logFails
      ? throwError(() => ({ status: 500, error: { message: 'Log unavailable.' } }))
      : of({ data: rows.map(r => ({ ...r })), totalRecords: rows.length, page: 1, pageSize: 25 }));
    api.getSummary.and.returnValue(of({ targets: [
      { targetCode: 'internal_gl', posted: 7, deadLetter: 1, counts: { Posted: 7, DeadLetter: 1 } },
      { targetCode: 'quickbooks', queued: 2, held: 4, failed: 1, counts: { Queued: 2, Held: 4, Failed: 1 } }
    ], postings: 9, needsAttention: 2 }));
    api.retry.and.callFake(uuid => of({ ...ROWS[1], uuid, targets: [{ targetCode: 'internal_gl', status: 'Pending' }] }));
    api.skip.and.callFake(uuid => of({ ...ROWS[1], uuid, targets: [{ targetCode: 'internal_gl', status: 'Skipped' }] }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlPostingComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: FinancialPostingService, useValue: api },
        { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
        { provide: TenantService, useValue: { hasFeature: (c: string) => features.includes(c), isSuperAdmin: () => false, tenant: signal(null) } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(GlPostingComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm');
    fixture.detectChanges();
  }

  beforeEach(() => {
    permissions = ['GL_VIEW', 'GL_SETUP_MANAGE'];
    features = ['FEATURE_GENERAL_LEDGER', 'MODULE_INTEGRATION'];
  });

  it('summary tiles over both targets', async () => {
    await setup();
    expect(query('tile-Posted')!.textContent).toContain('7');
    expect(query('tile-Queued')!.textContent).toContain('2');
    expect(query('tile-Held')!.textContent).toContain('4');
    expect(query('tile-Failed')!.textContent).toContain('1');
    expect(query('tile-DeadLetter')!.textContent).toContain('1');
  });

  it('a tile filters the log by its status (and again clears it)', async () => {
    await setup();
    api.getLog.calls.reset();
    query('tile-DeadLetter')!.click();
    expect(api.getLog).toHaveBeenCalledOnceWith(jasmine.objectContaining({ status: 'DeadLetter', page: 1 }));
    query('tile-DeadLetter')!.click();
    expect(api.getLog.calls.mostRecent().args[0]!.status).toBeNull();
  });

  it('the log: document, type, 2 dp amount, a chip per target, error', async () => {
    await setup();
    const r1 = query('row-r1')!;
    expect(r1.textContent).toContain('SINV-0001');
    expect(r1.textContent).toContain('Sales invoice');
    expect(r1.textContent).toContain('1,234.50');
    expect(query('chip-r1-internal_gl')!.textContent).toContain('Posted');
    expect(query('chip-r1-internal_gl')!.className).toContain('ok');
    expect(r1.textContent).toContain('JE-2026-00012');
    expect(query('chip-r1-quickbooks')!.textContent).toContain('Queued');
    expect(r1.textContent).toContain('v2');
    expect(query('chip-r2-internal_gl')!.textContent).toContain('Dead letter');
    expect(query('chip-r2-quickbooks')).toBeNull();
    expect(query('row-r2')!.textContent).toContain('PERIOD_CLOSED');
    expect(query('row-r2')!.textContent).toContain('5 attempts');
    expect(r1.querySelector('a')!.getAttribute('href')).toContain('/portal/pages/finance/sales-invoices/inv-1');
  });

  it('retry and skip only on failed / dead-letter rows; skip confirms', async () => {
    await setup();
    expect(query('retry-r1')).toBeNull();
    query('retry-r2')!.querySelector('button')!.click();
    expect(api.retry).toHaveBeenCalledOnceWith('r2');
    expect(component.rows.find(r => r.uuid === 'r2')!.targets[0].status).toBe('Pending');

    await setup();
    query('skip-r2')!.querySelector('button')!.click();
    expect(api.skip).not.toHaveBeenCalled();
    confirm.calls.mostRecent().args[0].accept();
    expect(api.skip).toHaveBeenCalledOnceWith('r2');
  });

  it('the internal GL switches save { internalAccountingEnabled, autoPostToGL }', async () => {
    await setup();
    expect(component.configDirty).toBeFalse();
    component.draft.internalAccountingEnabled = true;
    component.draft.autoPostToGL = false;
    fixture.detectChanges();
    query('save-config')!.querySelector('button')!.click();
    expect(api.updateConfig).toHaveBeenCalledOnceWith({ internalAccountingEnabled: true, autoPostToGL: false });
    expect(component.configDirty).toBeFalse();
    expect(toasts.calls.mostRecent().args[0].severity).toBe('success');
  });

  it('warns that nothing posts while GL setup is not complete', async () => {
    await setup({ config: { ...CONFIG, internalAccountingEnabled: true, setupStatus: 'DefaultsSet', glReady: false, notReadyReason: 'SETUP_INCOMPLETE' } });
    expect(query('gl-not-ready')!.textContent).toContain('not complete');
  });

  it('INTEGRATION_MANAGE alone: the log with retry / skip, no GL settings (their GET needs a GL permission)', async () => {
    permissions = ['INTEGRATION_VIEW', 'INTEGRATION_MANAGE'];
    await setup();
    expect(api.getConfig).not.toHaveBeenCalled();
    expect(query('config-card')).toBeNull();
    expect(query('retry-r2')).not.toBeNull();
  });

  it('QuickBooks only (no GL feature): no GL switches, the log still works', async () => {
    features = ['MODULE_INTEGRATION'];
    await setup({ config: { ...CONFIG, glFeatureEnabled: false, glReady: false, notReadyReason: 'FEATURE_OFF' } });
    expect(query('gl-off')).not.toBeNull();
    expect(query('enabled')).toBeNull();
    expect(query('row-r1')).not.toBeNull();
    expect(query('qbo-link')).not.toBeNull();
  });

  it('read-only for GL_VIEW: no save, retry or skip', async () => {
    permissions = ['GL_VIEW'];
    await setup();
    expect(query('save-config')).toBeNull();
    expect(query('retry-r2')).toBeNull();
    component.saveConfig();
    expect(api.updateConfig).not.toHaveBeenCalled();
  });

  it('empty and error states', async () => {
    await setup({ rows: [] });
    expect(query('log-empty')!.textContent).toContain('Nothing has been posted yet');
    await setup({ logFails: true });
    expect(query('log-error')!.textContent).toContain('Log unavailable.');
  });

  describe('journals in the posting detail (D-36)', () => {
    function open(uuid = 'r1') {
      query('open-' + uuid)!.querySelector('button')!.click();
      fixture.detectChanges();
    }

    it('opening a posting loads it and lists every journal: number, type, date, status, totals', async () => {
      await setup();
      open();
      expect(api.getEntry).toHaveBeenCalledOnceWith('r1');
      expect(component.detailVisible).toBeTrue();
      const j9 = document.querySelector('[data-testid="journal-JE-2026-00009"]') as HTMLElement;
      expect(j9.textContent).toContain('Sales invoice');
      expect(j9.textContent).toContain('16 Oct 2026');
      expect(j9.textContent).toContain('Dr 1,234.50 · Cr 1,234.50');
      expect(document.querySelector('[data-testid="jstatus-JE-2026-00009"]')!.textContent).toContain('Draft');
      expect(document.querySelector('[data-testid="jstatus-JE-2026-00007"]')!.className).toContain('ok');
    });

    it('shows what a journal reverses and what reversed it', async () => {
      await setup();
      open();
      expect(document.querySelector('[data-testid="reverses-JE-2026-00008"]')!.textContent).toContain('JE-2026-00007');
      expect(document.querySelector('[data-testid="reversed-JE-2026-00007"]')!.textContent).toContain('by JE-2026-00008');
      expect(document.querySelector('[data-testid="reversed-JE-2026-00009"]')).toBeNull();
    });

    it('a journal\'s lines: account code + name, memo, 2 dp debit / credit, a balanced total', async () => {
      await setup();
      open();
      const t = document.querySelector('[data-testid="jlines-JE-2026-00009"]') as HTMLElement;
      const rows = t.querySelectorAll('tbody tr');
      expect(rows.length).toBe(3);
      expect(rows[0].textContent).toContain('1200');
      expect(rows[0].textContent).toContain('Trade debtors');
      expect(rows[0].textContent).toContain('Acme');
      expect(rows[0].textContent).toContain('1,234.50');
      expect(rows[1].textContent).toContain('Sales');
      expect(t.querySelector('tfoot')!.textContent).toContain('balanced');
      expect(t.querySelector('tfoot')!.textContent).not.toContain('not balanced');
      expect(t.querySelector('tfoot')!.textContent).toContain('1,234.50');
      // Journals without lines from the server show none; with some lines present, the posted lines are not repeated.
      expect(document.querySelector('[data-testid="jlines-JE-2026-00007"]')).toBeNull();
      expect(document.querySelector('[data-testid="posted-lines"]')).toBeNull();
    });

    it('when the server sends no journal lines, the posting\'s own lines are shown, labelled', async () => {
      const d = DETAIL('r1');
      d.journals = d.journals.map(j => ({ ...j, lines: undefined }));
      await setup({ detail: d });
      open();
      const posted = document.querySelector('[data-testid="posted-lines"]') as HTMLElement;
      expect(posted.textContent).toContain('As the document sent them');
      expect(posted.textContent).toContain('Receivable');
      expect(posted.textContent).toContain('2210');
      expect(posted.querySelector('tfoot')!.textContent).toContain('balanced');
    });

    it('no internal GL: says so', async () => {
      const d = DETAIL('r2');
      d.journals = [];
      d.targets = [{ targetCode: 'quickbooks', status: 'Posted' }];
      await setup({ detail: d });
      open('r2');
      expect(document.querySelector('[data-testid="no-journal"]')!.textContent)
        .toContain('No general ledger journal — internal accounting is off or not set up.');
    });

    it('GL on but not posted yet: says the GL posting is pending / failed', async () => {
      const d = DETAIL('r2');
      d.journals = [];
      d.targets = [{ targetCode: 'internal_gl', status: 'Failed', lastError: 'PERIOD_CLOSED: …' }];
      await setup({ config: { ...CONFIG, internalAccountingEnabled: true, glActive: true }, detail: d });
      open('r2');
      expect(document.querySelector('[data-testid="no-journal"]')!.textContent).toContain('the posting to the general ledger is failed');
    });

    it('a GL target skipped as not ready counts as no internal GL', async () => {
      const d = DETAIL('r2');
      d.journals = [];
      d.targets = [{ targetCode: 'internal_gl', status: 'Skipped', lastError: 'GL_NOT_READY' }];
      await setup({ config: { ...CONFIG, glActive: undefined }, detail: d });
      open('r2');
      expect(document.querySelector('[data-testid="no-journal"]')!.textContent).toContain('internal accounting is off');
    });

    it('a failed load says so', async () => {
      await setup();
      api.getEntry.and.returnValue(throwError(() => ({ status: 404, error: { message: 'Posting not found.' } })));
      open();
      expect(document.querySelector('[data-testid="detail-error"]')!.textContent).toContain('Posting not found.');
    });
  });

  describe('route and guard (D-80)', () => {
    function guard(enabled: string[]): boolean | UrlTree {
      TestBed.resetTestingModule().configureTestingModule({
        providers: [
          provideRouter([]),
          { provide: TenantService, useValue: { tenant: () => ({}), isSuperAdmin: () => false, hasFeature: (c: string) => enabled.includes(c) } },
          { provide: ModuleService, useValue: { isFeatureEnabled: (c: string) => enabled.includes(c) } }
        ]
      });
      const result = TestBed.runInInjectionContext(() => postingFeatureGuard({} as any, {} as any));
      return result as boolean | UrlTree;
    }

    it('opens with the GL feature or the integration module, else access denied', () => {
      expect(guard(['FEATURE_GENERAL_LEDGER'])).toBeTrue();
      expect(guard(['MODULE_INTEGRATION'])).toBeTrue();
      const denied = guard(['MODULE_FINANCE']);
      expect(denied instanceof UrlTree).toBeTrue();
      expect(TestBed.inject(Router).serializeUrl(denied as UrlTree)).toBe('/portal/access-denied');
    });

    it('waits for the tenant when it is not loaded yet', () => {
      TestBed.resetTestingModule().configureTestingModule({
        providers: [provideRouter([]), { provide: TenantService, useValue: {
          tenant: () => null, isSuperAdmin: () => false, hasFeature: (c: string) => c === 'MODULE_INTEGRATION', loadCurrent: () => of({})
        } }, { provide: ModuleService, useValue: { isFeatureEnabled: (c: string) => c === 'MODULE_INTEGRATION' } }]
      });
      let value: unknown;
      (TestBed.runInInjectionContext(() => postingFeatureGuard({} as any, {} as any)) as Observable<unknown>).subscribe(v => value = v);
      expect(value).toBeTrue();
    });

    it('finance/gl/posting: the component, GL read permissions and the posting guard', () => {
      const route = (pageRoutes as Routes).find(r => r.path === 'finance/gl/posting')!;
      expect(route.component).toBe(GlPostingComponent);
      expect(route.canActivate).toContain(postingFeatureGuard);
    });
  });
});
