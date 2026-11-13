import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PlaceholderPanelComponent } from './placeholder-panel.component';

describe('PlaceholderPanelComponent (A50 task 308)', () => {
  let fixture: ComponentFixture<PlaceholderPanelComponent>;
  let c: PlaceholderPanelComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [PlaceholderPanelComponent] }).compileComponents();
    fixture = TestBed.createComponent(PlaceholderPanelComponent);
    c = fixture.componentInstance;
    c.placeholders = [
      { token: '{InvoiceNumber}', label: 'Invoice number', group: 'Document' },
      { token: 'CustomerName', label: 'Customer name', group: 'Customer', description: 'Bill-to name' },
      { token: '{LineItems}', label: 'Line items', group: 'Document', structural: true }
    ];
    fixture.detectChanges();
  });

  it('groups the tokens and always shows them in {braces}', () => {
    const groups = Array.from(fixture.nativeElement.querySelectorAll('.php-gname')).map((e: any) => e.textContent.trim());
    expect(groups).toEqual(['Document', 'Customer']);
    expect(fixture.nativeElement.querySelector('[data-testid="ph-CustomerName"] code').textContent).toBe('{CustomerName}');
    expect(fixture.nativeElement.querySelector('[data-testid="ph-LineItems"]').textContent).toContain('block');
  });

  it('emits the token on click', () => {
    const spy = jasmine.createSpy('insert');
    c.insert.subscribe(spy);
    (fixture.nativeElement.querySelector('[data-testid="ph-CustomerName"]') as HTMLButtonElement).click();
    expect(spy).toHaveBeenCalledWith('{CustomerName}');
  });

  it('filters by token, label or description, with an empty state', () => {
    c.query = 'bill-to';
    expect(c.groups.map(g => g.items.length)).toEqual([1]);
    c.query = 'zzz';
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="placeholder-empty"]').textContent).toContain('No placeholder matches');
  });

  it('shows the error state', () => {
    fixture.componentRef.setInput('error', 'The placeholders could not be loaded.');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="placeholder-error"]')).not.toBeNull();
  });
});
