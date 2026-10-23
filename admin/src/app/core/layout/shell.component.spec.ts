import { BreakpointObserver } from '@angular/cdk/layout';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { AdminLoginResponse } from '../auth/auth.models';
import { AuthService } from '../auth/auth.service';
import { AdminHubService } from '../realtime/admin-hub.service';
import { collapsedGroupsKey, navSections, readCollapsed } from './nav-groups';
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

  beforeEach(() => localStorage.removeItem(collapsedGroupsKey));
  afterEach(() => localStorage.removeItem(collapsedGroupsKey));

  it('shows the groups in their order, and none this account has nothing in', async () => {
    await open('full', ['kiosks.view', 'incidents.view', 'payments.view', 'refunds.view', 'audit.view']);

    expect(groups()).toEqual(['monitoring', 'finance', 'administration']);
    expect(links()).toEqual(['/', '/kiosks', '/incidents', '/payments', '/refunds', '/audit']);
  });

  it('shuts a group, remembers it in this browser, and keeps the group of the page on screen open', async () => {
    await open('full', ['kiosks.view', 'payments.view']);

    (fixture.nativeElement.querySelector('[data-nav-group="finance"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(links()).not.toContain('/payments');
    expect(readCollapsed(localStorage).has('finance')).toBe(true);
    expect(fixture.nativeElement.querySelector('[data-nav-group="finance"]').getAttribute('aria-expanded')).toBe('false');

    // On a finance page its group is open, shut or not.
    await TestBed.inject(Router).navigateByUrl('/payments/123');
    fixture.detectChanges();

    expect(links()).toContain('/payments');
  });

  it('on a tablet shows the icons only, with the names in tooltips and no group headings', async () => {
    await open('rail', ['kiosks.view', 'payments.view']);

    expect(groups()).toEqual([]);
    expect(fixture.nativeElement.querySelector('.shell__nav-label')).toBeNull();
    expect(links()).toEqual(['/', '/kiosks', '/payments']);
    expect(fixture.nativeElement.querySelector('a[href="/kiosks"]').getAttribute('aria-label')).toBe('nav.kiosks');
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
