import { AfterViewInit, Component, ElementRef, EventEmitter, Input, OnChanges, Output, SimpleChanges, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';

/**
 * A41 D-21 — the activity notes editor: bold, italic, underline and lists over a contenteditable area (no editor library is
 * installed). Emits HTML; the server keeps only these formatting tags (CrmHtml.Sanitize), so pasted styling is dropped there.
 */
@Component({
  selector: 'app-crm-rich-text',
  standalone: true,
  imports: [CommonModule],
  template: `
<div class="crt" [class.crt-disabled]="disabled">
  <div class="crt-bar" role="toolbar" aria-label="Formatting">
    <button type="button" (mousedown)="$event.preventDefault()" (click)="exec('bold')" title="Bold" aria-label="Bold"><b>B</b></button>
    <button type="button" (mousedown)="$event.preventDefault()" (click)="exec('italic')" title="Italic" aria-label="Italic"><i>I</i></button>
    <button type="button" (mousedown)="$event.preventDefault()" (click)="exec('underline')" title="Underline" aria-label="Underline"><u>U</u></button>
    <span class="crt-sep"></span>
    <button type="button" (mousedown)="$event.preventDefault()" (click)="exec('insertUnorderedList')" title="Bullet list" aria-label="Bullet list"><i class="pi pi-list"></i></button>
    <button type="button" (mousedown)="$event.preventDefault()" (click)="exec('insertOrderedList')" title="Numbered list" aria-label="Numbered list"><span class="crt-num">1.</span></button>
  </div>
  <div #area class="crt-area" [attr.contenteditable]="!disabled" role="textbox" aria-multiline="true" [attr.aria-label]="label"
       [attr.data-placeholder]="placeholder" (input)="emit()" (blur)="emit()" data-testid="rich-text"></div>
</div>`,
  styles: [`
    .crt { border: 1px solid var(--sms-border); border-radius: var(--sms-radius-sm); background: var(--sms-input-bg); }
    .crt-bar { display: flex; gap: .15rem; padding: .3rem .4rem; border-bottom: 1px solid var(--sms-border); }
    .crt-bar button { border: 0; background: transparent; min-width: 2rem; height: 1.9rem; border-radius: 4px; cursor: pointer; color: var(--sms-text); }
    .crt-bar button:hover { background: var(--sms-row-hover); }
    .crt-sep { width: 1px; background: var(--sms-border); margin: .2rem .3rem; }
    .crt-num { font-weight: 600; font-size: .8rem; }
    .crt-area { min-height: 8rem; max-height: 22rem; overflow: auto; padding: .6rem .75rem; outline: none; line-height: 1.5; }
    .crt-area:empty::before { content: attr(data-placeholder); color: var(--sms-text-faint); }
    .crt-area ul, .crt-area ol { padding-left: 1.4rem; margin: .25rem 0; }
    .crt-disabled { opacity: .7; }
  `]
})
export class CrmRichTextComponent implements AfterViewInit, OnChanges {
  @Input() value: string | null = '';
  @Output() valueChange = new EventEmitter<string>();
  @Input() placeholder = 'Notes, meeting minutes, key points…';
  @Input() label = 'Notes';
  @Input() disabled = false;
  @ViewChild('area', { static: true }) area!: ElementRef<HTMLDivElement>;

  private last = '';

  ngAfterViewInit(): void { this.write(this.value); }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['value'] && this.area && (this.value ?? '') !== this.last) this.write(this.value);
  }

  exec(command: string): void {
    if (this.disabled) return;
    this.area.nativeElement.focus();
    document.execCommand(command, false);
    this.emit();
  }

  emit(): void {
    const html = this.area.nativeElement.innerHTML;
    const text = this.area.nativeElement.textContent?.trim() ?? '';
    this.last = text || html.includes('<li') ? html : '';
    this.valueChange.emit(this.last);
  }

  private write(value: string | null): void {
    this.last = value ?? '';
    // Server-sanitized HTML (formatting tags only) or the user's own typing.
    this.area.nativeElement.innerHTML = this.last;
  }
}
