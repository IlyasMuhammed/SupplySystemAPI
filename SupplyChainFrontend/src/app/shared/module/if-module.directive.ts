import { Directive, TemplateRef, ViewContainerRef, effect, input } from '@angular/core';
import { ModuleService } from '../../services/module.service';

/**
 * A37 D-15 — renders its content only while a module (MODULE_*) or feature (FEATURE_*) is usable for the organization;
 * a list means every code must be. Re-evaluates when ModuleService refreshes.
 *
 *   <section *smsIfModule="'MODULE_MANUFACTURING'">…</section>
 *   <p-button *smsIfModule="['MODULE_INVENTORY', 'FEATURE_BOM_MANAGEMENT']; else readOnly" …></p-button>
 *   <ng-template #readOnly>…</ng-template>
 */
@Directive({
  selector: '[smsIfModule]',
  standalone: true
})
export class IfModuleDirective {
  readonly smsIfModule = input.required<string | string[]>();
  readonly smsIfModuleElse = input<TemplateRef<unknown> | null>(null);

  /** The template currently rendered (undefined = nothing evaluated yet, null = rendered nothing). */
  private current: TemplateRef<unknown> | null | undefined = undefined;

  constructor(
    private template: TemplateRef<unknown>,
    private container: ViewContainerRef,
    private modules: ModuleService
  ) {
    effect(() => {
      const enabled = this.modules.allEnabled(this.smsIfModule());
      this.render(enabled ? this.template : this.smsIfModuleElse());
    });
  }

  private render(tpl: TemplateRef<unknown> | null): void {
    if (tpl === this.current) return;
    this.container.clear();
    if (tpl) this.container.createEmbeddedView(tpl);
    this.current = tpl;
  }
}
