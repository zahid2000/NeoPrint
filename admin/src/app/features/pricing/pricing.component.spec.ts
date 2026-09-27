import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { PricingProfile } from '../../core/api/step2.models';
import { PricingComponent } from './pricing.component';

const standard: PricingProfile = {
  id: 'p1',
  name: 'Standart',
  isDefault: true,
  bwPricePerSide: 0.1,
  colorPricePerSide: 0.3,
  minOrder: 0.2,
  paperMultipliers: { A4: 1, A3: 2 },
  volumeTiers: [{ minSides: 50, discountPercent: 10 }],
  kioskCount: 2,
  updatedAt: '2026-09-27T10:00:00Z',
  revision: 3,
};

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
  await fixture.whenStable();
}

describe('PricingComponent', () => {
  let fixture: ComponentFixture<PricingComponent>;
  let component: PricingComponent;
  let backend: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(PricingComponent);
    component = fixture.componentInstance;

    backend.expectOne('/api/admin/pricing-profiles').flush([standard]);
    await settle(fixture);
    backend.expectOne('/api/admin/pricing-profiles/p1').flush({ profile: standard, kiosks: [] });
    await settle(fixture);
  });

  afterEach(() => backend.verify());

  it('opens the default profile with its values on the form', () => {
    expect(component.selectedId()).toBe('p1');
    expect(component.draft().color).toBe(0.3);
    expect(component.draft().tiers).toEqual([{ minSides: 50, discountPercent: 10 }]);
    expect(component.dirty()).toBe(false);
  });

  it('says what is wrong before the server is asked', () => {
    component.addTier();
    component.updateTier(1, 'minSides', 50);
    component.updateTier(1, 'discountPercent', 120);

    expect(component.problems()).toContain('pricing.invalid.tierDuplicate');
    expect(component.problems()).toContain('pricing.invalid.tierPercent');
    expect(component.canSave()).toBe(false);
  });

  it('prices an order with the values still on the form', async () => {
    component.update('color', 0.5);
    component.updateCalculator('color', true);
    component.updateCalculator('pageCount', 4);

    const calculating = component.calculate();
    const request = backend.expectOne('/api/admin/pricing-profiles/calculate');

    expect(request.request.body.profileId).toBeNull();
    expect(request.request.body.draft.colorPricePerSide).toBe(0.5);
    expect(request.request.body.options).toMatchObject({ pageCount: 4, color: true, paper: 'A4' });

    request.flush({
      pages: 4,
      sides: 4,
      sheets: 4,
      copies: 1,
      color: true,
      duplex: false,
      paper: 'A4',
      pricePerSide: 0.5,
      paperMultiplier: 1,
      basePrice: 2,
      discountPercent: 0,
      volumeDiscount: 0,
      promoDiscount: 0,
      bonusPages: 0,
      minimumApplied: false,
      finalPrice: 2,
      currency: 'AZN',
    });
    await calculating;

    expect(component.quote()?.finalPrice).toBe(2);
  });

  it('sends the tiers in order when saving', async () => {
    component.addTier();
    component.updateTier(1, 'minSides', 20);

    const saving = component.save();
    const request = backend.expectOne('/api/admin/pricing-profiles/p1');

    expect(request.request.method).toBe('PUT');
    expect(
      request.request.body.volumeTiers.map((tier: { minSides: number }) => tier.minSides),
    ).toEqual([20, 50]);

    request.flush(standard);
    await settle(fixture);
    backend.expectOne('/api/admin/pricing-profiles').flush([standard]);
    await settle(fixture);
    backend.expectOne('/api/admin/pricing-profiles/p1').flush({ profile: standard, kiosks: [] });
    await saving;
  });
});
