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

/** The delivery as the detail endpoint reads it — only what the REV-04b rule looks at. No route: today's path. */
function legacyDetail(overrides: any = {}) {
  return { uuid: UUID, status: 'STAGED', routeSteps: [], approvedAt: null, consignments: [], ...overrides };
}

/** PICK_AND_SHIP, staged by the route (no STAGE step), not approved, on no consignment: cartons still amendable. */
function autoStagedDetail(overrides: any = {}) {
  return legacyDetail({
    fulfillmentRouteCode: 'PICK_AND_SHIP',
    routeSteps: [
      { stepCode: 'PICK', label: 'Pick', state: 'DONE' },
      { stepCode: 'GOODS_ISSUE', label: 'Goods Issue', state: 'CURRENT' },
      { stepCode: 'SHIP', label: 'Ship', state: 'PENDING' },
      { stepCode: 'COMPLETE', label: 'Complete', state: 'PENDING' }
    ],
    ...overrides
  });
}

describe('PackStationComponent', () => {
  let fixture: ComponentFixture<PackStationComponent>;
  let component: PackStationComponent;
  let service: jasmine.SpyObj<LogisticsService>;

  async function setup(model: DeliveryPackingModel | null = packing(), deliveryDetail: any = legacyDetail()) {
    service = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getDeliveryPackages', 'packDelivery', 'voidPackage', 'stageDelivery',
      'goodsIssueDelivery', 'downloadPackingList', 'downloadGatePass',
      'getDeliveryById', 'patchPackage'
    ]);
    service.getDeliveryById.and.returnValue(ok(deliveryDetail));
    service.patchPackage.and.returnValue(of({ success: true, message: '' } as any));

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

  // ── A33 REV-04b — amending the cartons of an auto-staged delivery ────────────
  //
  // The server (REV-04) keeps the cartons of a delivery its route staged automatically (no STAGE step) amendable until
  // it is approved, consigned or issued. That window is the only time PICK_AND_SHIP's auto LOOSE unit — created with no
  // weight, which a courier refuses — can be weighed.

  describe('REV-04b amending cartons', () => {
    const staged = () => packing({
      status: 'STAGED', isFullyPacked: true, qtyPacked: 100, qtyUnpacked: 0,
      packages: [pkg({ packageType: 'LOOSE', grossWeightKg: undefined })]
    });
    const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

    it('reads the delivery only when it is staged, and keeps today\'s rule for one with no route', async () => {
      await setup(packing({ status: 'PICKED' }));
      fixture.detectChanges();
      expect(service.getDeliveryById).not.toHaveBeenCalled();
      expect(component.canAmend).withContext('picked cartons are always the packer\'s').toBeTrue();

      await setup(staged(), legacyDetail());
      fixture.detectChanges();
      expect(service.getDeliveryById).toHaveBeenCalledOnceWith(UUID);
      expect(component.canAmend).toBeFalse();
      expect(q('action-void')).toBeNull();
      expect(q('action-edit-package')).toBeNull();
    });

    it('lets the packer weigh or void a carton of an auto-staged PICK_AND_SHIP delivery', async () => {
      await setup(staged(), autoStagedDetail());
      fixture.detectChanges();

      expect(component.canAmend).toBeTrue();
      expect(component.canPack).withContext('new cartons still wait for PICKED (the void steps it back)').toBeFalse();
      expect(q('action-void')).not.toBeNull();
      expect(q('action-edit-package')).not.toBeNull();
      expect(q('banner-amendable')).not.toBeNull();
    });

    it('freezes the cartons once the dispatch is approved or a live consignment carries it', async () => {
      await setup(staged(), autoStagedDetail({ approvedAt: '2026-10-03T09:00:00Z' }));
      fixture.detectChanges();
      expect(component.canAmend).withContext('approved').toBeFalse();
      expect(q('action-edit-package')).toBeNull();

      await setup(staged(), autoStagedDetail({ consignments: [{ consignmentUuid: 'cn', consignmentNumber: 'CN-1', status: 'BOOKED' }] }));
      fixture.detectChanges();
      expect(component.canAmend).withContext('consigned').toBeFalse();
    });

    it('keeps the cartons frozen if the delivery cannot be read', async () => {
      await setup(staged());
      service.getDeliveryById.and.returnValue(throwError(() => ({ status: 500 })));
      fixture.detectChanges();

      expect(component.canAmend).toBeFalse();
      expect(component.packing).withContext('the packing still shows').not.toBeNull();
    });

    it('edits a carton\'s weight and size, sending only what was filled in, then reloads', async () => {
      await setup(staged(), autoStagedDetail());
      fixture.detectChanges();
      const loose = component.livePackages[0];

      component.openEditDialog(loose);
      expect(component.editDialogVisible).toBeTrue();
      component.edit.grossWeightKg = 12.5;
      component.edit.lengthCm = 40;
      component.saveEdit();

      expect(service.patchPackage).toHaveBeenCalledOnceWith('p1', { grossWeightKg: 12.5, lengthCm: 40 });
      expect(component.editDialogVisible).toBeFalse();
      expect(service.getDeliveryPackages).toHaveBeenCalledTimes(2);
    });

    it('prefills what the carton already records, and sends nothing for an untouched form', async () => {
      await setup(staged(), autoStagedDetail());
      fixture.detectChanges();

      component.openEditDialog(pkg({ grossWeightKg: 3, sealNumber: 'S-1' }));
      expect(component.edit.grossWeightKg).toBe(3);
      expect(component.edit.sealNumber).toBe('S-1');
      expect(component.canSaveEdit).withContext('nothing changed').toBeFalse();

      component.saveEdit();
      expect(service.patchPackage).not.toHaveBeenCalled();
    });

    it('shows the server\'s reason when the edit is refused, and keeps the dialog open', async () => {
      await setup(staged(), autoStagedDetail());
      fixture.detectChanges();
      const add = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
      service.patchPackage.and.returnValue(throwError(() => ({
        status: 409, error: { message: 'Delivery DLV-2026-00001 is STAGED, so its packages can no longer be changed.' }
      })));

      component.openEditDialog(component.livePackages[0]);
      component.edit.grossWeightKg = 2;
      component.saveEdit();

      expect(add.calls.mostRecent().args[0].detail).toContain('can no longer be changed');
      expect(component.editDialogVisible).toBeTrue();
      expect(component.isSubmitting).toBeFalse();
    });

    it('will not edit when the cartons are frozen', async () => {
      await setup(staged(), legacyDetail());
      fixture.detectChanges();

      component.openEditDialog(component.livePackages[0]);
      component.edit.grossWeightKg = 2;
      component.saveEdit();

      expect(service.patchPackage).not.toHaveBeenCalled();
    });
  });
});
