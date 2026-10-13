import { Component } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { CrmKanbanComponent, KanbanMove } from './crm-kanban.component';
import { CrmRichTextComponent } from './crm-rich-text.component';
import { CrmTimelineComponent } from './crm-timeline.component';
import { KanbanColumn } from '../../../services/crm.service';

interface Card { uuid: string; title: string; }

const col = (code: string, items: Card[], extra: Partial<KanbanColumn<Card>> = {}): KanbanColumn<Card> => ({
  stageUuid: 's-' + code, code, name: code, sequence: 1, winProbability: 20, isWon: false, isLost: false,
  count: items.length, totalRevenue: 1500, items, ...extra
});

@Component({
  standalone: true,
  imports: [CrmKanbanComponent],
  template: `
<app-crm-kanban [columns]="columns" currency="PKR" (moved)="moves.push($event)">
  <ng-template #card let-item><span class="t">{{ item.title }}</span></ng-template>
</app-crm-kanban>`
})
class KanbanHost {
  columns = [col('NEW', [{ uuid: 'a', title: 'Alpha' }, { uuid: 'b', title: 'Beta' }]), col('WON', [], { isWon: true, winProbability: 100 })];
  moves: KanbanMove<Card>[] = [];
}

// A41 register 1J — the pipeline board.
describe('CrmKanbanComponent', () => {
  let fixture: ComponentFixture<KanbanHost>;

  beforeEach(() => {
    fixture = TestBed.createComponent(KanbanHost);
    fixture.detectChanges();
  });

  it('renders a column per stage with count, probability, total and the caller\'s cards', () => {
    const el: HTMLElement = fixture.nativeElement;
    const newCol = el.querySelector('[data-testid="kanban-col-NEW"]')!;
    expect(newCol.querySelector('.sf-count')!.textContent!.trim()).toBe('2');
    expect(newCol.textContent).toContain('20%');
    expect(newCol.textContent).toContain('1,500');
    expect(newCol.textContent).toContain('PKR');
    expect(Array.from(newCol.querySelectorAll('.t')).map(e => e.textContent)).toEqual(['Alpha', 'Beta']);
    const won = el.querySelector('[data-testid="kanban-col-WON"]')!;
    expect(won.classList).toContain('ckb-won');
    expect(won.textContent).toContain('Nothing here');
  });

  it('emits a move only when a card changes column', () => {
    const board = fixture.debugElement.children[0].componentInstance as CrmKanbanComponent<Card>;
    const host = fixture.componentInstance;
    const [from, to] = host.columns;
    const fromList = { data: from } as any, toList = { data: to } as any;
    board.dropped({ previousContainer: fromList, container: fromList, item: { data: from.items[0] } } as any);
    expect(host.moves.length).toBe(0);
    board.dropped({ previousContainer: fromList, container: toList, item: { data: from.items[0] } } as any);
    expect(host.moves.length).toBe(1);
    expect(host.moves[0].item.uuid).toBe('a');
    expect(host.moves[0].to.code).toBe('WON');
  });
});

// A41 D-21 — the notes editor.
describe('CrmRichTextComponent', () => {
  let fixture: ComponentFixture<CrmRichTextComponent>;
  let cmp: CrmRichTextComponent;
  const area = () => fixture.nativeElement.querySelector('[data-testid="rich-text"]') as HTMLDivElement;

  beforeEach(() => {
    fixture = TestBed.createComponent(CrmRichTextComponent);
    cmp = fixture.componentInstance;
    cmp.value = '<p><b>Agreed</b> next steps</p>';
    fixture.detectChanges();
  });

  it('shows the given HTML and emits the edited HTML', () => {
    expect(area().innerHTML).toBe('<p><b>Agreed</b> next steps</p>');
    let out = '';
    cmp.valueChange.subscribe(v => out = v);
    area().innerHTML = '<p>Changed</p>';
    area().dispatchEvent(new Event('input'));
    expect(out).toBe('<p>Changed</p>');
  });

  it('emits empty for whitespace-only content but keeps an empty list', () => {
    let out = 'x';
    cmp.valueChange.subscribe(v => out = v);
    area().innerHTML = '<br>';
    cmp.emit();
    expect(out).toBe('');
    area().innerHTML = '<ul><li></li></ul>';
    cmp.emit();
    expect(out).toBe('<ul><li></li></ul>');
  });

  it('is not editable when disabled', () => {
    fixture.componentRef.setInput('disabled', true);
    fixture.detectChanges();
    expect(area().getAttribute('contenteditable')).toBe('false');
    const spy = spyOn(document, 'execCommand');
    cmp.exec('bold');
    expect(spy).not.toHaveBeenCalled();
  });
});

// Lead/opportunity timeline: text search (debounced, emitted to the parent) and its own scroll box.
describe('CrmTimelineComponent search and scroll', () => {
  let fixture: ComponentFixture<CrmTimelineComponent>;
  let cmp: CrmTimelineComponent;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [CrmTimelineComponent], providers: [provideRouter([])] });
    fixture = TestBed.createComponent(CrmTimelineComponent);
    cmp = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('emits the trimmed search text once typing pauses', fakeAsync(() => {
    const seen: string[] = [];
    cmp.searchChange.subscribe(s => seen.push(s));
    const input = fixture.nativeElement.querySelector('[data-testid="tl-search"]') as HTMLInputElement;
    input.value = ' disc';
    input.dispatchEvent(new Event('input'));
    tick(200);
    input.value = ' discount ';
    input.dispatchEvent(new Event('input'));
    tick(400);
    expect(seen).toEqual(['discount']);
  }));

  it('says nothing matched when a search returns no entries', fakeAsync(() => {
    cmp.query = 'zzz';
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Nothing in the timeline matches');
  }));

  it('puts the feed in a vertically scrolling box', () => {
    const box = fixture.nativeElement.querySelector('[data-testid="tl-scroll"]') as HTMLElement;
    expect(getComputedStyle(box).overflowY).toBe('auto');
  });
});
