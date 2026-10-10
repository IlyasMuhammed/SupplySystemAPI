import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { FlowAnchorsComponent, FlowSection } from './flow-anchors.component';

// Section strips on detail pages are tabs by default: one section shown at a time, the rest hidden by id.

@Component({
  standalone: true,
  imports: [CommonModule, FlowAnchorsComponent],
  template: `
    <sf-anchors [sections]="sections" [mode]="mode"></sf-anchors>
    <section id="t-a" class="sf-section">A</section>
    <section id="t-b" class="sf-section">B</section>
    <section id="t-c" class="sf-section" *ngIf="showC">C</section>
  `
})
class HostComponent {
  sections: FlowSection[] = [{ id: 't-a', label: 'A' }, { id: 't-b', label: 'B' }, { id: 't-c', label: 'C' }];
  mode: 'tabs' | 'scroll' = 'tabs';
  showC = true;
}

describe('sf-anchors as tabs', () => {
  let fixture: ComponentFixture<HostComponent>;
  const shown = () => ['t-a', 't-b', 't-c']
    .map(id => fixture.nativeElement.querySelector('#' + id) as HTMLElement | null)
    .filter(el => !!el && el.style.display !== 'none').map(el => el!.id);
  const click = (id: string) => {
    (fixture.nativeElement.querySelector(`[data-testid="anchor-${id}"]`) as HTMLButtonElement).click();
    fixture.detectChanges();
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    fixture = TestBed.createComponent(HostComponent);
    document.body.appendChild(fixture.nativeElement);
    fixture.detectChanges();
  });

  afterEach(() => fixture.nativeElement.remove());

  it('shows only the first section by default and switches on click', () => {
    expect(shown()).toEqual(['t-a']);
    click('t-b');
    expect(shown()).toEqual(['t-b']);
    expect(fixture.nativeElement.querySelector('[data-testid="anchor-t-b"]').getAttribute('aria-selected')).toBe('true');
  });

  it('falls back to the first section when the selected tab is removed', () => {
    click('t-c');
    expect(shown()).toEqual(['t-c']);
    fixture.componentInstance.showC = false;
    fixture.componentInstance.sections = fixture.componentInstance.sections.filter(s => s.id !== 't-c');
    fixture.detectChanges();
    expect(shown()).toEqual(['t-a']);
  });

  it('a click is not undone when the page renders the chosen section only after the click (*ngIf pages)', async () => {
    fixture.componentInstance.showC = false;
    fixture.detectChanges();
    click('t-c');                       // the section does not exist yet at click time
    fixture.componentInstance.showC = true;
    fixture.detectChanges();
    await Promise.resolve();
    fixture.detectChanges();
    expect(shown()).toEqual(['t-c']);
  });

  it('scroll mode keeps every section on the page', () => {
    fixture.componentInstance.mode = 'scroll';
    fixture.detectChanges();
    ['t-a', 't-b', 't-c'].forEach(id => (fixture.nativeElement.querySelector('#' + id) as HTMLElement).style.display = '');
    fixture.detectChanges();
    expect(shown()).toEqual(['t-a', 't-b', 't-c']);
  });
});
