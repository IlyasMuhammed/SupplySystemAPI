import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { ProofOfDeliveryComponent } from './proof-of-delivery.component';
import {
  LogisticsService, ProofCoverageModel, ProofGapModel, DeliveryProofModel
} from '../../../../services/logistics.service';

function gap(overrides: Partial<ProofGapModel> = {}): ProofGapModel {
  return {
    consignmentUuid: 'con-1', consignmentNumber: 'SHP-2026-00042',
    carrierName: 'Beta Road', masterAwb: 'AWB-1',
    deliveredAt: '2026-09-10T09:00:00Z',
    gap: 'Delivered with no proof of any kind.',
    ...overrides
  };
}

function coverage(overrides: Partial<ProofCoverageModel> = {}): ProofCoverageModel {
  return {
    delivered: 10, withProof: 7, defensible: 6, withoutProof: 3, weak: 1,
    gaps: [gap()], warnings: [],
    ...overrides
  };
}

function proof(overrides: Partial<DeliveryProofModel> = {}): DeliveryProofModel {
  return {
    uuid: 'proof-1', consignmentUuid: 'con-1', consignmentNumber: 'SHP-2026-00042',
    consignmentStatus: 'DELIVERED', masterAwb: 'AWB-1', carrierName: 'Beta Road',
    receivedBy: 'R. Ahmed', deliveredAt: '2026-09-10T09:00:00Z',
    source: 'MANUAL', files: [], isDefensible: false,
    createdDate: '2026-09-10T09:05:00Z', warnings: [],
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

function file(name: string, type: string, size: number): File {
  const f = new File(['x'], name, { type });
  Object.defineProperty(f, 'size', { value: size });
  return f;
}

describe('ProofOfDeliveryComponent', () => {
  let fixture: ComponentFixture<ProofOfDeliveryComponent>;
  let component: ProofOfDeliveryComponent;
  let api: jasmine.SpyObj<LogisticsService>;

  async function setup(
    totals: ProofCoverageModel = coverage(),
    proofs: DeliveryProofModel[] = [proof()]) {

    api = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getProofCoverage', 'getProofsForConsignment', 'recordProof',
      'attachProofFile', 'removeProofFile', 'downloadProofFile'
    ]);

    api.getProofCoverage.and.returnValue(ok(totals));
    api.getProofsForConsignment.and.returnValue(ok(proofs));
    api.recordProof.and.returnValue(ok('proof-1'));
    api.attachProofFile.and.returnValue(ok('file-1'));
    api.removeProofFile.and.returnValue(of({ success: true, message: '' } as any));
    api.downloadProofFile.and.returnValue(of(new Blob(['x'])));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ProofOfDeliveryComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ProofOfDeliveryComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function text(testId: string): string {
    const el = fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
    return el ? el.textContent.trim() : '';
  }

  // ── Loading ─────────────────────────────────────────────────────────────────

  it('loads coverage on open', async () => {
    await setup();

    expect(api.getProofCoverage).toHaveBeenCalled();
    expect(component.coverage?.delivered).toBe(10);
  });

  it('leads with what cannot be demonstrated, not with the successes', async () => {
    await setup();

    expect(text('tile-none')).toBe('3');
    expect(text('tile-weak')).toBe('1');
    expect(component.coverage!.gaps.length).toBe(1);
  });

  it('works out coverage from what was delivered, and says nothing when nothing was', async () => {
    await setup();
    expect(component.defensiblePercent).toBe(60);

    await setup(coverage({ delivered: 0, defensible: 0, withProof: 0, withoutProof: 0, weak: 0, gaps: [] }));
    expect(component.defensiblePercent)
      .withContext('a percentage of nothing is not zero').toBeNull();
  });

  it('survives coverage that will not load', async () => {
    api = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getProofCoverage', 'getProofsForConsignment', 'recordProof',
      'attachProofFile', 'removeProofFile', 'downloadProofFile'
    ]);
    api.getProofCoverage.and.returnValue(throwError(() => ({ error: { message: 'No.' } })));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ProofOfDeliveryComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ProofOfDeliveryComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    expect(component.coverage).toBeNull();
    expect(component.isLoading).withContext('the spinner must not spin forever').toBeFalse();
  });

  // ── Telling the two kinds of gap apart ──────────────────────────────────────

  it('separates nothing on file from something thin, and offers the right action for each', async () => {
    await setup();

    const nothing = gap();
    const thin    = gap({ gap: 'A name, but no signature, photograph or document on file.' });

    expect(component.needsRecording(nothing)).toBeTrue();
    expect(component.gapTag(nothing)).toBe('danger');
    expect(component.gapLabel(nothing)).toBe('Nothing at all');

    expect(component.needsRecording(thin)).toBeFalse();
    expect(component.gapTag(thin)).toBe('warn');
    expect(component.gapLabel(thin)).toBe('Not enough');
  });

  // ── Recording a handover ────────────────────────────────────────────────────

  it('refuses to record a proof naming nobody', async () => {
    await setup();

    component.openCapture(gap());
    component.form.receivedBy = '   ';

    expect(component.canCapture).toBeFalse();
    expect(component.captureError).toContain('proof of nothing');

    component.capture();

    expect(api.recordProof).not.toHaveBeenCalled();
  });

  it('refuses a delivery that would have happened in the future', async () => {
    await setup();

    component.openCapture(gap());
    component.form.receivedBy = 'R. Ahmed';
    component.form.deliveredAt = new Date(Date.now() + 86_400_000);

    expect(component.captureError).toContain('in the future');
    expect(component.canCapture).toBeFalse();
  });

  it('pre-fills when the system thinks it arrived rather than today', async () => {
    await setup();

    component.openCapture(gap({ deliveredAt: '2026-09-10T09:00:00Z' }));

    expect(component.form.deliveredAt?.toISOString()).toBe('2026-09-10T09:00:00.000Z');
  });

  it('records the handover and goes straight on to the artefacts', async () => {
    await setup();

    component.openCapture(gap());
    component.form.receivedBy = '  R. Ahmed  ';
    component.form.relationship = 'security guard';
    component.capture();

    expect(api.recordProof).toHaveBeenCalledWith('con-1', jasmine.objectContaining({
      receivedBy: 'R. Ahmed', relationship: 'security guard'
    }));

    expect(component.captureVisible).toBeFalse();
    expect(component.detailVisible)
      .withContext('a name on its own is recorded and is not evidence').toBeTrue();
    expect(api.getProofsForConsignment).toHaveBeenCalledWith('con-1');
  });

  // ── The artefacts ───────────────────────────────────────────────────────────

  it('refuses a file larger than the server would take, before sending it', async () => {
    await setup();
    component.openDetail(gap());

    const event = { target: { files: [file('huge.png', 'image/png', 40 * 1024 * 1024)], value: '' } } as any;
    component.onFileSelected(event, component.proofs[0]);

    expect(component.uploadError).toContain('limit is 10 MB');
    expect(api.attachProofFile).not.toHaveBeenCalled();
  });

  it('refuses a kind of file that cannot be stored as proof', async () => {
    await setup();
    component.openDetail(gap());

    const event = { target: { files: [file('page.html', 'text/html', 500)], value: '' } } as any;
    component.onFileSelected(event, component.proofs[0]);

    expect(component.uploadError).toContain('cannot be stored as proof');
    expect(api.attachProofFile).not.toHaveBeenCalled();
  });

  it('refuses an empty file', async () => {
    await setup();
    component.openDetail(gap());

    const event = { target: { files: [file('nothing.png', 'image/png', 0)], value: '' } } as any;
    component.onFileSelected(event, component.proofs[0]);

    expect(component.uploadError).toContain('empty');
    expect(api.attachProofFile).not.toHaveBeenCalled();
  });

  it('uploads an acceptable file under the chosen kind and refreshes coverage', async () => {
    await setup();
    component.openDetail(gap());
    component.uploadKind = 'PHOTO';

    const png = file('door.png', 'image/png', 4096);
    component.onFileSelected({ target: { files: [png], value: '' } } as any, component.proofs[0]);

    expect(component.uploadError).toBeNull();
    expect(api.attachProofFile).toHaveBeenCalledWith('proof-1', 'PHOTO', png);
    expect(api.getProofCoverage).toHaveBeenCalledTimes(2);
  });

  it('clears the file input either way, so the same file can be chosen again after a fix', async () => {
    await setup();
    component.openDetail(gap());

    const input = { files: [file('huge.png', 'image/png', 40 * 1024 * 1024)], value: 'C:\\fake\\huge.png' };
    component.onFileSelected({ target: input } as any, component.proofs[0]);

    expect(input.value).toBe('');
  });

  it('shows what the server said when an upload is refused', async () => {
    await setup();
    api.attachProofFile.and.returnValue(
      throwError(() => ({ error: { message: 'The upload says it is image/png, and the content is not.' } })));

    component.openDetail(gap());
    component.onFileSelected(
      { target: { files: [file('fake.png', 'image/png', 512)], value: '' } } as any,
      component.proofs[0]);

    expect(component.uploadError).toContain('the content is not');
    expect(component.isSubmitting).toBeFalse();
  });

  it('removes an artefact uploaded in error and reloads', async () => {
    await setup();
    component.openDetail(gap());

    component.removeFile('file-1');

    expect(api.removeProofFile).toHaveBeenCalledWith('file-1');
    expect(api.getProofsForConsignment).toHaveBeenCalledTimes(2);
  });

  it('fetches an artefact with the session rather than linking to it', async () => {
    await setup();
    spyOn(URL, 'createObjectURL').and.returnValue('blob:x');
    spyOn(URL, 'revokeObjectURL');

    component.openFile('file-1', 'sig.png');

    expect(api.downloadProofFile)
      .withContext('a bare href would be an unauthenticated request for evidence')
      .toHaveBeenCalledWith('file-1');
  });

  // ── Reading ─────────────────────────────────────────────────────────────────

  it('says what a proof is made of in words rather than codes', async () => {
    await setup();

    expect(component.kindLabel('DOCUMENT')).toBe('Delivery note');
    expect(component.sourceLabel('CARRIER')).toContain('carrier reported');
    expect(component.sizeLabel(4096)).toBe('4 KB');
    expect(component.sizeLabel(3 * 1024 * 1024)).toBe('3.0 MB');
  });

  it('shows the warnings the server put on a thin proof', async () => {
    await setup(coverage(), [proof({
      receivedBy: undefined, isDefensible: false,
      warnings: ['The carrier reported a delivery and named nobody, and there is nothing on file.']
    })]);

    component.openDetail(gap());
    fixture.detectChanges();

    expect(text('proof-warning-proof-1')).toContain('named nobody');
  });
});
