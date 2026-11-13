import { Component, ElementRef, EventEmitter, Input, OnChanges, Output, SimpleChanges, ViewChild, AfterViewInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TooltipModule } from 'primeng/tooltip';

/**
 * A small rich-text field (contenteditable + toolbar), as on the PO template page, that remembers where its cursor
 * was so a placeholder picked from the side panel lands there. Content is set imperatively, never bound, so the
 * caret does not jump while typing.
 */
@Component({
  selector: 'app-html-field',
  standalone: true,
  imports: [CommonModule, TooltipModule],
  template: `
    <div class="hf" [class.hf-focus]="active">
      <div class="rte-toolbar" role="toolbar" [attr.aria-label]="label + ' formatting'">
        <button type="button" class="rte-btn rte-btn--glyph" pTooltip="Bold" aria-label="Bold" (mousedown)="$event.preventDefault()" (click)="format('bold')"><b>B</b></button>
        <button type="button" class="rte-btn rte-btn--glyph" pTooltip="Italic" aria-label="Italic" (mousedown)="$event.preventDefault()" (click)="format('italic')"><i>I</i></button>
        <button type="button" class="rte-btn rte-btn--glyph" pTooltip="Underline" aria-label="Underline" (mousedown)="$event.preventDefault()" (click)="format('underline')"><u>U</u></button>
        <button type="button" class="rte-btn" pTooltip="Bullet list" aria-label="Bullet list" (mousedown)="$event.preventDefault()" (click)="format('insertUnorderedList')"><i class="pi pi-list"></i></button>
        <span class="rte-sep"></span>
        <button type="button" class="rte-btn" pTooltip="Align left" aria-label="Align left" (mousedown)="$event.preventDefault()" (click)="format('justifyLeft')"><i class="pi pi-align-left"></i></button>
        <button type="button" class="rte-btn" pTooltip="Align center" aria-label="Align center" (mousedown)="$event.preventDefault()" (click)="format('justifyCenter')"><i class="pi pi-align-center"></i></button>
        <button type="button" class="rte-btn" pTooltip="Align right" aria-label="Align right" (mousedown)="$event.preventDefault()" (click)="format('justifyRight')"><i class="pi pi-align-right"></i></button>
      </div>
      <div #editor class="rte-editor" contenteditable="true" role="textbox" aria-multiline="true" [attr.aria-label]="label"
           [style.min-height.px]="minHeight" [attr.data-testid]="testId"
           (focus)="onFocus()" (keyup)="remember()" (mouseup)="remember()" (input)="onInput()" (blur)="remember()"></div>
    </div>
  `,
  styles: [`
    .rte-toolbar { display: flex; align-items: center; gap: .3rem; padding: .4rem; border: 1px solid var(--sms-border); border-bottom: none;
                   border-radius: .5rem .5rem 0 0; background: var(--sms-surface-2); flex-wrap: wrap; }
    .rte-btn { width: 2rem; height: 2rem; display: flex; align-items: center; justify-content: center; border: 1px solid transparent;
               border-radius: .4rem; background: transparent; color: var(--sms-text-muted); cursor: pointer; }
    .rte-btn:hover { background: var(--sms-row-hover); color: var(--sms-primary-text); }
    .rte-btn i { font-size: .85rem; }
    .rte-btn--glyph { font-size: .85rem; font-family: Georgia, 'Times New Roman', serif; }
    .rte-sep { width: 1px; height: 1.5rem; background: var(--sms-border); margin: 0 .25rem; }
    .rte-editor { padding: .75rem .9rem; border: 1px solid var(--sms-border); border-radius: 0 0 .5rem .5rem; font-size: .875rem;
                  line-height: 1.6; color: var(--sms-text); background: var(--sms-surface); overflow-y: auto; overflow-wrap: anywhere; }
    .rte-editor:focus { outline: none; border-color: var(--sms-primary); }
    .hf-focus .rte-toolbar { border-color: var(--sms-primary); }
  `]
})
export class HtmlFieldComponent implements AfterViewInit, OnChanges {
  @ViewChild('editor', { static: true }) editorRef!: ElementRef<HTMLDivElement>;

  @Input() value: string | null | undefined = '';
  @Input() label = 'Text';
  @Input() minHeight = 120;
  @Input() testId = '';
  /** Highlighted as the placeholder panel's target. */
  @Input() active = false;
  @Output() valueChange = new EventEmitter<string>();
  @Output() focused = new EventEmitter<void>();

  private range: Range | null = null;
  private lastSet: string | null = null;

  ngAfterViewInit(): void { this.write(this.value); }

  ngOnChanges(changes: SimpleChanges): void {
    // Only an outside change (load, reset) is written; our own emits come back equal and are ignored.
    if (changes['value'] && this.editorRef && (this.value ?? '') !== this.lastSet) this.write(this.value);
  }

  get html(): string { return this.editorRef?.nativeElement.innerHTML ?? ''; }

  private write(v: string | null | undefined): void {
    this.lastSet = v ?? '';
    this.editorRef.nativeElement.innerHTML = this.lastSet;
  }

  onFocus(): void { this.focused.emit(); this.remember(); }

  onInput(): void {
    this.lastSet = this.html;
    this.valueChange.emit(this.lastSet);
  }

  remember(): void {
    const sel = typeof window !== 'undefined' ? window.getSelection() : null;
    if (sel && sel.rangeCount && this.editorRef.nativeElement.contains(sel.getRangeAt(0).commonAncestorContainer)) {
      this.range = sel.getRangeAt(0).cloneRange();
    }
  }

  format(command: string): void {
    this.editorRef.nativeElement.focus();
    this.restore();
    document.execCommand(command, false);
    this.onInput();
  }

  /** Inserts text where the cursor was (or at the end). */
  insertText(text: string): void {
    const el = this.editorRef.nativeElement;
    el.focus();
    this.restore();
    const sel = window.getSelection();
    if (!sel || !sel.rangeCount || !el.contains(sel.getRangeAt(0).commonAncestorContainer)) {
      const r = document.createRange();
      r.selectNodeContents(el);
      r.collapse(false);
      sel?.removeAllRanges();
      sel?.addRange(r);
    }
    const range = window.getSelection()!.getRangeAt(0);
    range.deleteContents();
    const node = document.createTextNode(text);
    range.insertNode(node);
    range.setStartAfter(node);
    range.collapse(true);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);
    this.range = range.cloneRange();
    this.onInput();
  }

  private restore(): void {
    if (!this.range) return;
    const sel = window.getSelection();
    sel?.removeAllRanges();
    sel?.addRange(this.range);
  }
}
