import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { MoneyApiService } from '../../core/api/money-api.service';
import { moneyTranslations } from '../../core/i18n/money.i18n';
import { PaymentDetail, RefundOptions } from '../../core/api/step4.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { FiscalComponent } from './fiscal.component';
import { PaymentDetailComponent } from './payment-detail.component';
import { PaymentsComponent } from './payments.component';
import { PromosComponent } from './promos.component';
import { RefundsComponent } from './refunds.component';
import { RefundDialogComponent } from './refund-dialog.component';

function setUp(permissions: string[], extra: unknown[] = []): HttpTestingController {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
      {
        provide: AuthService,
        useValue: {
          can: (permission: string) => permissions.includes(permission),
          canAny: (list: readonly string[]) => list.some((p) => permissions.includes(p)),
        },
      },
      ...(extra as never[]),
    ],
  });

  return TestBed.inject(HttpTestingController);
}

const options: RefundOptions = {
  paymentId: 'p1',
  method: 'BankQr',
  hasAccount: false,
  refundable: 0.3,
  externalRefundable: 0.3,
  walletRefundable: 0,
  bonusPagesRefundable: 0,
  externalDestinations: ['OriginalMethod', 'Voucher'],
  jobCancellable: true,
};

const detail: PaymentDetail = {
  payment: {
    id: 'p1',
    status: 'Succeeded',
    method: 'BankQr',
    channel: 'Kiosk',
    purpose: 'PrintJob',
    amount: 0.3,
    refunded: 0,
    currency: 'AZN',
    kioskCode: 'KSK-9001',
    jobId: 'j1',
    jobCode: '123456',
    createdAt: '2026-10-01T10:00:00Z',
  },
  split: {
    gross: 0.3,
    volumeDiscount: 0,
    promoDiscount: 0,
    external: 0.3,
    wallet: 0,
    bonusPages: 0,
    bonusValue: 0,
  },
  refundable: 0.3,
  expiresAt: '2026-10-01T10:05:00Z',
  fiscal: [{ id: 'f1', type: 'Sale', status: 'Issued', amount: 0.3 }],
  refunds: [
    {
      id: 'r1',
      amount: 0.3,
      bonusPages: 0,
      destination: 'OriginalMethod',
      status: 'Failed',
      reason: 'SystemError',
      initiatedBy: 'System',
      attempts: 3,
      lastError: 'Bank rədd etdi.',
      createdAt: '2026-10-01T10:01:00Z',
    },
  ],
  timeline: [
    { at: '2026-10-01T10:00:00Z', event: 'created' },
    { at: '2026-10-01T10:00:01Z', event: 'somethingNew' },
  ],
};

describe('money pages', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('sends repeated status filters the way the API binds arrays', () => {
    const http = setUp([]);
    const api = TestBed.inject(MoneyApiService);

    api
      .listPayments({
        statuses: ['Failed', 'RefundFailed'],
        refund: 'failed',
        hadError: true,
        page: 2,
        pageSize: 20,
      })
      .subscribe();

    const request = http.expectOne((r) => r.url === '/api/admin/payments');
    expect(request.request.params.getAll('status')).toEqual(['Failed', 'RefundFailed']);
    expect(request.request.params.get('refund')).toBe('failed');
    expect(request.request.params.get('hadError')).toBe('true');
    expect(request.request.params.get('page')).toBe('2');
    request.flush({ items: [], total: 0, page: 2, pageSize: 20 });
  });

  it('a link from the dashboard opens the list with the failed refunds', () => {
    const http = setUp([]);
    const fixture = TestBed.createComponent(PaymentsComponent);
    fixture.componentInstance.refund.set('failed');
    fixture.componentInstance.load();

    const request = http.expectOne((r) => r.url === '/api/admin/payments');
    expect(request.request.params.get('refund')).toBe('failed');
    request.flush({ items: [], total: 0, page: 1, pageSize: 20 });
  });

  it('the payment page shows the split, the failed refund with a retry, and unknown events as they are', async () => {
    const http = setUp([Permissions.paymentsRefund, Permissions.refundsRetry]);
    TestBed.inject(TranslateService).setTranslation('az', moneyTranslations.az);
    const fixture = TestBed.createComponent(PaymentDetailComponent);
    fixture.componentRef.setInput('id', 'p1');
    fixture.detectChanges();

    http.expectOne('/api/admin/payments/p1').flush(detail);
    await fixture.whenStable();
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Bank rədd etdi.');
    expect(text).toContain('somethingNew');
    expect(fixture.componentInstance.retryOptions()).toEqual(['OriginalMethod', 'Voucher']);
    expect(fixture.componentInstance.eventText('created')).toBe('Ödəniş açıldı');
    expect(text).toContain('Yenidən göndər');
  });

  it('the refund dialog offers only what the payment allows and needs a reason', () => {
    const http = setUp(
      [],
      [
        { provide: MAT_DIALOG_DATA, useValue: { paymentId: 'p1', options } },
        { provide: MatDialogRef, useValue: { close: vi.fn() } },
      ],
    );
    const dialog = TestBed.createComponent(RefundDialogComponent).componentInstance;

    expect(dialog.destinations()).toEqual(['OriginalMethod', 'Voucher']);
    expect(dialog.canSubmit()).toBe(false);

    dialog.reason.set('Printer kağız sıxdı');
    expect(dialog.canSubmit()).toBe(true);
    expect(dialog.total()).toBe(0.3);

    dialog.partial.set(true);
    dialog.amount.set(0.5);
    expect(dialog.canSubmit()).toBe(false);

    dialog.amount.set(0.1);
    dialog.destination.set('Voucher');
    dialog.cancelJob.set(true);
    void dialog.submit();

    const request = http.expectOne('/api/admin/payments/p1/refund');
    expect(request.request.body).toEqual({
      amount: 0.1,
      destination: 'Voucher',
      reason: 'Printer kağız sıxdı',
      cancelJob: true,
    });
    expect(request.request.headers.has('Idempotency-Key')).toBe(true);
    request.flush({});
  });

  it('the fiscal page puts the open incidents first and retries the receipt they are about (T-129)', async () => {
    const http = setUp([Permissions.fiscalView, Permissions.fiscalManage, Permissions.incidentsView]);
    TestBed.inject(TranslateService).setTranslation('az', moneyTranslations.az);
    const fixture = TestBed.createComponent(FiscalComponent);
    fixture.detectChanges();

    http.expectOne('/api/admin/fiscal/settings').flush([]);
    http.expectOne((r) => r.url === '/api/admin/fiscal-receipts').flush({
      items: [
        { id: 'r-wait', type: 'Refund', status: 'Pending', amount: 0.3, currency: 'AZN', attempts: 4, createdAt: '2026-10-02T05:00:00Z' },
        { id: 'r-new', type: 'Sale', status: 'Pending', amount: 0.3, currency: 'AZN', attempts: 0, createdAt: '2026-10-02T05:00:00Z' },
      ],
      total: 2,
      page: 1,
      pageSize: 20,
    });
    const incidents = http.expectOne((r) => r.url === '/api/admin/incidents');
    expect(incidents.request.params.getAll('status')).toEqual(['Open', 'Acknowledged']);
    expect(incidents.request.params.getAll('type')).toEqual(['FiscalFailed']);
    incidents.flush({
      items: [
        {
          id: 'i1',
          type: 'FiscalFailed',
          severity: 'High',
          status: 'Open',
          details: 'E-kassa 0.3 ₼ məbləğində Sale fiskal çekini rədd etdi.',
          entityType: 'FiscalReceipt',
          entityId: 'r-failed',
          createdAt: '2026-10-02T05:00:00Z',
        },
      ],
      total: 1,
      page: 1,
      pageSize: 50,
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const page = fixture.componentInstance;
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Açıq incident-lər (1)');
    expect(text).toContain('E-kassa 0.3 ₼ məbləğində Sale fiskal çekini rədd etdi.');
    expect(text).toContain('Yüksək');
    // A receipt the e-kassa has not answered can be tried now; a fresh one is left alone.
    expect(page.canRetry(page.rows()[0]!)).toBe(true);
    expect(page.canRetry(page.rows()[1]!)).toBe(false);

    const button = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.incidents button')][0] as HTMLButtonElement;
    expect(button.textContent?.trim()).toBe('Təkrar cəhd');
    button.click();
    const retry = http.expectOne('/api/admin/fiscal-receipts/r-failed/retry');
    expect(retry.request.method).toBe('POST');
    retry.flush({});
    await fixture.whenStable();
    http.expectOne((r) => r.url === '/api/admin/fiscal-receipts').flush({ items: [], total: 0, page: 1, pageSize: 20 });
    http.expectOne((r) => r.url === '/api/admin/incidents').flush({ items: [], total: 0, page: 1, pageSize: 50 });
    await fixture.whenStable();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('.incidents')).toBeNull();
  });

  it('without the incidents permission the fiscal page does not ask for them', () => {
    const http = setUp([Permissions.fiscalView]);
    const fixture = TestBed.createComponent(FiscalComponent);
    fixture.detectChanges();

    http.expectOne((r) => r.url === '/api/admin/fiscal-receipts').flush({ items: [], total: 0, page: 1, pageSize: 20 });
    http.expectNone((r) => r.url === '/api/admin/incidents');
  });

  it('the payments list filters by channel (T-138)', () => {
    const http = setUp([]);
    const fixture = TestBed.createComponent(PaymentsComponent);
    fixture.componentInstance.channel.set('Mobile');
    fixture.componentInstance.load();

    const request = http.expectOne((r) => r.url === '/api/admin/payments');
    expect(request.request.params.get('channel')).toBe('Mobile');
    request.flush({ items: [], total: 0, page: 1, pageSize: 20 });
    expect(fixture.componentInstance.filtersActive()).toBe(true);
  });

  it('the refunds list filters by status and reason; the failed tab keeps its own status (T-137)', async () => {
    const http = setUp([]);
    const page = TestBed.createComponent(RefundsComponent).componentInstance;
    page.statuses.set(['Succeeded']);
    page.reasons.set(['SystemError', 'AdminManual']);
    void page.apply();

    const all = http.match((r) => r.url === '/api/admin/refunds');
    const list = all.find((r) => r.request.params.get('pageSize') === '20')!;
    expect(list.request.params.getAll('status')).toEqual(['Succeeded']);
    expect(list.request.params.getAll('reason')).toEqual(['SystemError', 'AdminManual']);
    all.forEach((r) => r.flush({ items: [], total: 0, page: 1, pageSize: 20 }));
    await Promise.resolve();

    page.tab(1);
    const failed = http.match((r) => r.url === '/api/admin/refunds');
    const failedList = failed.find((r) => r.request.params.get('pageSize') === '20')!;
    expect(failedList.request.params.getAll('status')).toEqual(['Failed']);
    expect(failedList.request.params.getAll('reason')).toEqual(['SystemError', 'AdminManual']);
    failed.forEach((r) => r.flush({ items: [], total: 0, page: 1, pageSize: 20 }));
  });

  it('the promo list says when a code no longer works and where it applies (T-139)', async () => {
    const http = setUp([]);
    TestBed.inject(TranslateService).setTranslation('az', moneyTranslations.az);
    TestBed.inject(TranslateService).use('az');
    const fixture = TestBed.createComponent(PromosComponent);
    const page = fixture.componentInstance;
    fixture.detectChanges();
    http.expectOne('/api/admin/kiosks').flush([{ id: 'k1', code: 'KSK-9001', name: 'Windows', status: 'Online' }]);
    http.expectOne('/api/admin/pricing-profiles').flush([]);
    http.expectOne((r) => r.url === '/api/admin/promos').flush({ items: [], total: 0, page: 1, pageSize: 20 });
    await new Promise((resolve) => setTimeout(resolve));

    const now = new Date('2026-10-02T06:00:00Z');
    const promo = {
      id: 'p', code: 'X', kind: 'Percent' as const, value: 10, minAmount: 0, kioskIds: [] as string[], profileIds: [] as string[],
      isActive: true, usedCount: 0, confirmedCount: 0, totalDiscount: 0, createdAt: '2026-09-01T00:00:00Z',
    };
    expect(page.state(promo, now)).toBe('Active');
    expect(page.state({ ...promo, endsAt: '2026-09-30T19:59:59Z' }, now)).toBe('Expired');
    expect(page.state({ ...promo, startsAt: '2026-10-04T20:00:00Z' }, now)).toBe('Scheduled');
    expect(page.state({ ...promo, totalLimit: 2, usedCount: 2 }, now)).toBe('Exhausted');
    expect(page.state({ ...promo, isActive: false, endsAt: '2026-09-30T19:59:59Z' }, now)).toBe('Inactive');
    expect(page.restriction({ ...promo, kioskIds: ['k1'] })).toEqual(['Yalnız kiosklar: KSK-9001']);
    expect(page.restriction(promo)).toEqual([]);
  });
});
