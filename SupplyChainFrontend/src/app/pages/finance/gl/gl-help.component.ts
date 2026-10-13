import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ButtonModule } from 'primeng/button';

import { STEP_HELP, StepHelp } from './gl.shared';

/** A42 FSD §11.3 — the wizard's "[? Help]" toggle: What is this? / Why does it matter? / Tips / Example. */
@Component({
  selector: 'app-gl-help',
  standalone: true,
  imports: [CommonModule, ButtonModule],
  styleUrls: ['./gl.scss'],
  template: `
<p-button [label]="open ? 'Hide help' : 'Help'" icon="pi pi-question-circle" [text]="true" size="small" (onClick)="open = !open"
          [attr.aria-expanded]="open" data-testid="help-toggle"></p-button>
<div class="gl-help" *ngIf="open && help" role="note" data-testid="help-panel">
  <h3>What is this?</h3><p>{{ help.what }}</p>
  <h3>Why does it matter?</h3><p>{{ help.why }}</p>
  <h3>Tips</h3><ul><li *ngFor="let t of help.tips">{{ t }}</li></ul>
  <h3>Example</h3><p>{{ help.example }}</p>
</div>`,
  styles: [`:host { display: block; } .gl-help { margin-top: .5rem; }`]
})
export class GlHelpComponent {
  @Input() step = 1;
  @Input() open = false;
  get help(): StepHelp | undefined { return STEP_HELP[this.step]; }
}
