import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { ShippingRulesComponent } from './shipping-rules.component';
import {
  LogisticsService, ShippingRuleModel, CarrierListItemModel, CarrierServiceModel
} from '../../../../services/logistics.service';

function rule(overrides: Partial<ShippingRuleModel> = {}): ShippingRuleModel {
  return {
    uuid: 'rule-1', name: 'Domestic parcels', priority: 20, isActive: true,
    strategy: 'CHEAPEST', summary: 'Up to 30 kg, to PK → Beta Road, cheapest service.',
    createdDate: '2026-01-01T00:00:00Z',
    ...overrides
  };
}

function carrier(): CarrierListItemModel {
  return { uuid: 'carrier-1', name: 'Beta Road' } as CarrierListItemModel;
}

function service(): CarrierServiceModel {
  return {
    uuid: 's1', carrierUuid: 'carrier-1', carrierName: 'Beta Road',
    serviceCode: 'ROAD', serviceName: 'Road freight',
    supportsCod: false, supportsHazardous: false,
    isDefault: true, isActive: true, chargesVolumetricWeight: false,
    createdDate: '2026-01-01T00:00:00Z'
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('ShippingRulesComponent', () => {
  let fixture: ComponentFixture<ShippingRulesComponent>;
  let component: ShippingRulesComponent;
  let api: jasmine.SpyObj<LogisticsService>;

  async function setup(rules: ShippingRuleModel[] = [rule()]) {
    api = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getShippingRules', 'getShippingRuleById', 'createShippingRule',
      'patchShippingRule', 'deleteShippingRule',
      'getActiveCarriers', 'getCarrierServices'
    ]);

    api.getShippingRules.and.returnValue(ok(rules));
    api.getActiveCarriers.and.returnValue(ok([carrier()]));
    api.getCarrierServices.and.returnValue(ok([service()]));
    api.createShippingRule.and.returnValue(ok('new-rule'));
    api.patchShippingRule.and.returnValue(of({ success: true, message: '' } as any));
    api.deleteShippingRule.and.returnValue(of({ success: true, message: '' } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ShippingRulesComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ShippingRulesComponent);
    component = fixture.componentInstance;
  }

  // ── Reading the list ──────────────────────────────────────────────────────

  it('lists rules with the sentence each one reads as', async () => {
    // A rule list that has to be decoded column by column is a rule list nobody audits.
    await setup();
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelectorAll('[data-testid="rule"]').length).toBe(1);
    expect(el.querySelector('[data-testid="rule-summary"]')?.textContent)
      .toContain('Up to 30 kg, to PK → Beta Road');
  });

  it('marks a switched-off rule rather than hiding it', async () => {
    await setup([rule({ isActive: false })]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="rule-inactive"]')).toBeTruthy();
  });

  it('warns when no rule matches everything', async () => {
    // A list with no catch-all leaves some consignments unroutable, and the symptom only shows up
    // when somebody tries to apply a rule to one of them.
    await setup();
    fixture.detectChanges();

    expect(component.missingCatchAll).toBeTrue();
    expect(fixture.nativeElement.querySelector('[data-testid="no-catch-all"]')).toBeTruthy();
  });

  it('does not warn once a catch-all exists', async () => {
    await setup([rule(), rule({
      uuid: 'rule-2', name: 'Everything else', priority: 99,
      summary: 'Anything → the cheapest carrier.'
    })]);
    fixture.detectChanges();

    expect(component.missingCatchAll).toBeFalse();
    expect(fixture.nativeElement.querySelector('[data-testid="no-catch-all"]')).toBeNull();
  });

  it('says so when there are no rules at all', async () => {
    await setup([]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="no-rules"]')?.textContent)
      .toContain('chosen by hand');
  });

  // ── Editing ───────────────────────────────────────────────────────────────

  it('suggests the next free priority so two rules do not collide by accident', async () => {
    await setup([rule({ priority: 20 }), rule({ uuid: 'r2', priority: 40 })]);
    fixture.detectChanges();

    component.openCreate();

    expect(component.form.priority).toBe(50);
  });

  it('refuses a priority another rule already holds', async () => {
    await setup([rule({ priority: 20, name: 'Domestic parcels' })]);
    fixture.detectChanges();

    component.openCreate();
    component.form.name = 'Something else';
    component.form.priority = 20;

    expect(component.validationError)
      .toContain("'Domestic parcels' is already priority 20");
  });

  it('lets a rule keep its own priority while being edited', async () => {
    await setup([rule({ priority: 20 })]);
    fixture.detectChanges();

    component.openEdit(rule({ priority: 20 }));

    expect(component.validationError).toBeNull();
  });

  it('refuses a range that matches nothing at all', async () => {
    await setup([]);
    fixture.detectChanges();
    component.openCreate();
    component.form.name = 'Impossible';

    component.form.minChargeableWeightKg = 30;
    component.form.maxChargeableWeightKg = 5;
    expect(component.validationError).toContain('nothing at all');

    component.form.minChargeableWeightKg = null;
    component.form.maxChargeableWeightKg = null;
    component.form.minDeclaredValue = 5000;
    component.form.maxDeclaredValue = 100;
    expect(component.validationError).toContain('nothing at all');
  });

  it('refuses a service without a carrier', async () => {
    // The same code means different things at two carriers, so it resolves against nothing.
    await setup([]);
    fixture.detectChanges();
    component.openCreate();
    component.form.name = 'Orphan';
    component.form.carrierUuid = null;
    component.form.serviceCode = 'EXPRESS';

    expect(component.validationError).toContain('belongs to a carrier');
  });

  it('loads a carriers services only once a carrier is chosen', async () => {
    await setup([]);
    fixture.detectChanges();
    component.openCreate();

    expect(component.services.length).toBe(0);

    component.form.carrierUuid = 'carrier-1';
    component.onCarrierChange();

    expect(api.getCarrierServices).toHaveBeenCalledWith('carrier-1');
    expect(component.services.length).toBe(1);
  });

  it('clears a service that no longer has a carrier behind it', async () => {
    await setup([]);
    fixture.detectChanges();
    component.openCreate();
    component.form.carrierUuid = 'carrier-1';
    component.onCarrierChange();
    component.form.serviceCode = 'ROAD';

    component.form.carrierUuid = null;
    component.onCarrierChange();

    expect(component.form.serviceCode).toBeNull();
  });

  it('creates a rule with its conditions and its action', async () => {
    await setup([]);
    fixture.detectChanges();
    component.openCreate();

    component.form.name = ' Dangerous goods ';
    component.form.priority = 10;
    component.form.maxChargeableWeightKg = 30;
    component.form.destinationCountryIso = 'PK';
    component.form.hazardous = true;
    component.form.carrierUuid = 'carrier-1';
    component.form.strategy = 'FASTEST';

    component.save();

    expect(api.createShippingRule).toHaveBeenCalledWith(jasmine.objectContaining({
      name: 'Dangerous goods', priority: 10, maxChargeableWeightKg: 30,
      destinationCountryIso: 'PK', appliesToHazardous: true,
      carrierUuid: 'carrier-1', strategy: 'FASTEST'
    }));
  });

  it('keeps "does not care" distinct from "only no"', async () => {
    // Three states, not two: "this rule is for dangerous goods" and "this rule does not care" are
    // different statements, and collapsing them loses the one that matters.
    await setup([]);
    fixture.detectChanges();
    component.openCreate();
    component.form.name = 'Ordinary goods';
    component.form.hazardous = false;

    component.save();

    expect(api.createShippingRule.calls.mostRecent().args[0].appliesToHazardous).toBeFalse();
  });

  it('clears a condition that was emptied rather than leaving it alone', async () => {
    await setup([rule()]);
    fixture.detectChanges();

    component.openEdit(rule({ maxChargeableWeightKg: 30, appliesToHazardous: true }));
    component.form.maxChargeableWeightKg = null;
    component.form.hazardous = null;

    component.save();

    const sent = api.patchShippingRule.calls.mostRecent().args[1];
    expect(sent.clearConditions).toContain('MAX_WEIGHT');
    expect(sent.clearConditions).toContain('HAZARDOUS');
  });

  it('surfaces the servers reason when a rule is refused', async () => {
    await setup([]);
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    api.createShippingRule.and.returnValue(throwError(() => ({
      error: { message: "'Featherweight' is already priority 10." }
    })));

    component.openCreate();
    component.form.name = 'Clashing';
    component.save();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error', detail: "'Featherweight' is already priority 10."
    }));
  });

  // ── Switching off and removing ────────────────────────────────────────────

  it('switches a rule off without removing it', async () => {
    await setup([rule()]);
    fixture.detectChanges();

    component.toggleActive(rule());

    expect(api.patchShippingRule).toHaveBeenCalledWith('rule-1', { isActive: false });
  });

  it('removes a rule', async () => {
    await setup([rule()]);
    fixture.detectChanges();

    component.remove(rule());

    expect(api.deleteShippingRule).toHaveBeenCalledWith('rule-1');
  });
});
