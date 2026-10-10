import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { WorkflowListComponent } from './workflow-list.component';
import { WorkflowService, WorkflowDefinitionListItemModel } from '../../../services/workflow.service';

const def = (o: Partial<WorkflowDefinitionListItemModel>): WorkflowDefinitionListItemModel => ({
  uuid: 'u', interfaceCode: 'PO', name: 'PO approval', version: 1, isActive: true, requiresSequentialApproval: true,
  allowRecall: false, allowReissue: false, slaHours: 24, escalationAdminId: null, conditionField: null,
  conditionOperator: null, conditionValue: null, conditionValueMin: null, conditionValueMax: null, stepCount: 2,
  createdDate: '2026-10-01T00:00:00Z', ...o
});

describe('WorkflowListComponent — definitions of switched-off modules (A37 D-14 / §8)', () => {
  it('greys and flags a definition whose module is off; others are unchanged', async () => {
    const wf = jasmine.createSpyObj<WorkflowService>('WorkflowService', ['getInterfaceSummaries', 'getDefinitions']);
    wf.getInterfaceSummaries.and.returnValue(of({ success: true, message: '', result: [
      { interfaceCode: 'PO', hasActiveWorkflow: true, activeWorkflowName: 'PO approval', activeVersion: 1, stepCount: 2, moduleCode: 'MODULE_DEMAND', moduleEnabled: true },
      { interfaceCode: 'DELIVERY', hasActiveWorkflow: false, activeWorkflowName: null, activeVersion: null, stepCount: null, moduleCode: 'MODULE_LOGISTICS', moduleEnabled: false }
    ] } as any));
    wf.getDefinitions.and.returnValue(of({ success: true, message: '', result: { data: [
      def({ uuid: 'a', interfaceCode: 'PO', moduleCode: 'MODULE_DEMAND', moduleEnabled: true }),
      def({ uuid: 'b', interfaceCode: 'DELIVERY', name: 'Delivery approval', moduleCode: 'MODULE_LOGISTICS', moduleEnabled: false })
    ], totalRecords: 2 } } as any));

    await TestBed.configureTestingModule({
      imports: [WorkflowListComponent],
      providers: [provideNoopAnimations(), provideRouter([]), { provide: WorkflowService, useValue: wf }]
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkflowListComponent);
    fixture.detectChanges();
    const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

    expect(q('wf-row-b')!.classList).toContain('wf-module-off');
    expect(q('wf-module-off-b')!.textContent).toContain('Module off');
    expect(q('wf-row-a')!.classList).not.toContain('wf-module-off');
    expect(q('wf-module-off-a')).toBeNull();
    expect(q('wf-tile-DELIVERY')!.classList).toContain('wf-tile-off');
    expect(q('wf-tile-DELIVERY')!.textContent).toContain('Module off');
    expect(q('wf-tile-PO')!.classList).not.toContain('wf-tile-off');
    expect(fixture.componentInstance.moduleOffTip(def({ moduleCode: 'MODULE_LOGISTICS' })))
      .toContain('The Logistics module is switched off');
    fixture.destroy();
  });
});
