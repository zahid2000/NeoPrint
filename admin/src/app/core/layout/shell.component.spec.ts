import { BreakpointObserver } from '@angular/cdk/layout';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatSidenav } from '@angular/material/sidenav';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { of } from 'rxjs';
import { describe, expect, it } from 'vitest';

import { AdminLoginResponse } from '../auth/auth.models';
import { AuthService } from '../auth/auth.service';
import { AdminHubService } from '../realtime/admin-hub.service';
import { navSections } from './nav-groups';
import { ShellComponent } from './shell.component';

/** The hub, without a server: the shell opens it, the indicator reads it. */
const quietHub = {
  start: () => Promise.resolve(),
  stop: () => Promise.resolve(),
  restart: () => Promise.resolve(),
  on: () => () => undefined,
  onReconnect: () => () => undefined,
  connectionState: signal('connected'),
  lastEventAt: signal<Date | null>(null),
};

/** A screen of a given kind, as BreakpointObserver reports it. */
function screen(kind: 'full' | 'rail' | 'drawer') {
  const phone = kind === 'drawer';
  const tablet = kind !== 'full';
  return {
    isMatched: (query: string) => (query.includes('600') ? phone : tablet),
    observe: () =>
      of({
        matches: tablet,
        breakpoints: { '(max-width: 600px)': phone, '(max-width: 1024px)': tablet },
      }),
  };
}

function session(permissions: readonly string[]): AdminLoginResponse {
  return {
    accessToken: 'token',
    accessTokenExpiresAt: new Date(Date.now() + 900_000).toISOString(),
    admin: {
      id: '8f5d4f7e-0000-0000-0000-000000000001',
      email: 'admin@neoprint.test',
      fullName: 'Seeded Admin',
      roles: ['Operator'],
      permissions,
      allKiosks: true,
      kioskIds: [],
      mustChangePassword: false,
    },
  };
}

describe('ShellComponent menu (T-304)', () => {
  let fixture: ComponentFixture<ShellComponent>;
  let backend: HttpTestingController;

  async function open(kind: 'full' | 'rail' | 'drawer', permissions: readonly string[]): Promise<void> {
    TestBed.configureTestingModule({
      imports: [ShellComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: '**', children: [] }]),
        provideTranslateService({ fallbackLang: 'az', lang: 'az' }),
        { provide: AdminHubService, useValue: quietHub },
        { provide: BreakpointObserver, useValue: screen(kind) },
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    const signedIn = TestBed.inject(AuthService).signIn('admin@neoprint.test', 'password').toPromise();
    backend.expectOne('/api/auth/admin/login').flush(session(permissions));
    await signedIn;

    fixture = TestBed.createComponent(ShellComponent);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  const groups = () =>
    [...fixture.nativeElement.querySelectorAll('[data-nav-group]')].map((element: Element) =>
      element.getAttribute('data-nav-group'),
    );
  const links = () =>
    [...fixture.nativeElement.querySelectorAll('a.shell__nav-item')].map((element: Element) =>
      element.getAttribute('href'),
    );

  it('shows the groups in their order, and none this account has nothing in', async () => {
    await open('full', ['kiosks.view', 'incidents.view', 'payments.view', 'refunds.view', 'audit.view']);

    expect(groups()).toEqual(['monitoring', 'finance', 'administration']);
    // On a page without a group every group starts shut.
    expect(links()).toEqual(['/']);
  });

  it("keeps one group open at a time: the page's own, another opened by hand, or none (T-319)", async () => {
    await open('full', ['kiosks.view', 'payments.view']);
    const heading = (group: string) =>
      fixture.nativeElement.querySelector(`[data-nav-group="${group}"]`) as HTMLButtonElement;
    const click = (group: string) => {
      heading(group).click();
      fixture.detectChanges();
    };

    await TestBed.inject(Router).navigateByUrl('/payments/123');
    fixture.detectChanges();

    // The group of the page on screen is open, the others shut.
    expect(links()).toEqual(['/', '/payments']);
    expect(heading('finance').getAttribute('aria-expanded')).toBe('true');
    expect(heading('monitoring').getAttribute('aria-expanded')).toBe('false');

    // It shuts like any other.
    click('finance');
    expect(links()).toEqual(['/']);
    expect(heading('finance').getAttribute('aria-expanded')).toBe('false');

    // Opening a group shuts the one open before.
    click('finance');
    click('monitoring');
    expect(links()).toEqual(['/', '/kiosks']);
    expect(heading('finance').getAttribute('aria-expanded')).toBe('false');

    // Another page opens its own group and nothing else.
    await TestBed.inject(Router).navigateByUrl('/payments');
    fixture.detectChanges();
    expect(links()).toEqual(['/', '/payments']);
  });

  it('on a tablet shows the icons only, with the names in tooltips and no group headings', async () => {
    await open('rail', ['kiosks.view', 'payments.view']);

    expect(groups()).toEqual([]);
    expect(fixture.nativeElement.querySelector('.shell__nav-label')).toBeNull();
    expect(links()).toEqual(['/', '/kiosks', '/payments']);
    expect(fixture.nativeElement.querySelector('a[href="/kiosks"]').getAttribute('aria-label')).toBe('nav.kiosks');
  });

  it('on a tablet folds the full menu back into the icons when it shuts itself, never into nothing (T-319)', async () => {
    await open('rail', ['kiosks.view', 'payments.view']);
    const shell = fixture.componentInstance;
    const sidenav = fixture.debugElement.query(By.directive(MatSidenav)).injector.get(MatSidenav);

    shell.toggleSidenav();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(shell.iconsOnly()).toBe(false);

    // The backdrop or Escape: the sidenav shuts itself.
    await sidenav.close();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(shell.iconsOnly()).toBe(true);
    expect(sidenav.opened).toBe(true);
    expect(links()).toEqual(['/', '/kiosks', '/payments']);

    // And ☰ opens it in full again.
    shell.toggleSidenav();
    fixture.detectChanges();
    expect(shell.iconsOnly()).toBe(false);
  });

  it('on a phone keeps the menu shut until it is asked for', async () => {
    await open('drawer', ['kiosks.view']);

    expect(fixture.componentInstance.sidenavOpen()).toBe(false);
    fixture.componentInstance.toggleSidenav();
    expect(fixture.componentInstance.sidenavOpen()).toBe(true);
  });

  it('keeps the two pages without a group at the top', () => {
    const sections = navSections([
      { path: '/', labelKey: 'nav.dashboard', icon: 'dashboard', permissions: [] },
      { path: '/kiosks', labelKey: 'nav.kiosks', icon: 'storefront', permissions: [], group: 'monitoring' },
    ]);
    expect(sections.map((section) => section.group)).toEqual([null, 'monitoring']);
  });
});
