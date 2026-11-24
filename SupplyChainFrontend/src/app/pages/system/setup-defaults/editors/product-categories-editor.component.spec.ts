import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import {
  CategoryNode, ProductCategoriesEditorComponent, allCategories, categoryProblem, moveCategory
} from './product-categories-editor.component';

/** A51 P5 — PRODUCT_CATEGORIES tree editor: add, rename, remove, move (re-parent), validation. */
describe('ProductCategoriesEditorComponent', () => {
  let fixture: ComponentFixture<ProductCategoriesEditorComponent>;
  let c: ProductCategoriesEditorComponent;
  let emitted: any[];

  const sample = (): CategoryNode[] => [
    { code: 'FOOD', name: 'Food', children: [{ code: 'FOOD-A', name: 'Packaged' }] },
    { code: 'HOME', name: 'Household' },
    { code: 'OTH', name: 'Other' }
  ];
  const last = () => emitted[emitted.length - 1];
  const node = (code: string) => allCategories(c.roots).find(n => n.code === code)!;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ProductCategoriesEditorComponent], providers: [provideNoopAnimations()] }).compileComponents();
    fixture = TestBed.createComponent(ProductCategoriesEditorComponent);
    c = fixture.componentInstance;
    emitted = [];
    c.payloadChange.subscribe(p => emitted.push(p));
    fixture.componentRef.setInput('payload', { displayName: 'Cats', categories: sample() });
    fixture.detectChanges();
  });

  it('renders the tree with a count', () => {
    expect(c.count).toBe(4);
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="pc-count"]')!.textContent).toContain('4 categories');
    expect(el.querySelector('[data-testid="pc-node-FOOD-A"]')).toBeTruthy();
  });

  it('adds a top-level category and a sub-category, keeping the other payload fields', () => {
    c.addRoot();
    expect(last().displayName).toBe('Cats');
    expect(last().categories.length).toBe(4);
    c.addChild(node('HOME'));
    expect(allCategories(last().categories).length).toBe(6);
    expect(node('HOME').children!.length).toBe(1);
    const codes = allCategories(c.roots).map(n => n.code.toLowerCase());
    expect(new Set(codes).size).toBe(codes.length);
  });

  it('renames (code, name) on commit and trims', () => {
    const n = node('HOME');
    c.edit(n);
    n.name = '  Home and Living ';
    n.code = ' HL ';
    c.commit();
    expect(last().categories.find((x: any) => x.code === 'HL').name).toBe('Home and Living');
    expect(c.editing).toBeNull();
  });

  it('removes a category together with its children', () => {
    c.remove(node('FOOD'));
    expect(allCategories(last().categories).map(n => n.code)).toEqual(['HOME', 'OTH']);
  });

  it('moves a category under another top-level one and back to the top level', () => {
    c.startMove(node('OTH'));
    expect(c.moveTargets(node('OTH')).map(t => t.value)).toEqual(['', 'FOOD', 'HOME']);
    c.moveTarget = 'HOME';
    c.move(node('OTH'));
    expect(last().categories.find((x: any) => x.code === 'HOME').children.map((x: any) => x.code)).toEqual(['OTH']);
    c.startMove(node('FOOD-A'));
    c.moveTarget = '';
    c.move(node('FOOD-A'));
    expect(last().categories.map((x: any) => x.code)).toContain('FOOD-A');
    expect(categoryProblem(last().categories)).toBeNull();
  });

  it('refuses a move that would make three levels or move a category under itself', () => {
    // FOOD has a child, so it cannot go under another category (that would be three levels).
    expect(c.moveTargets(node('FOOD')).map(t => t.value)).toEqual(['']);
    expect(moveCategory(c.roots, node('FOOD'), node('HOME'))).toBeFalse();
    expect(moveCategory(c.roots, node('FOOD'), node('FOOD'))).toBeFalse();
    expect(moveCategory(c.roots, node('FOOD'), node('FOOD-A'))).toBeFalse();
    expect(c.roots.map(r => r.code)).toEqual(['FOOD', 'HOME', 'OTH']);
  });

  it('categoryProblem: empty, bad or duplicate codes, missing names, depth', () => {
    expect(categoryProblem([])).toContain('at least one');
    expect(categoryProblem([{ code: '', name: 'x' }])).toContain('code');
    expect(categoryProblem([{ code: 'bad code', name: 'x' }])).toContain('letters');
    expect(categoryProblem([{ code: 'A', name: ' ' }])).toContain('name');
    expect(categoryProblem([{ code: 'A', name: 'a' }, { code: 'a', name: 'b' }])).toContain('twice');
    expect(categoryProblem([{ code: 'A', name: 'a', children: [{ code: 'B', name: 'b', children: [{ code: 'C', name: 'c' }] }] }])).toContain('deeper');
    expect(categoryProblem(sample())).toBeNull();
  });

  it('shows the problem after an edit that breaks the rules', () => {
    const n = node('HOME');
    c.edit(n);
    n.code = 'OTH';
    c.commit();
    expect(c.problem).toContain('twice');
  });
});
