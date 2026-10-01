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
import { PaymentDetailComponent } from './payment-detail.component';
import { PaymentsComponent } from './payments.component';
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
});
