import { Component, ContentChild, EventEmitter, Input, Output, TemplateRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { CdkDragDrop, DragDropModule } from '@angular/cdk/drag-drop';
import { KanbanColumn } from '../../../services/crm.service';

export interface KanbanMove<T> { item: T; from: KanbanColumn<T>; to: KanbanColumn<T>; }

/**
 * A41 register 1J — a pipeline board: one column per stage (name, win probability, count, total), cards from the caller's
 * template (`<ng-template #card let-item>`), drag-and-drop between columns. The board does not move a card itself: the
 * parent calls the stage endpoint and reloads (a refused move snaps back).
 */
@Component({
  selector: 'app-crm-kanban',
  standalone: true,
  imports: [CommonModule, DragDropModule],
  template: `
<div class="ckb" cdkDropListGroup data-testid="kanban">
  <section *ngFor="let col of columns; trackBy: trackCol" class="ckb-col" [class.ckb-won]="col.isWon" [class.ckb-lost]="col.isLost"
           [attr.data-testid]="'kanban-col-' + col.code">
    <header class="ckb-head">
      <div class="ckb-title"><span>{{ col.name }}</span><span class="sf-count">{{ col.count }}</span></div>
      <div class="ckb-sub">
        <span *ngIf="col.winProbability != null">{{ col.winProbability }}%</span>
        <span *ngIf="showTotals && col.totalRevenue">· {{ col.totalRevenue | number:'1.0-0' }}<span *ngIf="currency"> {{ currency }}</span></span>
      </div>
    </header>
    <div class="ckb-list" cdkDropList [cdkDropListData]="col" [cdkDropListDisabled]="!canDrag || col.isWon || col.isLost"
         (cdkDropListDropped)="dropped($event)">
      <div *ngFor="let item of col.items; trackBy: trackItem" class="ckb-card" cdkDrag [cdkDragData]="item" [cdkDragDisabled]="!canDrag">
        <ng-container *ngTemplateOutlet="card; context: { $implicit: item, column: col }"></ng-container>
      </div>
      <div class="ckb-empty" *ngIf="!col.items.length">Nothing here</div>
    </div>
  </section>
</div>`,
  styles: [`
    .ckb { display: flex; gap: .75rem; overflow-x: auto; padding-bottom: .5rem; align-items: flex-start; }
    .ckb-col { flex: 0 0 17rem; background: var(--sms-bg); border: 1px solid var(--sms-border-soft); border-radius: var(--sms-radius); display: flex; flex-direction: column; max-height: 70vh; }
    .ckb-col.ckb-won { border-top: 3px solid var(--sms-ok); }
    .ckb-col.ckb-lost { border-top: 3px solid var(--sms-danger); }
    .ckb-head { padding: .6rem .75rem .4rem; border-bottom: 1px solid var(--sms-border-soft); }
    .ckb-title { display: flex; justify-content: space-between; align-items: center; font-weight: 600; }
    .ckb-sub { color: var(--sms-text-muted); font-size: .8rem; display: flex; gap: .3rem; min-height: 1rem; }
    .ckb-list { padding: .5rem; overflow-y: auto; min-height: 4rem; flex: 1; }
    .ckb-card { background: var(--sms-surface); border: 1px solid var(--sms-border); border-radius: var(--sms-radius-sm); padding: .55rem .65rem;
                margin-bottom: .5rem; box-shadow: var(--sms-shadow); cursor: grab; }
    .ckb-card.cdk-drag-preview { box-shadow: var(--sms-shadow-lg); }
    .ckb-card.cdk-drag-placeholder { opacity: .35; }
    .ckb-empty { color: var(--sms-text-faint); font-size: .8rem; text-align: center; padding: .75rem 0; }
  `]
})
export class CrmKanbanComponent<T extends { uuid: string }> {
  @Input() columns: KanbanColumn<T>[] = [];
  @Input() canDrag = true;
  @Input() showTotals = true;
  /** One currency for the totals; empty when the cards mix currencies (totals are then plain sums). */
  @Input() currency = '';
  @Output() moved = new EventEmitter<KanbanMove<T>>();
  @ContentChild('card') card!: TemplateRef<unknown>;

  trackCol = (_: number, c: KanbanColumn<T>) => c.stageUuid;
  trackItem = (_: number, i: T) => i.uuid;

  dropped(event: CdkDragDrop<KanbanColumn<T>>): void {
    if (event.previousContainer === event.container) return;
    this.moved.emit({ item: event.item.data as T, from: event.previousContainer.data, to: event.container.data });
  }
}
