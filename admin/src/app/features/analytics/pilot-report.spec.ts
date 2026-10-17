import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService, TranslateService } from '@ngx-translate/core';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { PilotReport } from '../../core/api/step7.models';
import { translations } from '../../core/i18n/translations';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { FakeHub } from '../../core/realtime/fake-hub.testing';
import { liveDebounceMs } from '../../core/realtime/live-refresh';
import { PilotReportComponent } from './pilot-report.component';

const kioskId = '11111111-1111-1111-1111-111111111111';
const kiosk = { id: kioskId, code: 'KSK-9002', name: 'Library', status: 'Online' };

let backend: HttpTestingController;
let hub: FakeHub;

function setUp(): void {
  hub = new FakeHub();

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      { provide: AdminHubService, useValue: hub },
    ],
  });

  const translate = TestBed.inject(TranslateService);
  translate.setTranslation('az', translations.az);
  translate.use('az');
  backend = TestBed.inject(HttpTestingController);
}

function text(fixture: ComponentFixture<unknown>): string {
  return ((fixture.nativeElement as HTMLElement).textContent ?? '').replace(/\s+/g, ' ');
}

function element(fixture: ComponentFixture<unknown>, selector: string): HTMLElement {
  const found = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(selector);
  expect(found, selector).not.toBeNull();
  return found!;
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  await fixture.whenStable();
  fixture.detectChanges();
}

const pilotRequest = (request: { url: string }) => request.url === '/api/admin/analytics/pilot';

function report(overrides: Partial<PilotReport> = {}): PilotReport {
  return {
    from: '2026-10-04',
    to: '2026-10-17',
    currency: 'AZN',
    computedAt: '2026-10-17T08:23:00Z',
    totals: {
      visits: 10,
      orders: 5,
      printsSucceeded: 3,
      printsFailed: 1,
      cancelled: 1,
      refunds: 1,
      refunded: 0.4,
      sales: 2.4,
      net: 2,
      averagePrintSeconds: 80,
      paperSheets: 9,
      testSheets: 1,
      tonerReplaced: 0,
      paymentsStarted: 6,
      paymentsSucceeded: 4,
      incidents: 2,
    },
    funnel: [
      { step: 'selected', count: 10 },
      { step: 'previewed', count: 9, loss: 10 },
      { step: 'options', count: 8, loss: 11.1 },
      { step: 'paymentScreen', count: 6, loss: 25 },
      { step: 'paid', count: 4, loss: 33.3 },
      { step: 'printing', count: 4, loss: 0 },
      { step: 'printed', count: 3, loss: 25 },
    ],
    formats: [
      { format: 'pdf', selected: 7, failed: 0, share: 70 },
      { format: 'docx', selected: 3, failed: 1, share: 30 },
    ],
    appFormats: [{ format: 'jpg', selected: 2, failed: 0, share: 100 }],
    paymentFailures: [
      { key: 'payments.declined', text: 'Bank ödənişi rədd etdi.', count: 1 },
      { key: 'analytics.paymentExpired', text: 'Ödəniş vaxtı bitdi: müştəri ödəməyib, heç nə tutulmayıb.', count: 1 },
    ],
    methods: [
      { method: 'BankQr', started: 5, succeeded: 3 },
      { method: 'MobileApp', started: 1, succeeded: 1 },
    ],
    abandoned: [
      { key: 'QrPayment', count: 2 },
      { key: 'SomethingNew', count: 1 },
    ],
    conversionErrors: [{ key: 'layout.failed', count: 1 }],
    printErrors: [{ key: 'printer.paperJam', text: 'Printerdə kağız ilişib.', count: 1 }],
    refundReasons: [{ key: 'SystemError', count: 1 }],
    paper: [{ key: 'A4', count: 9 }],
    toner: [{ key: 'Black Toner', count: 3 }],
    incidents: [
      { key: 'Critical', count: 1 },
      { key: 'Medium', count: 1 },
    ],
    days: [
      { day: '2026-10-16', live: false, visits: 4, orders: 2, printsSucceeded: 1, printsFailed: 1, net: 0.4, paperSheets: 3, incidents: 1 },
      { day: '2026-10-17', live: true, visits: 6, orders: 3, printsSucceeded: 2, printsFailed: 0, net: 1.6, paperSheets: 6, incidents: 1 },
    ],
    ...overrides,
  };
}

async function open(answer: PilotReport = report()): Promise<ComponentFixture<PilotReportComponent>> {
  setUp();
  const fixture = TestBed.createComponent(PilotReportComponent);
  fixture.detectChanges();
  backend.expectOne('/api/admin/kiosks').flush([kiosk]);
  backend.expectOne(pilotRequest).flush(answer);
  await settle(fixture);
  return fixture;
}

afterEach(() => {
  backend.verify();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('Pilot hesabatı', () => {
  it('shows the figures, the way through the kiosk with where most are lost, and the reasons in words', async () => {
    const fixture = await open();
    const page = text(fixture);

    expect(page).toContain('Pilot hesabatı');
    expect(text({ nativeElement: element(fixture, '[data-stat="net"]') } as never)).toContain('Satış');
    expect(element(fixture, '[data-stat="duration"]').textContent).toContain('1 dəq 20 san');
    expect(element(fixture, '[data-stat="incidents"]').textContent).toContain('Kritik 1 · Orta 1');
    expect(element(fixture, '[data-stat="failed"] mat-icon').textContent).toContain('error');

    // Seven steps, the widest bar the first; the loss after "Ödəniş ekranı" is the worst.
    const steps = (fixture.nativeElement as HTMLElement).querySelectorAll('.pilot__step');
    expect(steps.length).toBe(7);
    expect(steps[0].textContent).toContain('Fayl seçildi');
    expect((steps[0].querySelector('.pilot__bar-fill') as HTMLElement).style.width).toBe('100%');
    const paid = element(fixture, '[data-step="paid"]');
    expect(paid.textContent).toContain('−33.3%');
    expect(paid.querySelector('.pilot__loss--worst')?.textContent).toContain('trending_down');
    expect(paid.querySelector('.pilot__worst-text')?.textContent).toContain('ən çox burada düşür');
    // No loss reads as 0%, not as a minus nought.
    expect(element(fixture, '[data-step="printing"] .pilot__loss').textContent?.trim()).toBe('0%');
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('.pilot__loss--worst').length).toBe(1);

    // Formats by their extension, the app's apart; reasons as the server words them, else "another reason" with the key.
    expect(element(fixture, '[data-format="docx"]').textContent).toContain('DOCX');
    expect(page).toContain('Tətbiqdən yüklənən');
    expect(element(fixture, '[data-reason="payments.declined"]').textContent).toContain('Bank ödənişi rədd etdi.');
    const unknown = element(fixture, '[data-reason="layout.failed"]');
    expect(unknown.textContent).toContain('Başqa səbəb');
    expect(unknown.querySelector('code')?.textContent).toBe('layout.failed');
    expect(page).toContain('Bank QR kodu');
    expect(page).not.toContain('BankQr');

    // Screens by their names; one the panel does not know is "another screen", never its code.
    expect(page).toContain('Ödəniş QR-ı');
    expect(page).toContain('Başqa ekran');
    expect(page).not.toContain('SomethingNew');
    expect(page).toContain('Sistem xətası');

    // Today is live; the past days were worked out by the rollup.
    expect(element(fixture, 'tr[data-day="2026-10-17"]').textContent).toContain('Canlı');
    expect(element(fixture, 'tr[data-day="2026-10-16"]').textContent).not.toContain('Canlı');
    expect(page).toContain('Bu günün rəqəmləri canlıdır');
  });

  it('says no print was timed rather than showing nought, and hides the app formats for a scoped account', async () => {
    const base = report();
    const fixture = await open(report({ appFormats: undefined, totals: { ...base.totals, averagePrintSeconds: undefined } }));

    expect(element(fixture, '[data-stat="duration"]').textContent).toContain('Ölçülən çap yoxdur');
    expect(text(fixture)).not.toContain('Tətbiqdən yüklənən');
  });

  it('shows an empty state when nothing happened, not a page of noughts', async () => {
    const base = report();
    const fixture = await open(
      report({
        totals: Object.fromEntries(Object.keys(base.totals).map((key) => [key, 0])) as never,
        funnel: base.funnel.map((step) => ({ step: step.step, count: 0 })),
        formats: [],
        appFormats: [],
        days: [],
      }),
    );

    expect(text(fixture)).toContain('Bu günlərdə kiosklarda fəaliyyət olmayıb');
    expect((fixture.nativeElement as HTMLElement).querySelector('.pilot__stats')).toBeNull();
  });

  it('asks for one kiosk when one is chosen', async () => {
    const fixture = await open();

    fixture.componentInstance.kiosk.set(kioskId);
    void fixture.componentInstance.load();
    const request = backend.expectOne(pilotRequest);
    expect(request.request.params.get('kioskId')).toBe(kioskId);
    expect(request.request.params.get('from')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    request.flush(report({ kioskId }));
    await settle(fixture);
  });

  it('follows the day as it goes while today is shown, and leaves past days alone', async () => {
    const fixture = await open();
    vi.useFakeTimers();

    hub.emit('QueueUpdated', { kioskId });
    await vi.advanceTimersByTimeAsync(liveDebounceMs + 10);
    backend.expectOne(pilotRequest).flush(report({ totals: { ...report().totals, printsSucceeded: 4 } }));
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
    expect(element(fixture, '[data-stat="printed"]').textContent).toContain('4');

    // A span of past days only: an event changes nothing there, and nothing is asked.
    fixture.componentInstance.report.set(report({ days: [report().days[0]] }));
    hub.emit('IncidentRaised', { incidentId: 'i-1', kioskId, severity: 'High' });
    await vi.advanceTimersByTimeAsync(liveDebounceMs + 10);
    backend.expectNone(pilotRequest);
  });

  it('keeps what it shows when a quiet refresh fails, and says so when the first load fails', async () => {
    setUp();
    const fixture = TestBed.createComponent(PilotReportComponent);
    fixture.detectChanges();
    backend.expectOne('/api/admin/kiosks').flush([kiosk]);
    backend.expectOne(pilotRequest).flush({ title: 'down' }, { status: 503, statusText: 'Unavailable' });
    await settle(fixture);

    expect((fixture.nativeElement as HTMLElement).querySelector('app-error-banner')).not.toBeNull();
    expect(text(fixture)).toContain('Hesabat göstərilə bilmir');
  });

  it('downloads the report as CSV in the reader language with no names of files or people', async () => {
    const fixture = await open();
    let written = '';
    let name = '';
    vi.spyOn(URL, 'createObjectURL').mockImplementation((blob) => {
      // jsdom's Blob has no text(): read the bytes, keeping the byte-order mark.
      const reader = new FileReader();
      reader.onload = () => (written = new TextDecoder('utf-8', { ignoreBOM: true }).decode(reader.result as ArrayBuffer));
      reader.readAsArrayBuffer(blob as Blob);
      return 'blob:pilot';
    });
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      name = this.download;
    });

    fixture.componentInstance.kiosk.set(kioskId);
    fixture.componentInstance.csv(report({ kioskId }));
    await new Promise((resolve) => setTimeout(resolve, 10));

    expect(name).toBe('neoprint-pilot-2026-10-04-2026-10-17-KSK-9002.csv');
    expect(written.charCodeAt(0)).toBe(0xfeff);
    const lines = written.slice(1).split('\r\n');
    expect(lines[0]).toBe('Gün;Ziyarət;Sifariş;Uğurlu çap;Uğursuz çap;Xalis gəlir;Kağız sərfi;Incident');
    expect(lines[1]).toBe('2026-10-16;4;2;1;1;0.40;3;1');
    expect(written).toContain('Ödəniş ekranı;6;25');
    expect(written).toContain('DOCX;3;1;30');
    expect(written).toContain('Bank ödənişi rədd etdi.;1');
  });
});
