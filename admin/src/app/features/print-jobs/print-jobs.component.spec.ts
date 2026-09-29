import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { of } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';

import { PrintJob, PrintJobDetail } from '../../core/api/step3.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { PrintJobDetailComponent } from './print-job-detail.component';
import { PrintJobsComponent } from './print-jobs.component';

const kioskId = '11111111-1111-1111-1111-111111111111';

const hub = { on: () => () => undefined };

function setUp(permissions: string[], dialog?: unknown): HttpTestingController {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      { provide: AdminHubService, useValue: hub },
      {
        provide: AuthService,
        useValue: {
          can: (permission: string) => permissions.includes(permission),
          canAny: (list: readonly string[]) =>
            list.some((permission) => permissions.includes(permission)),
        },
      },
      ...(dialog ? [{ provide: MatDialog, useValue: dialog }] : []),
    ],
  });

  return TestBed.inject(HttpTestingController);
}

const job: PrintJob = {
  id: 'j1',
  jobCode: 'A1B2C3',
  kioskId,
  status: 'Queued',
  source: 'Flash',
  fileName: 'report.pdf',
  pageCount: 4,
  selectedPages: 4,
  options: {
    copies: 1,
    pageRange: null,
    color: false,
    duplex: false,
    paper: 'A4',
    orientation: 'Auto',
    pagesPerSheet: 1,
    scaling: 'Fit',
  },
  price: {
    total: 0.4,
    currency: 'AZN',
    basePrice: 0.4,
    volumeDiscount: 0,
    discountPercent: 0,
    minimumApplied: false,
    sides: 4,
    sheets: 4,
    profileName: 'Default',
    takenAt: '2026-09-28T10:00:00Z',
  },
  requiredSheets: 4,
  queuePosition: 1,
  printedSides: 0,
  printedSheets: 0,
  errorReason: null,
  cancelReason: null,
  paymentSkipped: true,
  createdAt: '2026-09-28T10:00:00Z',
  queuedAt: '2026-09-28T10:00:01Z',
  finishedAt: null,
};

describe('PrintJobsComponent', () => {
  let backend: HttpTestingController;

  afterEach(() => backend.verify());

  it('in a kiosk tab asks only for that kiosk and does not load the kiosk list', async () => {
    backend = setUp([Permissions.jobsView, Permissions.kiosksView]);

    const fixture = TestBed.createComponent(PrintJobsComponent);
    fixture.componentRef.setInput('kioskId', kioskId);
    fixture.detectChanges();

    const request = backend.expectOne((call) => call.url === '/api/admin/print-jobs');
    expect(request.request.params.get('kioskId')).toBe(kioskId);
    expect(request.request.params.get('page')).toBe('1');
    request.flush({ items: [], total: 0, page: 1, pageSize: 20 });
    await fixture.whenStable();

    expect(fixture.componentInstance.embedded()).toBe(true);
    expect(fixture.componentInstance.columns()).not.toContain('kiosk');
  });

  it('sends the chosen filters and starts again from the first page', async () => {
    backend = setUp([Permissions.jobsView]);

    const fixture = TestBed.createComponent(PrintJobsComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();

    backend
      .expectOne((call) => call.url === '/api/admin/print-jobs')
      .flush({ items: [job], total: 45, page: 1, pageSize: 20 });
    await fixture.whenStable();

    expect(component.pages()).toBe(3);

    component.go(3);
    backend
      .expectOne((call) => call.url === '/api/admin/print-jobs' && call.params.get('page') === '3')
      .flush({ items: [], total: 45, page: 3, pageSize: 20 });
    await fixture.whenStable();

    component.statuses.set(['Queued', 'Interrupted']);
    component.code.set(' A1B2 ');
    component.search();

    const request = backend.expectOne((call) => call.url === '/api/admin/print-jobs');
    expect(request.request.params.get('status')).toBe('Queued,Interrupted');
    expect(request.request.params.get('code')).toBe('A1B2');
    expect(request.request.params.get('page')).toBe('1');
    request.flush({ items: [], total: 0, page: 1, pageSize: 20 });
    await fixture.whenStable();

    expect(component.filtersActive()).toBe(true);

    // Leaving the code field without changing anything does not ask again.
    component.search();
    backend.expectNone((call) => call.url === '/api/admin/print-jobs');
  });

  it('asks for whole days of the chosen period and ignores a date it could not read', async () => {
    backend = setUp([Permissions.jobsView]);

    const fixture = TestBed.createComponent(PrintJobsComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();

    backend
      .expectOne((call) => call.url === '/api/admin/print-jobs')
      .flush({ items: [], total: 0, page: 1, pageSize: 20 });
    await fixture.whenStable();

    component.from.set(component.validDate(new Date(2026, 8, 27, 15, 30)));
    component.to.set(component.validDate(new Date(2026, 8, 29)));
    component.search();

    const request = backend.expectOne((call) => call.url === '/api/admin/print-jobs');
    expect(request.request.params.get('from')).toBe(new Date(2026, 8, 27).toISOString());
    expect(request.request.params.get('to')).toBe(
      new Date(2026, 8, 29, 23, 59, 59, 999).toISOString(),
    );
    request.flush({ items: [], total: 0, page: 1, pageSize: 20 });
    await fixture.whenStable();

    expect(component.validDate(new Date(Number.NaN))).toBeNull();
    expect(component.filtersActive()).toBe(true);

    component.clearFilters();
    const cleared = backend.expectOne((call) => call.url === '/api/admin/print-jobs');
    expect(cleared.request.params.has('from')).toBe(false);
    expect(cleared.request.params.has('to')).toBe(false);
    cleared.flush({ items: [], total: 0, page: 1, pageSize: 20 });
  });
});

describe('PrintJobDetailComponent', () => {
  let backend: HttpTestingController;

  afterEach(() => backend.verify());

  async function open(status: string, permissions: string[], dialog?: unknown) {
    backend = setUp(permissions, dialog);

    const fixture = TestBed.createComponent(PrintJobDetailComponent);
    fixture.componentRef.setInput('id', 'j1');
    fixture.detectChanges();

    const detail: PrintJobDetail = {
      job: { ...job, status },
      kioskCode: 'KSK-0001',
      history: [],
      attempts: [],
    };
    backend.expectOne('/api/admin/print-jobs/j1').flush(detail);
    await fixture.whenStable();

    return fixture.componentInstance;
  }

  it('offers cancelling only for a job still waiting, and only with the permission', async () => {
    expect((await open('Queued', [Permissions.jobsView, Permissions.jobsCancel])).canCancel()).toBe(
      true,
    );
    TestBed.resetTestingModule();
    expect(
      (await open('Printing', [Permissions.jobsView, Permissions.jobsCancel])).canCancel(),
    ).toBe(false);
    TestBed.resetTestingModule();
    expect((await open('Paid', [Permissions.jobsView, Permissions.jobsCancel])).canCancel()).toBe(
      false,
    );
    TestBed.resetTestingModule();
    expect((await open('Queued', [Permissions.jobsView])).canCancel()).toBe(false);
  });

  it('cancels with the reason given, under an idempotency key', async () => {
    const dialog = { open: () => ({ afterClosed: () => of({ reason: 'Customer left' }) }) };
    const component = await open('Queued', [Permissions.jobsView, Permissions.jobsCancel], dialog);

    const cancelling = component.cancel();
    await new Promise((resolve) => setTimeout(resolve, 0));

    const request = backend.expectOne('/api/admin/print-jobs/j1/cancel');
    expect(request.request.headers.has('Idempotency-Key')).toBe(true);
    expect(request.request.body).toEqual({ reason: 'Customer left' });
    request.flush({ ...job, status: 'Cancelled' });
    await new Promise((resolve) => setTimeout(resolve, 0));

    backend
      .expectOne('/api/admin/print-jobs/j1')
      .flush({
        job: { ...job, status: 'Cancelled' },
        kioskCode: 'KSK-0001',
        history: [],
        attempts: [],
      });
    await cancelling;

    expect(component.detail()?.job.status).toBe('Cancelled');
    expect(component.canCancel()).toBe(false);
  });

  it('says what the printer counter showed, or that there was none', async () => {
    const component = await open('Completed', [Permissions.jobsView]);
    const attempt = {
      number: 1,
      printer: 'HP',
      spoolerJobId: '12',
      startedAt: null,
      finishedAt: null,
      counterBefore: 100,
      counterAfter: 104,
      counterUnit: 'sheets' as const,
      reportedSides: 4,
      reportedSheets: 4,
      status: 'Completed',
      error: null,
      recovered: false,
    };

    // No translations are loaded here, so the keys show which sentence was chosen.
    expect(component.counterText(attempt)).toBe('jobs.counter jobs.counterUnit.sheets');
    expect(component.counterText({ ...attempt, counterUnit: null })).toBe('jobs.counter');
    expect(component.counterText({ ...attempt, counterAfter: null })).toBe('jobs.noCounter');
    // The API leaves null fields out of the JSON.
    const withoutCounter: Partial<typeof attempt> = { ...attempt };
    delete withoutCounter.counterBefore;
    delete withoutCounter.counterAfter;
    expect(component.counterText(withoutCounter as typeof attempt)).toBe('jobs.noCounter');
  });
});
