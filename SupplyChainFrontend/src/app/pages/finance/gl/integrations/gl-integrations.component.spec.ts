import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { GlIntegrationsComponent } from './gl-integrations.component';
import { AccountingService, ExternalMapping } from '../../../../services/accounting.service';
import { AuthService } from '../../../service/auth.service';
import { fakeApi, fakeAuth, okRes } from '../gl.testing';

const mapping = (over: Partial<ExternalMapping> = {}): ExternalMapping => ({
  uuid: 'm1', systemType: 'QUICKBOOKS_ONLINE', systemName: 'Head office', account: { uuid: 'a1', accountCode: '1112', accountName: 'Meezan Bank' },
  externalAccountCode: '35', externalAccountName: 'Checking', syncDirection: 'Outbound', isActive: true, syncStatus: null, ...over
});

describe('GlIntegrationsComponent (A42 external mappings)', () => {
  let fixture: ComponentFixture<GlIntegrationsComponent>;
  let c: GlIntegrationsComponent;
  let api: jasmine.SpyObj<AccountingService>;

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(perms = ['GL_VIEW', 'GL_SETUP_MANAGE']) {
    api = fakeApi();
    api.getMappings.and.returnValue(okRes([mapping(), mapping({ uuid: 'm2', account: { uuid: 'a2', accountCode: '1111', accountName: 'Cash' }, syncStatus: 'Error', syncErrorMessage: 'Not found' })]));
    api.getPostableAccounts.and.returnValue(of([{ uuid: 'a3', accountCode: '1200', accountName: 'Debtors' } as any]));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlIntegrationsComponent],
      providers: [provideNoopAnimations(), MessageService, { provide: AccountingService, useValue: api }, { provide: AuthService, useValue: fakeAuth(perms) }]
    }).compileComponents();
    fixture = TestBed.createComponent(GlIntegrationsComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('lists the mappings of the chosen system sorted by SMS code, with sync status and the connector note', async () => {
    await setup();
    expect(api.getMappings).toHaveBeenCalledWith('QUICKBOOKS_ONLINE');
    expect(c.mappings.map(m => m.account.accountCode)).toEqual(['1111', '1112']);
    expect(q('map-1112')!.querySelector('[data-testid="sync"]')!.textContent).toContain('Never');
    expect(q('map-1111')!.querySelector('[data-testid="sync"]')!.textContent).toContain('Error');
    expect(q('connector-note')!.textContent).toContain('Connectors are configured in a future addendum.');
    expect(c.systemName).toBe('Head office');
    c.systemType = 'XERO';
    c.load();
    expect(api.getMappings).toHaveBeenCalledWith('XERO');
  });

  it('direction changes inline (PUT) and goes back on a refusal', async () => {
    await setup();
    const m = c.mappings[1];
    api.updateMapping.and.returnValue(okRes({ ...m, syncDirection: 'Bidirectional' as const }));
    c.setDirection(m, 'Bidirectional');
    expect(api.updateMapping).toHaveBeenCalledWith('m1', jasmine.objectContaining({ syncDirection: 'Bidirectional', externalAccountCode: '35', isActive: true }));
    expect(m.syncDirection).toBe('Bidirectional');
    api.updateMapping.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { message: 'Mapped account must be active.' } })));
    c.setDirection(m, 'None');
    expect(m.syncDirection).toBe('Bidirectional');
  });

  it('Add mapping: checks name, account, code and duplicates, then posts', async () => {
    await setup();
    c.openNew();
    c.save();
    expect(c.shownProblem).toBe('Choose the SMS account.');
    c.draft.accountUuid = 'a1';
    c.draft.externalAccountCode = '99';
    expect(c.problem).toBe('This account is already mapped for QuickBooks Online.');
    c.draft.accountUuid = 'a3';
    api.createMapping.and.returnValue(okRes(mapping({ uuid: 'm3' })));
    c.save();
    expect(api.createMapping).toHaveBeenCalledWith(jasmine.objectContaining({ systemType: 'QUICKBOOKS_ONLINE', systemName: 'Head office', accountUuid: 'a3', externalAccountCode: '99', syncDirection: 'Outbound' }));
    expect(c.dialogVisible).toBeFalse();
  });

  it('Import CSV shows created / updated / errors', async () => {
    await setup();
    api.importMappings.and.returnValue(okRes({ created: 4, updated: 1, errors: [{ line: 7, message: 'Only Level 4 accounts can be mapped.' }] }));
    const input = { files: [new File(['x'], 'map.csv')], value: 'x' } as unknown as HTMLInputElement;
    c.onFile({ target: input } as unknown as Event);
    fixture.detectChanges();
    expect(api.importMappings).toHaveBeenCalledWith(jasmine.any(File), 'QUICKBOOKS_ONLINE', 'Head office');
    expect(q('imp-created')!.textContent).toBe('4');
    expect(q('imp-updated')!.textContent).toBe('1');
    expect(q('imp-error')!.textContent).toContain('Line 7: Only Level 4 accounts can be mapped.');
  });

  it('Import refuses a non-CSV file', async () => {
    await setup();
    const input = { files: [new File(['x'], 'map.xlsx')], value: 'x' } as unknown as HTMLInputElement;
    c.onFile({ target: input } as unknown as Event);
    expect(api.importMappings).not.toHaveBeenCalled();
  });

  it('read-only: no Add / Import, direction disabled', async () => {
    await setup(['GL_VIEW']);
    expect(q('add-mapping')).toBeNull();
    expect(q('import')).toBeNull();
    expect(q('export')).not.toBeNull();
  });
});
