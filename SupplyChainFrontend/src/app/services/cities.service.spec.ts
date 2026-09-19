import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';

import { CitiesService } from './cities.service';

describe('CitiesService', () => {
  let service: CitiesService;

  beforeEach(() => {
    // The service injects HttpClient, so the testing module has to provide it. The CLI stub
    // configured an empty module, which is what this was failing on.
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    service = TestBed.inject(CitiesService);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
