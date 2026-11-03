import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { PrimeTemplate } from 'primeng/api';

/**
 * A49 — every HRM dialog. `styleClass="sdlg"` zeroes the dialog's padding and expects the full inner markup: sdlg-header with
 * an icon, sdlg-body, sdlg-footer with sdlg-footer-btns. Having it in one place means no HRM screen can leave it out.
 */
@Component({
  selector: 'hrm-dialog',
  standalone: true,
  imports: [CommonModule, ButtonModule, DialogModule, PrimeTemplate],
  template: `
<p-dialog [visible]="visible" (visibleChange)="visibleChange.emit($event)" [modal]="true" [draggable]="false" [resizable]="false"
          [style]="{ width: width, maxWidth: '96vw' }" styleClass="sdlg">
  <ng-template pTemplate="header">
    <div class="sdlg-header" data-testid="dlg-header">
      <div class="sdlg-header-icon" [ngClass]="'sdlg-icon-' + tone"><i class="pi" [ngClass]="icon"></i></div>
      <div class="sdlg-header-text">
        <div class="sdlg-title">{{ title }}</div>
        <div class="sdlg-subtitle" *ngIf="subtitle">{{ subtitle }}</div>
      </div>
    </div>
  </ng-template>
  <div class="sdlg-body" data-testid="dlg-body">
    <ng-content></ng-content>
    <div class="gl-note er" *ngIf="error" data-testid="dialog-error" role="alert" style="margin-top:0.75rem"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
  </div>
  <ng-template pTemplate="footer">
    <div class="sdlg-footer">
      <div class="sdlg-footer-btns">
        <p-button [label]="saveLabel ? 'Cancel' : 'Close'" [text]="true" severity="secondary" (onClick)="visibleChange.emit(false)" data-testid="dlg-cancel"></p-button>
        <p-button *ngIf="saveLabel" [label]="saveLabel" [icon]="saveIcon" [severity]="danger ? 'danger' : undefined" [disabled]="busy || saveDisabled" [loading]="busy"
                  (onClick)="save.emit()" data-testid="dlg-save"></p-button>
      </div>
    </div>
  </ng-template>
</p-dialog>`,
  styleUrls: ['../../finance/gl/gl.scss']
})
export class HrmDialogComponent {
  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();
  @Input() title = '';
  @Input() subtitle?: string | null;
  @Input() icon = 'pi-pencil';
  @Input() tone: 'blue' | 'green' | 'red' | 'amber' | 'purple' | 'teal' = 'blue';
  @Input() width = '34rem';
  @Input() saveLabel?: string | null = 'Save';
  @Input() saveIcon = 'pi pi-check';
  @Input() danger = false;
  @Input() busy = false;
  @Input() saveDisabled = false;
  @Input() error?: string | null;
  @Output() save = new EventEmitter<void>();
}
