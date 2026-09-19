import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { LookupValuesListComponent } from './lookup-values-list.component';

describe('LookupValuesListComponent', () => {
  let component: LookupValuesListComponent;
  let fixture: ComponentFixture<LookupValuesListComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LookupValuesListComponent],
      providers: [
        // These components inject a data service, so they need the HTTP stack. The CLI stub
        // provided none, which is why every one of them failed on NullInjectorError rather than
        // on anything about the component. Routing and animations are for the PrimeNG pieces.
        provideHttpClient(), provideHttpClientTesting(),
        provideRouter([]), provideNoopAnimations()
      ]
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
