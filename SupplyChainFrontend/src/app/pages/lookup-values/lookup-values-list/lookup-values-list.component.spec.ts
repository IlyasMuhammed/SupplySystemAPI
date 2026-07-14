import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LookupValuesListComponent } from './lookup-values-list.component';

describe('LookupValuesListComponent', () => {
  let component: LookupValuesListComponent;
  let fixture: ComponentFixture<LookupValuesListComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LookupValuesListComponent]
    })
    .compileComponents();

    fixture = TestBed.createComponent(LookupValuesListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
