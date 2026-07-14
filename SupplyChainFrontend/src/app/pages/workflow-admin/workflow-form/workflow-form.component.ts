import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, ActivatedRoute, Router } from '@angular/router';
import {
  FormBuilder, FormGroup, FormArray, Validators, ReactiveFormsModule, AbstractControl
} from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { CheckboxModule } from 'primeng/checkbox';
import { DropdownModule } from 'primeng/dropdown';
import { ToastModule } from 'primeng/toast';
import { DividerModule } from 'primeng/divider';
import { TooltipModule } from 'primeng/tooltip';
import { InputNumberModule } from 'primeng/inputnumber';
import { TagModule } from 'primeng/tag';
import { MessageService } from 'primeng/api';
import {
  WorkflowService,
  WorkflowDefinitionDetailModel,
  ApproverOptionsDto,
  ConditionFieldDto,
  InterfaceSummaryDto,
  AddWorkflowStepRequest
} from '../../../services/workflow.service';

@Component({
  selector: 'app-workflow-form',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule,
    ButtonModule, InputTextModule, TextareaModule,
    CheckboxModule, DropdownModule, ToastModule,
    DividerModule, TooltipModule, InputNumberModule, TagModule
  ],
  templateUrl: './workflow-form.component.html',
  styleUrls: ['./workflow-form.component.scss'],
  providers: [MessageService]
})
export class WorkflowFormComponent implements OnInit {
  form!: FormGroup;
  isEditMode   = false;
  editId       = '';
  isSaving        = false;
  isSavingInPlace = false;
  isLoading       = false;
  existingDef: WorkflowDefinitionDetailModel | null = null;

  // Dropdown option lists
  interfaceOptions:  { label: string; value: string }[] = [];
  conditionFields:   { label: string; value: string }[] = [];
  approverOptions: ApproverOptionsDto = { users: [], roles: [], groups: [] };

  stepTypeOptions = [
    { label: 'Approval',     value: 'APPROVAL' },
    { label: 'Notification', value: 'NOTIFICATION' },
    { label: 'Auto-Approve', value: 'AUTO_APPROVE' }
  ];
  approverTypeOptions = [
    { label: 'Role',       value: 'ROLE' },
    { label: 'Specific User', value: 'USER' },
    { label: 'Direct Manager', value: 'MANAGER' },
    { label: 'Any Approver', value: 'ANY' },
    { label: 'Group',      value: 'GROUP' },
    { label: 'Committee',  value: 'COMMITTEE' }
  ];
  approvalModeOptions = [
    { label: 'Any One',  value: 'ANY_ONE' },
    { label: 'All',      value: 'ALL' },
    { label: 'Majority', value: 'MAJORITY' }
  ];
  operatorOptions = [
    { label: 'Greater Than',          value: 'GT' },
    { label: 'Greater Than or Equal', value: 'GTE' },
    { label: 'Less Than',             value: 'LT' },
    { label: 'Less Than or Equal',    value: 'LTE' },
    { label: 'Equal To',              value: 'EQ' },
    { label: 'Between',               value: 'BETWEEN' }
  ];

  constructor(
    private fb: FormBuilder,
    private route: ActivatedRoute,
    private router: Router,
    private wfService: WorkflowService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.buildForm();
    this.loadApproverOptions();
    this.loadInterfaceOptions();

    this.editId = this.route.snapshot.paramMap.get('id') ?? '';
    this.isEditMode = !!this.editId;

    if (this.isEditMode) {
      this.isLoading = true;
      this.wfService.getDefinitionById(this.editId).subscribe({
        next: res => {
          this.isLoading = false;
          if (res.success && res.result) {
            this.existingDef = res.result;
            this.patchForm(res.result);
            this.loadConditionFields(res.result.interfaceCode);
          }
        },
        error: () => {
          this.isLoading = false;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load workflow definition.' });
        }
      });
    }
  }

  buildForm() {
    this.form = this.fb.group({
      interfaceCode:             ['', Validators.required],
      name:                      ['', Validators.required],
      description:               [''],
      requiresSequentialApproval:[true],
      allowRecall:               [true],
      allowReissue:              [true],
      slaHours:                  [48, [Validators.required, Validators.min(1)]],
      escalationAdminId:         [null],
      conditionField:            [null],
      conditionOperator:         [null],
      conditionValue:            [null],
      conditionValueMin:         [null],
      conditionValueMax:         [null],
      steps: this.fb.array([])
    });

    // Load condition fields when interfaceCode changes (create mode only)
    this.form.get('interfaceCode')!.valueChanges.subscribe(code => {
      if (code && !this.isEditMode) this.loadConditionFields(code);
    });
  }

  get stepsArray(): FormArray { return this.form.get('steps') as FormArray; }

  get isBetween(): boolean { return this.form.get('conditionOperator')?.value === 'BETWEEN'; }

  patchForm(def: WorkflowDefinitionDetailModel) {
    this.form.patchValue({
      interfaceCode:             def.interfaceCode,
      name:                      def.name,
      description:               def.description,
      requiresSequentialApproval: def.requiresSequentialApproval,
      allowRecall:               def.allowRecall,
      allowReissue:              def.allowReissue,
      slaHours:                  def.slaHours,
      escalationAdminId:         def.escalationAdminId,
      conditionField:            def.conditionField,
      conditionOperator:         def.conditionOperator,
      conditionValue:            def.conditionValue,
      conditionValueMin:         def.conditionValueMin,
      conditionValueMax:         def.conditionValueMax
    });

    // Disable interface code in edit mode
    this.form.get('interfaceCode')!.disable();

    // Load steps into FormArray
    this.stepsArray.clear();
    def.steps.forEach(s => this.stepsArray.push(this.buildStepGroup({
      stepName:         s.stepName,
      stepType:         s.stepType,
      approverType:     s.approverType,
      approverRefId:    s.approverRefId,
      approverRole:     s.approverRole,
      approverRefName:  s.approverRefName,
      isMandatory:      s.isMandatory,
      isFinalStep:      s.isFinalStep,
      slaHoursOverride: s.slaHoursOverride,
      escalationUserId: s.escalationUserId,
      skipCondition:    s.skipCondition,
      stepInstructions: s.stepInstructions,
      approvalMode:     s.approvalMode,
      canReject:        s.canReject,
      canRecall:        s.canRecall
    })));
  }

  buildStepGroup(defaults?: any): FormGroup {
    return this.fb.group({
      stepName:         [defaults?.stepName         ?? '', Validators.required],
      stepType:         [defaults?.stepType         ?? 'APPROVAL'],
      approverType:     [defaults?.approverType     ?? 'ROLE'],
      approverRefId:    [defaults?.approverRefId    ?? null],
      approverRole:     [defaults?.approverRole     ?? null],
      approverRefName:  [defaults?.approverRefName  ?? ''],
      isMandatory:      [defaults?.isMandatory      ?? true],
      isFinalStep:      [defaults?.isFinalStep      ?? false],
      slaHoursOverride: [defaults?.slaHoursOverride ?? null],
      escalationUserId: [defaults?.escalationUserId ?? null],
      skipCondition:    [defaults?.skipCondition    ?? ''],
      stepInstructions: [defaults?.stepInstructions ?? ''],
      approvalMode:     [defaults?.approvalMode     ?? 'ANY_ONE'],
      canReject:        [defaults?.canReject        ?? true],
      canRecall:        [defaults?.canRecall        ?? true]
    });
  }

  addStep() {
    this.stepsArray.push(this.buildStepGroup());
  }

  removeStep(i: number) {
    this.stepsArray.removeAt(i);
  }

  moveStep(i: number, dir: -1 | 1) {
    const j = i + dir;
    if (j < 0 || j >= this.stepsArray.length) return;
    const a = this.stepsArray.at(i).value;
    const b = this.stepsArray.at(j).value;
    this.stepsArray.at(i).setValue(b);
    this.stepsArray.at(j).setValue(a);
  }

  getRoleOptions() { return this.approverOptions.roles.map(r => ({ label: r.name,    value: r.id })); }
  getUserOptions() { return this.approverOptions.users.map(u => ({ label: `${u.fullName} (${u.email})`, value: u.id })); }
  getGroupOptions() { return this.approverOptions.groups.map(g => ({ label: g.name,  value: g.id })); }

  needsApproverRef(step: AbstractControl): boolean {
    const t = step.get('approverType')?.value;
    return ['USER', 'GROUP', 'COMMITTEE'].includes(t);
  }

  needsApproverRole(step: AbstractControl): boolean {
    return step.get('approverType')?.value === 'ROLE';
  }

  loadApproverOptions() {
    this.wfService.getApproverOptions().subscribe({
      next: res => { if (res.success && res.result) this.approverOptions = res.result; },
      error: () => {}
    });
  }

  loadInterfaceOptions() {
    this.wfService.getInterfaceSummaries().subscribe({
      next: res => {
        if (res.success && res.result) {
          this.interfaceOptions = res.result.map((s: InterfaceSummaryDto) => ({
            label: s.interfaceCode,
            value: s.interfaceCode
          }));
        }
      },
      error: () => {}
    });
  }

  loadConditionFields(code: string) {
    if (!code) return;
    this.wfService.getConditionFields(code).subscribe({
      next: res => {
        if (res.success && res.result) {
          this.conditionFields = [
            { label: '— None —', value: '' },
            ...res.result.map((f: ConditionFieldDto) => ({ label: f.label, value: f.field }))
          ];
        }
      },
      error: () => {}
    });
  }

  private buildStepPayload(): AddWorkflowStepRequest[] {
    const rawSteps = this.form.getRawValue().steps;
    const steps: AddWorkflowStepRequest[] = rawSteps.map((s: any, idx: number) => ({
      stepNumber:       idx + 1,
      stepName:         s.stepName,
      stepType:         s.stepType,
      approverType:     s.approverType,
      approverRefId:    s.approverRefId    || null,
      approverRole:     s.approverRole     || null,
      approverRefName:  s.approverRefName  || null,
      isMandatory:      s.isMandatory,
      isFinalStep:      s.isFinalStep,
      slaHoursOverride: s.slaHoursOverride || null,
      escalationUserId: s.escalationUserId || null,
      skipCondition:    s.skipCondition    || null,
      stepInstructions: s.stepInstructions || null,
      approvalMode:     s.approvalMode,
      canReject:        s.canReject,
      canRecall:        s.canRecall
    }));
    // Auto-mark the last step as final if none is explicitly checked
    if (steps.length > 0 && !steps.some(s => s.isFinalStep))
      steps[steps.length - 1].isFinalStep = true;
    return steps;
  }

  private buildUpdatePayload(steps: AddWorkflowStepRequest[]) {
    const raw = this.form.getRawValue();
    return {
      name:                       raw.name,
      description:                raw.description || undefined,
      requiresSequentialApproval: raw.requiresSequentialApproval,
      allowRecall:                raw.allowRecall,
      allowReissue:               raw.allowReissue,
      slaHours:                   raw.slaHours ?? 48,
      escalationAdminId:          raw.escalationAdminId || null,
      conditionField:             raw.conditionField    || null,
      conditionOperator:          raw.conditionOperator || null,
      conditionValue:             raw.conditionValue    ?? null,
      conditionValueMin:          raw.conditionValueMin ?? null,
      conditionValueMax:          raw.conditionValueMax ?? null,
      steps
    };
  }

  // Create mode: new workflow definition
  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    const raw   = this.form.getRawValue();
    const steps = this.buildStepPayload();

    this.isSaving = true;
    this.wfService.createDefinition({
      interfaceCode:              raw.interfaceCode,
      name:                       raw.name,
      description:                raw.description || undefined,
      requiresSequentialApproval: raw.requiresSequentialApproval,
      allowRecall:                raw.allowRecall,
      allowReissue:               raw.allowReissue,
      slaHours:                   raw.slaHours ?? 48,
      escalationAdminId:          raw.escalationAdminId || null,
      conditionField:             raw.conditionField    || null,
      conditionOperator:          raw.conditionOperator || null,
      conditionValue:             raw.conditionValue    ?? null,
      conditionValueMin:          raw.conditionValueMin ?? null,
      conditionValueMax:          raw.conditionValueMax ?? null,
      steps
    }).subscribe({
      next: res => {
        this.isSaving = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Workflow definition created.' });
          setTimeout(() => this.router.navigate(['/portal/pages/workflow-admin/workflows']), 1000);
        }
      },
      error: () => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to create workflow.' });
      }
    });
  }

  // Edit mode: create a new version (old version deactivated, new version active)
  saveNewVersion() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    this.isSaving = true;
    this.wfService.updateDefinition(this.editId, this.buildUpdatePayload(this.buildStepPayload())).subscribe({
      next: res => {
        this.isSaving = false;
        if (res.success) {
          const newUuid = res.result as string;
          this.messageService.add({ severity: 'success', summary: 'New Version', detail: 'New version created and activated.' });
          setTimeout(() => this.router.navigate(['/portal/pages/workflow-admin/workflows', newUuid, 'edit']), 1000);
        }
      },
      error: () => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to create new version.' });
      }
    });
  }

  // Edit mode: update existing version in-place (version number unchanged)
  saveInPlace() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    this.isSavingInPlace = true;
    this.wfService.updateDefinitionInPlace(this.editId, this.buildUpdatePayload(this.buildStepPayload())).subscribe({
      next: res => {
        this.isSavingInPlace = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Workflow updated successfully.' });
        }
      },
      error: () => {
        this.isSavingInPlace = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to update workflow.' });
      }
    });
  }

  cancel() {
    this.router.navigate(['/portal/pages/workflow-admin/workflows']);
  }

  get pageTitle(): string {
    return this.isEditMode
      ? `Edit Workflow${this.existingDef ? ' — ' + this.existingDef.interfaceCode + ' v' + this.existingDef.version : ''}`
      : 'New Workflow Definition';
  }
}
