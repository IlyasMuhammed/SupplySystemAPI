import { EventEmitter } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AutoComplete } from 'primeng/autocomplete';
import { AutoCompleteOpenOnFocusDirective } from './autocomplete-open-on-focus.directive';

describe('AutoCompleteOpenOnFocusDirective', () => {
  let ac: { onSelect: EventEmitter<unknown>; onFocus: EventEmitter<Event>; search: jasmine.Spy };
  let directive: AutoCompleteOpenOnFocusDirective;

  beforeEach(() => {
    ac = { onSelect: new EventEmitter(), onFocus: new EventEmitter<Event>(), search: jasmine.createSpy('search') };
    TestBed.configureTestingModule({
      providers: [AutoCompleteOpenOnFocusDirective, { provide: AutoComplete, useValue: ac }]
    });
    directive = TestBed.inject(AutoCompleteOpenOnFocusDirective);
    directive.ngOnInit();
  });

  afterEach(() => directive.ngOnDestroy());

  it('opens the suggestions when the field gets focus', () => {
    const e = new Event('focus');
    ac.onFocus.emit(e);
    expect(ac.search).toHaveBeenCalledOnceWith(e, '', 'focus');
  });

  it('does not reopen the list on the focus PrimeNG gives the input right after a pick', () => {
    ac.onSelect.emit({ value: { uuid: 's-1' } });
    ac.onFocus.emit(new Event('focus'));
    expect(ac.search).not.toHaveBeenCalled();

    // A later, real focus opens it again.
    ac.onFocus.emit(new Event('focus'));
    expect(ac.search).toHaveBeenCalledTimes(1);
  });

  it('stays closed on focus when switched off', () => {
    directive.smsOpenOnFocus = false;
    ac.onFocus.emit(new Event('focus'));
    expect(ac.search).not.toHaveBeenCalled();
  });
});
