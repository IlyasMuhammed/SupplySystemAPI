import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { AuthService } from '../../service/auth.service';
import {
  OnboardingChecklistModel, OnboardingDemoService, OnboardingStepModel, isStepDone
} from '../../../services/onboarding-demo.service';

/**
 * A50 tasks 313–317 (D-28): the dashboard's "Get started" checklist. Progress "3 of 8 done", each open step with a
 * "Do this now" link, a manual tick, and "I'll handle this myself" (dismiss, permanent).
 *
 * Shown only to ONBOARDING_VIEW holders (no call otherwise), and only while the checklist is neither dismissed nor
 * complete. While loading, and when the call fails (403, 404, 5xx), it renders nothing: the dashboard must not flash a
 * checklist at a legacy org whose checklist is dismissed, nor show an error for an optional widget (REV50-4).
 */
@Component({
  selector: 'app-onboarding-checklist-widget',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule],
  template: `
    @if (allowed && !loading && !failed) {
      @if (visible) {
        <section class="sf-card ocl" data-testid="onboarding-checklist">
          <div class="sf-ch">
            <h2><i class="pi pi-flag"></i> Get started</h2>
            <span class="ocl-progress-text" data-testid="checklist-progress">{{ done }} of {{ total }} done</span>
          </div>
          <div class="ocl-track" role="progressbar" [attr.aria-valuenow]="done" aria-valuemin="0" [attr.aria-valuemax]="total"
               [attr.aria-label]="done + ' of ' + total + ' steps done'">
            <div class="ocl-fill" [style.width.%]="percent"></div>
          </div>
          <ul class="ocl-steps">
            @for (s of steps; track s.stepCode) {
              <li class="ocl-step" [class.done]="isDone(s)" [attr.data-testid]="'step-' + s.stepCode">
                <i class="pi" [class.pi-check-circle]="isDone(s)" [class.pi-circle]="!isDone(s)" aria-hidden="true"></i>
                <div class="ocl-text">
                  @if (!isDone(s) && s.link) {
                    <a class="ocl-title" [routerLink]="s.link">{{ s.title }}</a>
                  } @else {
                    <span class="ocl-title">{{ s.title }}</span>
                  }
                  @if (s.description) { <span class="ocl-desc">{{ s.description }}</span> }
                </div>
                @if (!isDone(s)) {
                  <span class="ocl-acts">
                    @if (s.link) {
                      <a class="ocl-do" [routerLink]="s.link" [attr.data-testid]="'do-' + s.stepCode">Do this now <i class="pi pi-arrow-right"></i></a>
                    }
                    <button type="button" class="ocl-tick" [disabled]="busy" (click)="complete(s)"
                            [attr.aria-label]="'Mark ' + s.title + ' as done'" [attr.data-testid]="'tick-' + s.stepCode">Mark done</button>
                  </span>
                }
              </li>
            }
          </ul>
          <div class="ocl-foot">
            <p-button label="I'll handle this myself" [text]="true" size="small" severity="secondary"
                      [disabled]="busy" (onClick)="dismiss()" data-testid="checklist-dismiss"></p-button>
          </div>
        </section>
      }
    }
  `,
  styles: [`
    :host { display: block; }
    .ocl { display: flex; flex-direction: column; gap: .75rem; }
    .ocl-err { flex-direction: row; align-items: center; justify-content: space-between; flex-wrap: wrap; color: var(--sms-danger); font-size: .86rem; font-weight: 600; }
    .ocl-mu { color: var(--sms-text-muted); font-size: .86rem; }
    .ocl-progress-text { font-size: .82rem; font-weight: 700; color: var(--sms-primary-text); white-space: nowrap; }
    .ocl-track { height: 8px; background: var(--sms-surface-2); border-radius: 4px; overflow: hidden; }
    .ocl-fill { height: 100%; background: var(--sms-ok); border-radius: 4px; transition: width .5s ease; }
    .ocl-steps { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; }
    .ocl-step { display: flex; align-items: center; gap: .75rem; padding: .6rem .25rem; border-bottom: 1px solid var(--sms-border-soft); flex-wrap: wrap; }
    .ocl-step:last-child { border-bottom: none; }
    .ocl-step > i { font-size: 1rem; color: var(--sms-text-faint); }
    .ocl-step.done > i { color: var(--sms-ok); }
    .ocl-text { flex: 1 1 200px; min-width: 0; display: flex; flex-direction: column; gap: .1rem; }
    .ocl-title { font-size: .875rem; font-weight: 600; color: var(--sms-text); text-decoration: none; }
    a.ocl-title:hover { color: var(--sms-primary-text); text-decoration: underline; }
    .ocl-step.done .ocl-title { color: var(--sms-text-muted); text-decoration: line-through; }
    .ocl-desc { font-size: .75rem; color: var(--sms-text-faint); }
    .ocl-acts { display: inline-flex; align-items: center; gap: .75rem; }
    .ocl-do { font-size: .8rem; font-weight: 600; color: var(--sms-primary-text); text-decoration: none; white-space: nowrap; }
    .ocl-do:hover { text-decoration: underline; }
    .ocl-do i { font-size: .7rem; }
    .ocl-tick { border: 1px solid var(--sms-border); background: var(--sms-surface); color: var(--sms-text-muted); border-radius: 999px;
                font: inherit; font-size: .72rem; padding: .15rem .6rem; cursor: pointer; }
    .ocl-tick:hover:not(:disabled) { background: var(--sms-row-hover); color: var(--sms-text); }
    .ocl-tick:disabled { opacity: .6; cursor: progress; }
    .ocl-foot { display: flex; justify-content: flex-end; }
    @media (max-width: 640px) { .ocl-acts { width: 100%; padding-left: 1.75rem; } }
  `]
})
export class OnboardingChecklistWidgetComponent implements OnInit {
  private readonly api = inject(OnboardingDemoService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router, { optional: true });

  allowed = false;
  loading = false;
  failed = false;
  busy = false;
  checklist: OnboardingChecklistModel | null = null;

  ngOnInit(): void {
    this.allowed = this.auth.hasPermission('ONBOARDING_VIEW');
    if (this.allowed) this.load();
  }

  load(): void {
    this.loading = true;
    this.failed = false;
    this.api.getChecklist().subscribe({
      next: res => { this.loading = false; this.checklist = res?.result ?? null; this.failed = !this.checklist; },
      // 403 = not for this user after all; 404 = an API without the onboarding module. Neither is worth an error line.
      // REV50-4: 403, 404 (no onboarding module) or any failure renders nothing, and never a toast on the dashboard.
      error: () => {
        this.loading = false;
        this.checklist = null;
        this.failed = true;
      }
    });
  }

  get steps(): OnboardingStepModel[] {
    return [...(this.checklist?.steps ?? [])].sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0));
  }

  get total(): number { return this.checklist?.totalCount ?? this.steps.length; }
  get done(): number { return this.checklist?.completedCount ?? this.steps.filter(isStepDone).length; }
  get percent(): number { return this.total > 0 ? Math.round(this.done / this.total * 100) : 0; }

  get complete_(): boolean {
    const c = this.checklist;
    if (!c) return false;
    if (c.isComplete != null) return c.isComplete;
    return this.total > 0 && this.done >= this.total;
  }

  /** Task 317: only when not dismissed and not all complete. */
  get visible(): boolean { return !!this.checklist && !this.checklist.dismissed && !this.complete_ && this.steps.length > 0; }

  isDone(s: OnboardingStepModel): boolean { return isStepDone(s); }

  complete(s: OnboardingStepModel): void {
    if (this.busy) return;
    this.busy = true;
    this.api.completeStep(s.stepCode).subscribe({
      next: res => { this.busy = false; if (res?.result) this.checklist = res.result; else this.load(); },
      error: () => { this.busy = false; this.load(); }
    });
  }

  dismiss(): void {
    if (this.busy || !this.checklist) return;
    this.busy = true;
    this.api.dismissChecklist().subscribe({
      next: res => {
        this.busy = false;
        this.checklist = res?.result ?? { ...this.checklist!, dismissed: true };
      },
      error: () => { this.busy = false; }
    });
  }
}
