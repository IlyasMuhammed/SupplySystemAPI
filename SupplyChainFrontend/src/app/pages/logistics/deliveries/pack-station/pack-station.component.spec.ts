import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { PackStationComponent } from './pack-station.component';
import {
  LogisticsService,
  DeliveryPackingModel,
  PackageModel
} from '../../../../services/logistics.service';

const UUID = '11111111-1111-1111-1111-111111111111';

function pkg(overrides: Partial<PackageModel> = {}): PackageModel {
  return {
    uuid: 'p1',
    packageBarcode: 'HU-2026-00001',
    packageType: 'BOX',
    deliveryUuid: UUID,
    deliveryNumber: 'DLV-2026-00001',
    childPackageBarcodes: [],
    isVoided: false,
    createdDate: '2026-09-01T00:00:00Z',
    contents: [
      {
        uuid: 'c1', deliveryLineUuid: 'l1', deliveryLineNo: 1,
        itemDescription: '4mm cable', unitOfMeasure: 'M', qty: 60
      }
    ],
    ...overrides
  };
}

function packing(overrides: Partial<DeliveryPackingModel> = {}): DeliveryPackingModel {
  return {
    deliveryUuid: UUID,
    deliveryNumber: 'DLV-2026-00001',
    status: 'PICKED',
    isFullyPacked: false,
    qtyPicked: 100,
    qtyPacked: 0,
    qtyUnpacked: 100,
    totalGrossWeightKg: 0,
    lines: [
      {
        deliveryLineUuid: 'l1', lineNo: 1, itemDescription: '4mm cable', unitOfMeasure: 'M',
        qtyPicked: 100, qtyPacked: 0, qtyToPack: 100
      }
    ],
    packages: [],
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('PackStationComponent', () => {
  let fixture: ComponentFixture<PackStationComponent>;
  let component: PackStationComponent;
  let service: jasmine.SpyObj<LogisticsService>;

  async function setup(model: DeliveryPackingModel | null = packing()) {
    service = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getDeliveryPackages', 'packDelivery', 'voidPackage', 'stageDelivery',
      'goodsIssueDelivery', 'downloadPackingList', 'downloadGatePass'
    ]);

    service.getDeliveryPackages.and.returnValue(
      model ? ok(model) : of({ success: false, message: 'not found', result: null } as any));
    service.packDelivery.and.returnValue(ok('new-package-uuid'));
    service.voidPackage.and.returnValue(of({ success: true, message: '' } as any));
    service.stageDelivery.and.returnValue(of({ success: true, message: '' } as any));
    service.goodsIssueDelivery.and.returnValue(ok({
      deliveryUuid: UUID, deliveryNumber: 'DLV-2026-00001', status: 'GOODS_ISSUED',
      postedStock: true, movementsPosted: 1, reservationsClosed: 1,
      qtyShipped: 100, qtyOut: 100, qtyIn: 0
    }));
    service.downloadPackingList.and.returnValue(of(new Blob(['%PDF-'])));
    service.downloadGatePass.and.returnValue(of(new Blob(['%PDF-'])));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PackStationComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: service },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(PackStationComponent);
    component = fixture.componentInstance;
  }

  // ── Loading ───────────────────────────────────────────────────────────────

  it('loads the delivery once on init', async () => {
    await setup();
    fixture.detectChanges();

    expect(service.getDeliveryPackages).toHaveBeenCalledOnceWith(UUID);
    expect(component.isLoading).toBeFalse();
    expect(component.packing?.deliveryNumber).toBe('DLV-2026-00001');
  });

  it('shows not found for a 404 but not for a failed request', async () => {
    await setup();
    service.getDeliveryPackages.and.returnValue(throwError(() => ({ status: 404 })));
    fixture.detectChanges();
    expect(component.notFound).toBeTrue();

    // A dropped connection is not a deleted record — saying "not found" would send someone
    // hunting for something that is still there.
    await setup();
    service.getDeliveryPackages.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();
    expect(component.notFound).toBeFalse();
  });

  // ── What the dock may do ──────────────────────────────────────────────────

  it('offers packing only while the goods are off the shelf and still here', async () => {
    for (const [status, expected] of [
      ['PICKED', true], ['PACKED', true],
      ['RELEASED', false], ['STAGED', false], ['GOODS_ISSUED', false]
    ] as [string, boolean][]) {
      await setup(packing({ status }));
      fixture.detectChanges();
      expect(component.canPack).toBe(expected, `canPack for ${status}`);
    }
  });

  it('offers staging only when everything picked is boxed', async () => {
    await setup(packing({ status: 'PICKED' }));
    fixture.detectChanges();
    expect(component.canStage).toBeFalse();

    await setup(packing({ status: 'PACKED', isFullyPacked: true }));
    fixture.detectChanges();
    expect(component.canStage).toBeTrue();
  });

  it('offers issuing only at the dock', async () => {
    for (const [status, expected] of [
      ['STAGED', true], ['PENDING_APPROVAL', true],
      ['PACKED', false], ['GOODS_ISSUED', false]
    ] as [string, boolean][]) {
      await setup(packing({ status }));
      fixture.detectChanges();
      expect(component.canIssue).toBe(expected, `canIssue for ${status}`);
    }
  });

  it('enables the packing list only once there is a carton to describe', async () => {
    await setup();
    fixture.detectChanges();
    expect(component.canPrintPackingList).toBeFalse();

    await setup(packing({ packages: [pkg()] }));
    fixture.detectChanges();
    expect(component.canPrintPackingList).toBeTrue();
  });

  it('enables the gate pass only once the goods are staged or past it', async () => {
    for (const [status, expected] of [
      ['PACKED', false], ['STAGED', true], ['GOODS_ISSUED', true], ['DELIVERED', true]
    ] as [string, boolean][]) {
      await setup(packing({ status, packages: [pkg()] }));
      fixture.detectChanges();
      expect(component.canPrintGatePass).toBe(expected, `gate pass for ${status}`);
    }
  });

  it('separates live cartons from voided ones', async () => {
    await setup(packing({
      packages: [pkg(), pkg({ uuid: 'p2', packageBarcode: 'HU-2', isVoided: true, voidReason: 'Crushed' })]
    }));
    fixture.detectChanges();

    expect(component.livePackages.length).toBe(1);
    expect(component.voidedPackages.length).toBe(1);
    expect(component.voidedPackages[0].voidReason).toBe('Crushed');
  });

  // ── Building a carton ─────────────────────────────────────────────────────

  it('offers only lines with something still on the floor, pre-filled with the balance', async () => {
    await setup(packing({
      qtyPacked: 40, qtyUnpacked: 60,
      lines: [
        { deliveryLineUuid: 'l1', lineNo: 1, itemDescription: 'Cable', qtyPicked: 100, qtyPacked: 40, qtyToPack: 60 },
        { deliveryLineUuid: 'l2', lineNo: 2, itemDescription: 'Boxes', qtyPicked: 20, qtyPacked: 20, qtyToPack: 0 }
      ]
    }));
    fixture.detectChanges();

    component.openPackDialog();

    // A row for a fully boxed line is a field whose only valid value is zero.
    expect(component.draftLines.length).toBe(1);
    expect(component.draftLines[0].lineNo).toBe(1);
    expect(component.draftLines[0].available).toBe(60);
    expect(component.draftLines[0].qty).toBe(60);
  });

  it('will not pack an empty carton', async () => {
    await setup();
    fixture.detectChanges();
    component.openPackDialog();

    component.draftLines[0].qty = 0;

    expect(component.draftContents.length).toBe(0);
    expect(component.canConfirmPack).toBeFalse();
  });

  it('refuses more than is picked and unpacked, and says which line', async () => {
    await setup();
    fixture.detectChanges();
    component.openPackDialog();

    component.draftLines[0].qty = 140;

    expect(component.canConfirmPack).toBeFalse();
    expect(component.overPackedLine?.lineNo).toBe(1);
  });

  it('sends only the fields the packer filled in', async () => {
    await setup();
    fixture.detectChanges();
    component.openPackDialog();

    component.draft.packageType   = 'CRATE';
    component.draft.grossWeightKg = 22.5;
    component.draft.packageBarcode = '  HU-PREPRINTED  ';
    component.draftLines[0].qty = 60;
    component.draftLines[0].batchNumber = ' B-1 ';

    component.confirmPack();

    const [uuid, req] = service.packDelivery.calls.mostRecent().args;
    expect(uuid).toBe(UUID);
    expect(req.packageType).toBe('CRATE');
    expect(req.grossWeightKg).toBe(22.5);
    expect(req.packageBarcode).toBe('HU-PREPRINTED');
    // Blank optional fields are omitted rather than sent as empty strings, which the server
    // would otherwise have to treat as a supplied value.
    expect(req.sealNumber).toBeUndefined();
    expect(req.lengthCm).toBeUndefined();
    expect(req.contents).toEqual([
      { deliveryLineUuid: 'l1', qty: 60, batchNumber: 'B-1' }
    ]);
  });

  it('reloads after packing rather than patching its own state', async () => {
    // The server decides the resulting status and what may happen next; guessing here is how the
    // screen and the API drift apart.
    await setup();
    fixture.detectChanges();
    component.openPackDialog();
    component.confirmPack();

    expect(service.getDeliveryPackages).toHaveBeenCalledTimes(2);
    expect(component.packDialogVisible).toBeFalse();
  });

  it('surfaces the servers explanation when packing is refused', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.packDelivery.and.returnValue(
      throwError(() => ({ error: { message: 'Line 1 (Cable): only 40 is picked and unpacked.' } })));

    component.openPackDialog();
    component.confirmPack();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error',
      detail: 'Line 1 (Cable): only 40 is picked and unpacked.'
    }));
    expect(component.isSubmitting).toBeFalse();
  });

  it('offers only top-level packages as pallets to load onto', async () => {
    await setup(packing({
      packages: [
        pkg({ uuid: 'pallet', packageBarcode: 'HU-PALLET', packageType: 'PALLET' }),
        pkg({ uuid: 'carton', packageBarcode: 'HU-CARTON', parentPackageUuid: 'pallet' })
      ]
    }));
    fixture.detectChanges();

    const values = component.palletOptions.map(o => o.value);

    // Nesting is one level deep, which is what the server enforces too.
    expect(values).toContain(null);
    expect(values).toContain('pallet');
    expect(values).not.toContain('carton');
  });

  // ── Voiding ───────────────────────────────────────────────────────────────

  it('requires a reason to void', async () => {
    await setup(packing({ packages: [pkg()] }));
    fixture.detectChanges();

    component.openVoidDialog(component.livePackages[0]);
    component.voidReason = '   ';
    component.confirmVoid();

    expect(service.voidPackage).not.toHaveBeenCalled();
  });

  it('voids with a trimmed reason and reloads', async () => {
    await setup(packing({ packages: [pkg()] }));
    fixture.detectChanges();

    component.openVoidDialog(component.livePackages[0]);
    component.voidReason = '  Carton split open  ';
    component.confirmVoid();

    expect(service.voidPackage).toHaveBeenCalledWith('p1', { reason: 'Carton split open' });
    expect(service.getDeliveryPackages).toHaveBeenCalledTimes(2);
  });

  // ── Staging and issuing ───────────────────────────────────────────────────

  it('stages and reloads', async () => {
    await setup(packing({ status: 'PACKED', isFullyPacked: true }));
    fixture.detectChanges();

    component.stage();

    expect(service.stageDelivery).toHaveBeenCalledWith(UUID);
    expect(service.getDeliveryPackages).toHaveBeenCalledTimes(2);
  });

  it('reports what the goods issue actually posted', async () => {
    await setup(packing({ status: 'STAGED', packages: [pkg()] }));
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    component.confirmIssue();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'success',
      detail: 'Stock posted: 100 issued.'
    }));
    expect(component.issueDialogVisible).toBeFalse();
  });

  it('says so when the source document had already posted the movement', async () => {
    // The distinction matters to whoever reconciles the ledger, so the screen must not flatten
    // it into a generic success.
    await setup(packing({ status: 'STAGED', packages: [pkg()] }));
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.goodsIssueDelivery.and.returnValue(ok({
      deliveryUuid: UUID, deliveryNumber: 'DLV-2026-00001', status: 'GOODS_ISSUED',
      postedStock: false, movementsPosted: 0, reservationsClosed: 1,
      qtyShipped: 100, qtyOut: 0, qtyIn: 0,
      note: 'MIV already posted this movement, so the delivery records it rather than posting it again.'
    }));

    component.confirmIssue();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      detail: 'MIV already posted this movement, so the delivery records it rather than posting it again.'
    }));
  });

  // ── Documents ─────────────────────────────────────────────────────────────

  it('downloads the packing list and releases the object URL', async () => {
    await setup(packing({ packages: [pkg()] }));
    fixture.detectChanges();

    spyOn(URL, 'createObjectURL').and.returnValue('blob:test');
    const revoke = spyOn(URL, 'revokeObjectURL');
    spyOn(document, 'createElement').and.returnValue({ click: () => {} } as any);

    component.downloadPackingList();

    expect(service.downloadPackingList).toHaveBeenCalledWith(UUID);
    // Left behind, an object URL holds the whole PDF in memory for the life of the tab.
    expect(revoke).toHaveBeenCalledWith('blob:test');
  });

  it('surfaces a refused gate pass rather than downloading nothing', async () => {
    await setup(packing({ status: 'PACKED', packages: [pkg()] }));
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.downloadGatePass.and.returnValue(
      throwError(() => ({ error: { message: 'A gate pass is issued once the goods are staged.' } })));

    component.downloadGatePass();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error',
      detail: 'A gate pass is issued once the goods are staged.'
    }));
  });

  // ── Display ───────────────────────────────────────────────────────────────

  it('renders dimensions only when all three are known', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.dimensions(pkg({ lengthCm: 100, widthCm: 50, heightCm: 40 })))
      .toBe('100 × 50 × 40 cm');
    expect(component.dimensions(pkg({ lengthCm: 100, widthCm: 50 }))).toBe('—');
  });

  it('turns persisted codes into something a human reads', async () => {
    await setup();
    expect(component.titleCase('PENDING_APPROVAL')).toBe('Pending Approval');
    expect(component.titleCase(undefined)).toBe('');
  });
});
