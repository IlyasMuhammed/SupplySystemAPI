import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { IfModuleDirective } from './if-module.directive';
import { ModuleService } from '../../services/module.service';

/** A ModuleService double whose enabled set is a signal, like the real one. */
class FakeModules {
  readonly enabled = signal<string[]>([]);
  allEnabled(codes: string | string[]): boolean {
    const on = this.enabled();
    return ([] as string[]).concat(codes).every(c => on.includes(c));
  }
}

@Component({
  standalone: true,
  imports: [IfModuleDirective],
  template: `
    <div id="mfg" *smsIfModule="'MODULE_MANUFACTURING'">Production tab</div>
    <div id="bom" *smsIfModule="codes(); else noBom">Edit BOM</div>
    <ng-template #noBom><span id="nobom">BOM read-only</span></ng-template>
  `
})
class HostComponent {
  codes = signal<string | string[]>(['MODULE_INVENTORY', 'FEATURE_BOM_MANAGEMENT']);
}

describe('*smsIfModule (A37 D-15)', () => {
  let modules: FakeModules;

  beforeEach(() => {
    modules = new FakeModules();
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [{ provide: ModuleService, useValue: modules }]
    });
  });

  function render() {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, el, has: (id: string) => !!el.querySelector('#' + id) };
  }

  it('hides content of a switched-off module and shows the else template', () => {
    const { has } = render();
    expect(has('mfg')).toBeFalse();
    expect(has('bom')).toBeFalse();
    expect(has('nobom')).toBeTrue();
  });

  it('shows content when the module (and, for a list, every code) is on', () => {
    modules.enabled.set(['MODULE_MANUFACTURING', 'MODULE_INVENTORY', 'FEATURE_BOM_MANAGEMENT']);
    const { has } = render();
    expect(has('mfg')).toBeTrue();
    expect(has('bom')).toBeTrue();
    expect(has('nobom')).toBeFalse();
  });

  it('needs every code of a list', () => {
    modules.enabled.set(['MODULE_INVENTORY']);
    const { has } = render();
    expect(has('bom')).toBeFalse();
    expect(has('nobom')).toBeTrue();
  });

  it('re-evaluates when ModuleService refreshes, without re-creating an unchanged view', () => {
    const { fixture, el, has } = render();
    expect(has('mfg')).toBeFalse();

    modules.enabled.set(['MODULE_MANUFACTURING']);
    fixture.detectChanges();
    expect(has('mfg')).toBeTrue();
    const first = el.querySelector('#mfg');

    modules.enabled.set(['MODULE_MANUFACTURING', 'MODULE_SERVICES']);
    fixture.detectChanges();
    expect(el.querySelector('#mfg')).toBe(first);

    modules.enabled.set([]);
    fixture.detectChanges();
    expect(has('mfg')).toBeFalse();
  });

  it('re-evaluates when the code input changes', () => {
    modules.enabled.set(['MODULE_SERVICES']);
    const { fixture, has } = render();
    expect(has('bom')).toBeFalse();
    fixture.componentInstance.codes.set('MODULE_SERVICES');
    fixture.detectChanges();
    expect(has('bom')).toBeTrue();
  });
});
