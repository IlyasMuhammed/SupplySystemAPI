import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { PoDocumentTemplateComponent } from './po-document-template.component';
import { PoDocumentTemplateService, PoDocumentTemplateModel } from '../../services/po-document-template.service';

const BANK = 'Habib Bank Ltd, Korangi Branch\nAccount title: Sunrise Electricals\nIBAN: PK36HABB0000123456702568';

function template(overrides: Partial<PoDocumentTemplateModel> = {}): PoDocumentTemplateModel {
  return {
    id: 't-1', companyName: 'Sunrise Electricals', showSignatureBlock: true, footerText: 'Thank you',
    bodyHtml: '<p>Dear Sir</p>', bankDetails: BANK,
    ...overrides
  };
}

describe('PoDocumentTemplateComponent — bank details', () => {
  let fixture: ComponentFixture<PoDocumentTemplateComponent>;
  let component: PoDocumentTemplateComponent;
  let service: jasmine.SpyObj<PoDocumentTemplateService>;

  function create(loaded: PoDocumentTemplateModel | null) {
    service.get.and.returnValue(of({ success: true, message: '', result: loaded } as any));
    fixture = TestBed.createComponent(PoDocumentTemplateComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();   // ngOnInit -> load(); the spy answers synchronously
    fixture.detectChanges();   // the form renders once loading is over
  }

  const textarea = () => fixture.nativeElement.querySelector('textarea[formControlName="bankDetails"]') as HTMLTextAreaElement;

  beforeEach(async () => {
    service = jasmine.createSpyObj<PoDocumentTemplateService>('PoDocumentTemplateService', ['get', 'upsert', 'getTokens']);
    service.getTokens.and.returnValue(of({ success: true, message: '', result: [] } as any));
    service.upsert.and.returnValue(of({ success: true, message: '', result: 't-1' } as any));

    await TestBed.configureTestingModule({
      imports: [PoDocumentTemplateComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
                  { provide: PoDocumentTemplateService, useValue: service }]
    }).compileComponents();
  });

  it('shows the saved bank details in their own section, line breaks kept', fakeAsync(() => {
    create(template());
    tick();

    expect(fixture.nativeElement.textContent).toContain('Bank Details');
    expect(fixture.nativeElement.textContent).toContain('sales invoices');
    expect(textarea()).toBeTruthy();
    expect(textarea().value).toBe(BANK);
    expect(component.form.value.bankDetails).toBe(BANK);
  }));

  it('starts empty when nothing has been configured, or the template has none', fakeAsync(() => {
    create(null);
    tick();
    expect(textarea().value).toBe('');

    create(template({ bankDetails: undefined }));
    tick();
    expect(textarea().value).toBe('');
  }));

  it('counts characters against the 1,000 the server accepts', fakeAsync(() => {
    create(template({ bankDetails: 'HBL 0123' }));
    tick();

    expect(fixture.nativeElement.textContent).toContain('8 / 1000');
    expect(textarea().getAttribute('maxlength')).toBe('1000');
  }));

  it('sends the bank details with the rest of the template when saved', fakeAsync(() => {
    create(template({ bankDetails: undefined }));
    tick();

    component.form.patchValue({ bankDetails: BANK });
    component.save();

    expect(service.upsert).toHaveBeenCalledTimes(1);
    const sent = service.upsert.calls.mostRecent().args[0];
    expect(sent.bankDetails).toBe(BANK);
    expect(sent.companyName).toBe('Sunrise Electricals');
  }));

  it('clears them when the box is emptied, by sending them blank', fakeAsync(() => {
    create(template());
    tick();

    component.form.patchValue({ bankDetails: '' });
    component.save();

    expect(service.upsert.calls.mostRecent().args[0].bankDetails).toBe('');
  }));

  it('will not save more than the server will accept', fakeAsync(() => {
    create(template());
    tick();

    component.form.patchValue({ bankDetails: 'x'.repeat(1001) });
    component.save();

    expect(component.form.controls['bankDetails'].hasError('maxlength')).toBeTrue();
    expect(service.upsert).not.toHaveBeenCalled();
  }));

  it('accepts exactly a thousand characters', fakeAsync(() => {
    create(template());
    tick();

    component.form.patchValue({ bankDetails: 'x'.repeat(1000) });
    component.save();

    expect(service.upsert).toHaveBeenCalled();
  }));
});
