import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { afterEach, describe, expect, it } from 'vitest';

import { ReferralPage, ReferralRow, referralOutcome } from '../../core/api/step6.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { moneyTranslations } from '../../core/i18n/money.i18n';
import { referralsTranslations } from '../../core/i18n/referrals.i18n';
import { CustomerDetailComponent } from '../money/customer-detail.component';
import { ReferralsComponent } from './referrals.component';

function setUp(permissions: string[]): HttpTestingController {
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
    ],
  });

  const translate = TestBed.inject(TranslateService);
  translate.setTranslation('az', { ...moneyTranslations.az, ...referralsTranslations.az });
  translate.use('az');

  return TestBed.inject(HttpTestingController);
}

const row = (overrides: Partial<ReferralRow>): ReferralRow => ({
  id: 'r1',
  inviter: { id: 'u1', publicId: 'NP-INVITE' },
  invitee: { id: 'u2', publicId: 'NP-FRIEND' },
  status: 'Registered',
  hold: 'None',
  createdAt: '2026-10-05T06:00:00Z',
  inviterBonusPages: 0,
  inviteeBonusPages: 0,
  ...overrides,
});

const page = (items: readonly ReferralRow[]): ReferralPage => ({
  totals: { registered: 3, rewarded: 1, waiting: 1, held: 1, bonusPagesGiven: 12 },
  top: [{ inviter: { id: 'u1', publicId: 'NP-INVITE' }, invited: 3, rewarded: 1, bonusPages: 10 }],
  items,
  total: items.length,
  page: 1,
  pageSize: 20,
});

const settle = (): Promise<void> => new Promise((resolve) => setTimeout(resolve));

describe('referrals', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('a row is one of three things to the reader, whatever its two fields say', () => {
    setUp([]);
    expect(referralOutcome({ status: 'Rewarded', hold: 'None' })).toBe('Rewarded');
    expect(referralOutcome({ status: 'Registered', hold: 'None' })).toBe('Waiting');
    expect(referralOutcome({ status: 'Registered', hold: 'SameDevice' })).toBe('Held');
  });

  it('the page shows the numbers, the top inviters and the history with why a bonus was not written', async () => {
    const http = setUp([Permissions.referralsView]);
    const fixture = TestBed.createComponent(ReferralsComponent);
    fixture.detectChanges();

    http
      .expectOne((r) => r.url === '/api/admin/referrals' && !r.params.has('status'))
      .flush(
        page([
          row({
            id: 'a',
            status: 'Rewarded',
            inviterBonusPages: 10,
            inviteeBonusPages: 2,
            qualifiedAt: '2026-10-05T07:00:00Z',
          }),
          row({ id: 'b', hold: 'MonthlyLimit', qualifiedAt: '2026-10-05T07:00:00Z' }),
          row({ id: 'c', invitee: { id: 'u9', publicId: '' } }),
        ]),
      );
    await settle();
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Verilən bonus: 12 səhifə');
    expect(text).toContain('Ən çox dəvət edənlər');
    expect(text).toContain('Dəvət edənə 10, dəvət olunana 2 səhifə');
    expect(text).toContain('Aylıq limit');
    expect(text).toContain('İlk çapı gözləyir');
    expect(text).toContain('Silinmiş hesab');

    // Without users.view a public id is text, not a link to the customer.
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('a[href*="/customers/"]'),
    ).toBeNull();
  });

  it('a tab asks for its own slice from the first page', async () => {
    const http = setUp([Permissions.referralsView, Permissions.usersView]);
    const fixture = TestBed.createComponent(ReferralsComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();
    http.expectOne((r) => r.url === '/api/admin/referrals').flush(page([row({})]));
    await settle();
    fixture.detectChanges();

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('a[href="/customers/u1"]'),
    ).not.toBeNull();

    component.page.set(3);
    component.tab(3);
    const request = http.expectOne((r) => r.url === '/api/admin/referrals');
    expect(request.request.params.get('status')).toBe('Held');
    expect(request.request.params.get('page')).toBe('1');
    request.flush(page([]));
    await settle();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      'Bu vəziyyətdə dəvət yoxdur.',
    );
  });

  it('a customer page lists files as masked metadata and the referrals both ways', async () => {
    const http = setUp([Permissions.usersView]);
    const fixture = TestBed.createComponent(CustomerDetailComponent);
    fixture.componentRef.setInput('id', 'u1');
    fixture.detectChanges();

    http.expectOne('/api/admin/users/u1').flush({
      profile: {
        id: 'u1',
        publicId: 'NP-INVITE',
        email: 'a@neoprint.test',
        status: 'Active',
        emailConfirmed: true,
        createdAt: '2026-10-01T06:00:00Z',
      },
      language: 'az',
      wallet: { balance: 0, bonusPages: 10, currency: 'AZN' },
      recentJobs: [],
      fileCount: 60,
      fileBytes: 1000,
      referred: 1,
      consents: [{ docType: 'ScanNotice', version: '0.1-draft' }],
      files: [
        {
          id: 'f1',
          maskedName: 'He•••.pdf',
          extension: 'pdf',
          sizeBytes: 1000,
          source: 'Uploaded',
          isSensitive: false,
          status: 'Ready',
          pageCount: 3,
          createdAt: '2026-10-02T06:00:00Z',
        },
        {
          id: 'f2',
          maskedName: '•••.pdf',
          extension: 'pdf',
          sizeBytes: 2000,
          source: 'Scan',
          isSensitive: true,
          status: 'Ready',
          pageCount: 1,
          createdAt: '2026-10-03T06:00:00Z',
        },
      ],
      referrals: {
        invitedBy: { id: 'u0', publicId: 'NP-PARENT' },
        totals: { registered: 1, rewarded: 0, waiting: 0, bonusPagesEarned: 0 },
        items: [row({ hold: 'SameDevice' })],
      },
    });
    http
      .expectOne((r) => r.url.includes('/wallet'))
      .flush({
        items: [
          {
            id: 'w1',
            type: 'Bonus',
            amount: 0,
            bonusPages: 10,
            balanceAfter: 0,
            bonusPagesAfter: 10,
            createdAt: '2026-10-05T06:43:00Z',
            note: 'Dəvət bonusu: dəvət etdiyiniz istifadəçi ilk çapını etdi',
          },
        ],
        total: 1,
        page: 1,
        pageSize: 20,
      });
    await settle();
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    const text = element.textContent ?? '';
    expect(text).toContain('He•••.pdf');
    expect(text).toContain('•••.pdf');
    expect(text).toContain('Həssas');
    expect(text).toContain('Skan');
    expect(text).toContain('Adlar qismən gizlədilir');
    expect(text).toContain('Son 2 fayl göstərilir (cəmi 60).');
    expect(text).toContain('NP-PARENT');
    expect(text).toContain('NP-FRIEND');
    expect(text).toContain('Eyni cihaz');
    expect(element.querySelector('a[href*="/files/"]')).toBeNull();

    // A bonus line says its pages, not "0,00 ₼"; every consent has its name.
    const bonusLine = [...element.querySelectorAll('.money__item-head strong')].find((e) =>
      (e.textContent ?? '').includes('bonus səhifə'),
    );
    expect(bonusLine?.textContent?.trim()).toBe('+10 bonus səhifə');
    expect(text).toContain('Sənəd skanı barədə məlumat');
    expect(text).not.toContain('customerDetail.');
  });
});
