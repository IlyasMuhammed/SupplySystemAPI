import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Subject, of, throwError } from 'rxjs';

import { SalesInvoicePdfDialogComponent } from './sales-invoice-pdf-dialog.component';
import { SalesInvoiceService } from '../../../../services/sales-invoice.service';

describe('SalesInvoicePdfDialogComponent', () => {
  let fixture: ComponentFixture<SalesInvoicePdfDialogComponent>;
  let component: SalesInvoicePdfDialogComponent;
  let invoices: jasmine.SpyObj<SalesInvoiceService>;
  let create: jasmine.Spy;
  let revoke: jasmine.Spy;
  let made: number;

  async function setup() {
    invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['downloadPdf']);
    invoices.downloadPdf.and.returnValue(of(new Blob(['%PDF'], { type: 'application/pdf' })));

    made = 0;
    create = spyOn(URL, 'createObjectURL').and.callFake(() => `blob:pdf-${++made}`);
    revoke = spyOn(URL, 'revokeObjectURL');

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SalesInvoicePdfDialogComponent],
      providers: [provideNoopAnimations(), { provide: SalesInvoiceService, useValue: invoices }]
    }).compileComponents();

    fixture = TestBed.createComponent(SalesInvoicePdfDialogComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('invoiceUuid', 'inv-1');
    fixture.componentRef.setInput('invoiceNumber', 'SINV-20260920-0001');
    fixture.detectChanges();
  }

  function open() {
    fixture.componentRef.setInput('visible', true);
    fixture.detectChanges();
  }

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  it('fetches nothing while it is closed', async () => {
    await setup();

    expect(invoices.downloadPdf).not.toHaveBeenCalled();
    expect(component.url).toBeNull();
  });

  it('fetches the invoice when it opens and shows it in a frame', async () => {
    await setup();
    open();

    expect(invoices.downloadPdf).toHaveBeenCalledOnceWith('inv-1');
    expect(create).toHaveBeenCalledTimes(1);
    expect(component.url).not.toBeNull();
    expect(component.isLoading).toBeFalse();
    expect(query('pdf-frame')).not.toBeNull();
    expect(query('pdf-failed')).toBeNull();
  });

  it('shows that it is preparing the invoice until the bytes arrive', async () => {
    await setup();
    invoices.downloadPdf.and.returnValue(new Subject<Blob>());
    open();

    expect(component.isLoading).toBeTrue();
    expect(query('pdf-loading')).not.toBeNull();
    expect(query('pdf-frame')).toBeNull();
  });

  it('says so when the PDF cannot be loaded', async () => {
    await setup();
    invoices.downloadPdf.and.returnValue(throwError(() => ({ status: 500 })));
    open();

    expect(component.failed).toBeTrue();
    expect(component.isLoading).toBeFalse();
    expect(query('pdf-failed')).not.toBeNull();
    expect(query('pdf-frame')).toBeNull();
  });

  it('lets go of the bytes when it closes, and fetches afresh when it opens again', async () => {
    await setup();
    open();

    fixture.componentRef.setInput('visible', false);
    fixture.detectChanges();

    expect(revoke).toHaveBeenCalledOnceWith('blob:pdf-1');
    expect(component.url).toBeNull();

    open();
    expect(invoices.downloadPdf).withContext('an invoice may have been paid since').toHaveBeenCalledTimes(2);
    expect(create).toHaveBeenCalledTimes(2);
  });

  it('tells its owner when the user closes it, and lets go of the bytes', async () => {
    await setup();
    const changes: boolean[] = [];
    component.visibleChange.subscribe(v => changes.push(v));
    open();

    component.close();

    expect(changes).toEqual([false]);
    expect(component.visible).toBeFalse();
    expect(revoke).toHaveBeenCalledOnceWith('blob:pdf-1');
  });

  it('ignores an answer that arrives after it was closed', async () => {
    await setup();
    const pending = new Subject<Blob>();
    invoices.downloadPdf.and.returnValue(pending);
    open();

    fixture.componentRef.setInput('visible', false);
    fixture.detectChanges();
    pending.next(new Blob(['%PDF']));

    expect(create).not.toHaveBeenCalled();
    expect(component.url).toBeNull();
  });

  it('fetches the other invoice when it is pointed at one while open, releasing the first', async () => {
    await setup();
    open();

    fixture.componentRef.setInput('invoiceUuid', 'inv-2');
    fixture.detectChanges();

    expect(invoices.downloadPdf.calls.allArgs()).toEqual([['inv-1'], ['inv-2']]);
    expect(revoke).toHaveBeenCalledWith('blob:pdf-1');
  });

  it('fetches nothing when opened with no invoice', async () => {
    await setup();
    fixture.componentRef.setInput('invoiceUuid', null);
    open();

    expect(invoices.downloadPdf).not.toHaveBeenCalled();
  });

  it('downloads the PDF under the invoice number', async () => {
    await setup();
    open();
    const clicked: HTMLAnchorElement[] = [];
    spyOn(HTMLAnchorElement.prototype, 'click').and.callFake(function (this: HTMLAnchorElement) { clicked.push(this); });

    component.download();

    expect(clicked.length).toBe(1);
    expect(clicked[0].download).toBe('SINV-20260920-0001.pdf');
    expect(clicked[0].getAttribute('href')).toBe('blob:pdf-1');
  });

  it('has nothing to download until the PDF is here', async () => {
    await setup();
    const click = spyOn(HTMLAnchorElement.prototype, 'click');

    component.download();

    expect(click).not.toHaveBeenCalled();
  });

  it('lets go of the bytes when it is destroyed', async () => {
    await setup();
    open();

    fixture.destroy();

    expect(revoke).toHaveBeenCalledOnceWith('blob:pdf-1');
  });
});
