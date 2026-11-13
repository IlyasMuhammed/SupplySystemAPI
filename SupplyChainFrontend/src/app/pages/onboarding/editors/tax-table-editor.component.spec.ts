import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { TaxTableEditorComponent } from './tax-table-editor.component';

/** A50 task 131 / D-13 — tax table with the unverified banner. */
describe('TaxTableEditorComponent', () => {
  let fixture: ComponentFixture<TaxTableEditorComponent>;
  let c: TaxTableEditorComponent;
  let emitted: any[];
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  async function setup(payload: any, allowVerify = false) {
    await TestBed.configureTestingModule({ imports: [TaxTableEditorComponent], providers: [provideNoopAnimations()] }).compileComponents();
    fixture = TestBed.createComponent(TaxTableEditorComponent);
    c = fixture.componentInstance;
    emitted = [];
    c.payloadChange.subscribe(p => emitted.push(p));
    fixture.componentRef.setInput('payload', payload);
    fixture.componentRef.setInput('allowVerify', allowVerify);
    fixture.detectChanges();
  }

  it('shows "verified: false — check with your tax adviser" for unverified packs', async () => {
    await setup({ verified: false, verificationNote: 'Indicative.', taxCodes: [{ code: 'GST18', name: 'GST', rate: 18, usage: 'SALES', isDefault: true }] });
    expect(q('tax-unverified').textContent).toContain('verified: false — check with your tax adviser');
    expect(q('tax-unverified').textContent).toContain('Indicative.');
    expect(q('tax-row-0')).toBeTruthy();
  });

  it('no banner when verified', async () => {
    await setup({ verified: true, taxCodes: [] });
    expect(q('tax-unverified')).toBeNull();
    expect(q('tax-empty')).toBeTruthy();
  });

  it('add / edit / remove rows emit the payload with upper-cased codes and the other fields kept', async () => {
    await setup({ verified: false, verificationNote: 'n', taxCodes: [] });
    c.add();
    c.rows[0].code = 'vat5';
    c.rows[0].name = 'VAT';
    c.rows[0].rate = 5;
    c.changed();
    let last = emitted[emitted.length - 1];
    expect(last.verified).toBeFalse();
    expect(last.verificationNote).toBe('n');
    expect(last.taxCodes[0].code).toBe('VAT5');
    expect(c.problem).toBeNull();
    c.remove(0);
    last = emitted[emitted.length - 1];
    expect(last.taxCodes).toEqual([]);
  });

  it('flags a problem (two defaults for one usage)', async () => {
    await setup({ taxCodes: [{ code: 'A', name: 'a', rate: 1, usage: 'SALES', isDefault: true }, { code: 'B', name: 'b', rate: 1, usage: 'SALES', isDefault: true }] });
    c.changed();
    fixture.detectChanges();
    expect(q('tax-problem').textContent).toContain('Only one default');
  });

  it('super admin can tick verified', async () => {
    await setup({ verified: false, taxCodes: [] }, true);
    expect(q('tax-verify')).toBeTruthy();
    c.setVerified(true);
    expect(emitted[emitted.length - 1].verified).toBeTrue();
  });
});
