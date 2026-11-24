import { of } from 'rxjs';
import { CountriesAdminService, CountryAdmin } from '../../../services/countries-admin.service';
import { FeaturesAdminService, Plan } from '../../../services/features-admin.service';
import { Industry, IndustriesService } from '../../../services/industries.service';

// A51 P3 - test doubles for the services the Create Organization form added (countries master, industries by country, plans).

export function country(code: string, name: string, tz: string | null, over: Partial<CountryAdmin> = {}): CountryAdmin {
  return {
    id: 'c-' + code, code, name, currencyCode: null, defaultTimezone: tz, fiscalYearStartMonth: 1, dateFormat: 'DD/MM/YYYY',
    phoneCode: null, isActive: true, sortOrder: 0, ...over
  };
}

export function industry(code: string, name: string, over: Partial<Industry> = {}): Industry {
  return { id: code.length, code, name, description: null, isActive: true, sortOrder: 0, countryCodes: [], ...over };
}

export function plan(code: string, name: string, over: Partial<Plan> = {}): Plan {
  return { id: code.length, code, name, description: null, monthlyPrice: null, isActive: true, sortOrder: 0, includedFeatureCount: 1, totalFeatureCount: 2, ...over };
}

export const PK = country('PK', 'Pakistan', 'Asia/Karachi', { sortOrder: 1 });
export const AE = country('AE', 'United Arab Emirates', 'Asia/Dubai', { sortOrder: 2 });

export function a51Doubles(
  countries: CountryAdmin[] = [PK, AE],
  industriesByCountry: Record<string, Industry[]> = { PK: [industry('TRADING', 'Trading'), industry('RETAIL', 'Retail')], AE: [industry('TRADING', 'Trading')] },
  plans: Plan[] = [plan('BASIC', 'Basic'), plan('STANDARD', 'Standard', { sortOrder: 1 }), plan('ENTERPRISE', 'Enterprise', { sortOrder: 2 })]
) {
  const countriesAdmin = jasmine.createSpyObj<CountriesAdminService>('CountriesAdminService', ['list']);
  countriesAdmin.list.and.returnValue(of({ success: true, message: '', result: countries }));
  const industries = jasmine.createSpyObj<IndustriesService>('IndustriesService', ['byCountry']);
  industries.byCountry.and.callFake((c: string) => of({ success: true, message: '', result: industriesByCountry[c] ?? [] }));
  const featuresAdmin = jasmine.createSpyObj<FeaturesAdminService>('FeaturesAdminService', ['listPlans']);
  featuresAdmin.listPlans.and.returnValue(of({ success: true, message: '', result: plans }));
  return { countriesAdmin, industries, featuresAdmin };
}

export function a51Providers() {
  const d = a51Doubles();
  return [
    { provide: CountriesAdminService, useValue: d.countriesAdmin },
    { provide: IndustriesService, useValue: d.industries },
    { provide: FeaturesAdminService, useValue: d.featuresAdmin }
  ];
}
