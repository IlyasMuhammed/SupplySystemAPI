import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { PickListQueueComponent } from './pick-list-queue.component';
import { LogisticsService, PickListListItemModel } from '../../../../services/logistics.service';

function row(overrides: Partial<PickListListItemModel> = {}): PickListListItemModel {
  return {
    uuid: 'p1',
    pickListNumber: 'PCK-2026-00001',
    status: 'OPEN',
    deliveryUuid: 'd1',
    deliveryNumber: 'DLV-2026-00001',
    warehouseName: 'Central',
    lineCount: 3,
    qtyToPick: 100,
    qtyPicked: 0,
    generatedAt: '2026-09-01T00:00:00Z',
    ...overrides
  };
}

function page(rows: PickListListItemModel[]) {
  return of({
    success: true, message: '',
    result: { data: rows, totalRecords: rows.length, page: 1, pageSize: 20, totalPages: 1 }
  } as any);
}

describe('PickListQueueComponent', () => {
  let fixture: ComponentFixture<PickListQueueComponent>;
  let component: PickListQueueComponent;
  let service: jasmine.SpyObj<LogisticsService>;

  async function setup() {
    service = jasmine.createSpyObj<LogisticsService>('LogisticsService', ['getPickLists']);
    service.getPickLists.and.returnValue(page([row()]));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PickListQueueComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: service }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(PickListQueueComponent);
    component = fixture.componentInstance;
  }

  it('loads exactly once on init', async () => {
    // The table's [lazy] binding fires onLazyLoad as it initialises. Loading in ngOnInit as well
    // issues the same request twice on every visit — that was F25.
    await setup();
    fixture.detectChanges();

    expect(service.getPickLists).toHaveBeenCalledTimes(1);
    expect(component.isLoading).toBeFalse();
    expect(component.rows.length).toBe(1);
  });

  it('passes the filters it was given and resets to the first page', async () => {
    await setup();
    fixture.detectChanges();

    component.search = '  DLV-2026  ';
    component.status = 'IN_PROGRESS';
    component.applyFilters();

    expect(service.getPickLists).toHaveBeenCalledWith(jasmine.objectContaining({
      search: 'DLV-2026', status: 'IN_PROGRESS', page: 1
    }));
  });

  it('omits an empty search rather than sending a blank filter', async () => {
    await setup();
    fixture.detectChanges();

    component.search = '   ';
    component.applyFilters();

    expect(service.getPickLists.calls.mostRecent().args[0]?.search).toBeUndefined();
  });

  it('translates paging into page and pageSize', async () => {
    await setup();
    fixture.detectChanges();

    component.onLazyLoad({ first: 40, rows: 20 } as any);

    expect(service.getPickLists).toHaveBeenCalledWith(
      jasmine.objectContaining({ page: 3, pageSize: 20 }));
  });

  it('empties the table and says so when the request fails', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.getPickLists.and.returnValue(throwError(() => ({ status: 500 })));
    component.load();

    expect(component.rows).toEqual([]);
    expect(component.totalRecords).toBe(0);
    expect(component.isLoading).toBeFalse();
    expect(messages.add).toHaveBeenCalledWith(
      jasmine.objectContaining({ severity: 'error' }));
  });

  it('reports progress as a percentage of what there is to take', async () => {
    await setup();
    expect(component.progress(row({ qtyToPick: 100, qtyPicked: 25 }))).toBe(25);
    // A list with nothing to take is not "100% done" — it is a list with nothing to take.
    expect(component.progress(row({ qtyToPick: 0, qtyPicked: 0 }))).toBe(0);
  });

  it('renders statuses as readable text with a severity', async () => {
    await setup();
    // Each word is title-cased, matching the delivery screens: "Short Closed", "In Progress".
    expect(component.formatStatus('IN_PROGRESS')).toBe('In Progress');
    expect(component.getStatusSeverity('COMPLETED')).toBe('success');
    expect(component.getStatusSeverity('WHATEVER')).toBe('secondary');
  });
});
