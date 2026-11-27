import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService, TranslateService } from '@ngx-translate/core';
import { afterEach, describe, expect, it } from 'vitest';

import { MonitoringStatus } from '../../core/api/step7.models';
import { translations } from '../../core/i18n/translations';
import { SystemMonitoringComponent } from './system-monitoring.component';

let backend: HttpTestingController;

function setUp(): void {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
    ],
  });
  TestBed.inject(TranslateService).setTranslation('az', translations.az);
  TestBed.inject(TranslateService).use('az');
  backend = TestBed.inject(HttpTestingController);
}

async function render(status: MonitoringStatus): Promise<ComponentFixture<SystemMonitoringComponent>> {
  const fixture = TestBed.createComponent(SystemMonitoringComponent);
  fixture.detectChanges();
  backend.expectOne('/api/admin/monitoring').flush(status);
  await new Promise((resolve) => setTimeout(resolve));
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture;
}

const host = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;

describe('SystemMonitoringComponent (T-477)', () => {
  afterEach(() => backend.verify());

  it('links to Grafana and to each dashboard when metrics flow', async () => {
    setUp();
    const fixture = await render({ metricsEnabled: true, grafanaUrl: 'https://grafana.example.test/', countedAt: '2026-11-27T10:00:00Z' });

    expect(host(fixture).textContent).toContain('Metrikalar göndərilir');
    expect(host(fixture).querySelector<HTMLAnchorElement>('[data-test="open"]')?.href).toBe('https://grafana.example.test/');
    expect(host(fixture).querySelector<HTMLAnchorElement>('[data-dashboard="queues"]')?.href).toBe(
      'https://grafana.example.test/d/neoprint-queues',
    );
    expect(host(fixture).querySelectorAll('[data-dashboard]')).toHaveLength(4);
  });

  it('says what is missing when metrics are off and Grafana is not configured', async () => {
    setUp();
    const fixture = await render({ metricsEnabled: false, grafanaUrl: null, countedAt: null });

    expect(host(fixture).textContent).toContain('Metrikalar göndərilmir');
    expect(host(fixture).textContent).toContain('Monitoring__OtlpEndpoint');
    expect(host(fixture).querySelector('[data-test="open"]')).toBeNull();
    expect(host(fixture).querySelector('[data-test="no-grafana"]')).not.toBeNull();
  });
});
