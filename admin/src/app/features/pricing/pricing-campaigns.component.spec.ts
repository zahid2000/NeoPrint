import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { PricingCampaign } from '../../core/api/step2.models';
import { CampaignDialogComponent } from './campaign-dialog.component';
import { PricingCampaignsComponent } from './pricing-campaigns.component';

const november: PricingCampaign = {
  id: 'c1',
  name: 'Noyabr',
  kind: 'Percent',
  percent: 20,
  bwPricePerSide: null,
  colorPricePerSide: null,
  volumeTiers: [],
  tierMode: 'WholeOrder',
  startsAt: '2026-11-20T00:00:00Z',
  endsAt: null,
  kioskIds: [],
  isActive: true,
  state: 'Running',
  updatedAt: '2026-11-16T10:00:00Z',
  revision: 1,
};

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
  await fixture.whenStable();
}

const providers = () => [
  provideHttpClient(),
  provideHttpClientTesting(),
  provideRouter([]),
  provideNoopAnimations(),
  provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
];

/** T-436: the "Kampaniyalar" tab and its dialog. */
describe('PricingCampaignsComponent', () => {
  let fixture: ComponentFixture<PricingCampaignsComponent>;
  let backend: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: providers() });
    backend = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(PricingCampaignsComponent);

    backend.expectOne('/api/admin/pricing-campaigns').flush([november]);
    await settle(fixture);
    fixture.detectChanges();
  });

  afterEach(() => backend.verify());

  it('lists each campaign with its value, period, kiosks and state', () => {
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    expect(text).toContain('Noyabr');
    expect(text).toContain('−20%');
    expect(text).toContain('campaigns.noEnd');
    expect(text).toContain('campaigns.allKiosks');
    expect(text).toContain('campaigns.state.Running');
    expect(fixture.componentInstance.look('Running')).toEqual({
      tone: 'success',
      icon: 'play_circle',
    });
  });
});

describe('CampaignDialogComponent', () => {
  let dialog: CampaignDialogComponent;
  let backend: HttpTestingController;
  let closed: unknown;

  beforeEach(() => {
    closed = undefined;
    TestBed.configureTestingModule({
      providers: [
        ...providers(),
        { provide: MAT_DIALOG_DATA, useValue: { campaign: null, kiosks: [] } },
        { provide: MatDialogRef, useValue: { close: (value: unknown) => (closed = value) } },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
    dialog = TestBed.createComponent(CampaignDialogComponent).componentInstance;
  });

  afterEach(() => backend.verify());

  it('says what is wrong before the server is asked', () => {
    dialog.name.set(' ');
    dialog.percent.set(0);
    dialog.startsAt.set(new Date(2026, 10, 30));
    dialog.endsAt.set(new Date(2026, 10, 20));

    expect(dialog.problems()).toEqual([
      'campaigns.invalid.name',
      'campaigns.invalid.percent',
      'campaigns.invalid.dates',
    ]);
  });

  it('sends a price campaign with its prices and ladder, and no dates as no dates', async () => {
    dialog.name.set('Tələbə');
    dialog.kind.set('Price');
    dialog.bw.set(0.08);
    dialog.color.set(0.25);
    dialog.tierMode.set('Graduated');
    dialog.tiers.set([{ minSides: 11, kind: 'Price', discountPercent: 0, bw: 0.05, color: 0.2 }]);

    const saving = dialog.save();
    const request = backend.expectOne('/api/admin/pricing-campaigns');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toMatchObject({
      name: 'Tələbə',
      kind: 'Price',
      percent: 0,
      bwPricePerSide: 0.08,
      colorPricePerSide: 0.25,
      tierMode: 'Graduated',
      startsAt: null,
      endsAt: null,
      kioskIds: [],
      isActive: true,
    });
    expect(request.request.body.volumeTiers).toEqual([
      { minSides: 11, discountPercent: 0, bwPricePerSide: 0.05, colorPricePerSide: 0.2 },
    ]);

    request.flush({ ...november, kind: 'Price' });
    await saving;
    expect(closed).toBe(true);
  });
});
