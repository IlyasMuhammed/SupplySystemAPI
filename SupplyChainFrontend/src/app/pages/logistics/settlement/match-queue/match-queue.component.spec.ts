import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { MatchQueueComponent } from './match-queue.component';
import {
  LogisticsService, InvoiceLineMatchModel, CarrierListItemModel
} from '../../../../services/logistics.service';

function line(overrides: Partial<InvoiceLineMatchModel> = {}): InvoiceLineMatchModel {
  return {
    lineUuid: 'line-1', lineNo: 1, description: 'Large mystery',
    invoiceUuid: 'inv-1', invoiceNumber: 'INV-9001', carrierName: 'Beta Road',
    amount: 7000, currency: 'PKR', matchStatus: 'UNMATCHED',
    matchNote: 'Nothing this carrier moved matches the references on this line.',
    ...overrides
  };
}

function page<T>(data: T[]) {
  return of({
    success: true, message: '',
    result: { data, totalRecords: data.length, page: 1, pageSize: 20, totalPages: 1 }
  } as any);
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('MatchQueueComponent', () => {
  let fixture: ComponentFixture<MatchQueueComponent>;
  let component: MatchQueueComponent;
  let api: jasmine.SpyObj<LogisticsService>;

  async function setup(lines: InvoiceLineMatchModel[] = [line()]) {
    api = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getMatchQueue', 'getActiveCarriers'
    ]);

    api.getMatchQueue.and.returnValue(page(lines));
    api.getActiveCarriers.and.returnValue(
      ok([{ uuid: 'carrier-1', name: 'Beta Road' } as CarrierListItemModel]));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [MatchQueueComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(MatchQueueComponent);
    component = fixture.componentInstance;
  }

  it('lists what nobody could tie to a movement', async () => {
    await setup();
    fixture.detectChanges();

    expect(api.getMatchQueue).toHaveBeenCalled();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="queue-row"]').length).toBe(1);
  });

  it('shows why each line could not be matched', async () => {
    // Which kind of nothing it matched, so somebody knows where to start.
    await setup();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="queue-row"]')?.textContent)
      .toContain('Nothing this carrier moved matches');
  });

  it('says what the page is worth', async () => {
    await setup([line({ amount: 7000 }), line({ lineUuid: 'line-2', amount: 100 })]);
    fixture.detectChanges();

    expect(component.pageValue).toBe(7100);
    expect(fixture.nativeElement.querySelector('[data-testid="page-value"]')?.textContent)
      .toContain('7,100');
  });

  it('counts a credit line by what it is worth, not by its sign', async () => {
    await setup([line({ amount: -500 })]);
    fixture.detectChanges();

    expect(component.pageValue).toBe(500);
  });

  it('defaults to everything needing a person', async () => {
    await setup();
    fixture.detectChanges();

    expect(api.getMatchQueue).toHaveBeenCalledWith(jasmine.objectContaining({
      matchStatus: undefined
    }));
  });

  it('narrows by carrier and by why it is stuck', async () => {
    await setup();
    fixture.detectChanges();

    component.filter = { carrierUuid: 'carrier-1', matchStatus: 'AMBIGUOUS' };
    component.search();

    expect(api.getMatchQueue).toHaveBeenCalledWith(jasmine.objectContaining({
      carrierUuid: 'carrier-1', matchStatus: 'AMBIGUOUS', page: 1
    }));
  });

  it('distinguishes unmatched from needing a person', async () => {
    await setup();

    expect(component.matchLabel('UNMATCHED')).toBe('Unmatched');
    expect(component.matchLabel('AMBIGUOUS')).toBe('Needs a person');
    expect(component.matchSeverity('AMBIGUOUS')).toBe('warn');
  });

  it('says so when nothing is waiting', async () => {
    await setup([]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="queue-empty"]')?.textContent)
      .toContain('tied to a movement or set aside');
  });
});
