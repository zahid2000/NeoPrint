import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { of } from 'rxjs';
import { afterEach, describe, expect, it } from 'vitest';

import { KioskLog, KioskUpdateDetail } from '../../core/api/releases-api.service';
import { KioskCommand } from '../../core/api/step3.models';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { HubEvents } from '../../core/realtime/hub-events';
import { liveDebounceMs } from '../../core/realtime/live-refresh';
import { KioskUpdatesComponent } from './kiosk-updates.component';

/** Stands in for the SignalR connection: the test fires the events itself. */
class FakeHub {
  readonly handlers = new Map<string, ((payload: unknown) => void)[]>();

  connectionState = (): 'connected' => 'connected';
  onState = (): (() => void) => () => undefined;
  onReconnect = (): (() => void) => () => undefined;

  on<T>(event: string, handler: (payload: T) => void): () => void {
    this.handlers.set(event, [
      ...(this.handlers.get(event) ?? []),
      handler as (payload: unknown) => void,
    ]);
    return () => undefined;
  }

  fire(event: string, payload: unknown): void {
    this.handlers.get(event)?.forEach((handler) => handler(payload));
  }
}

const kioskId = '11111111-1111-1111-1111-111111111111';

function detail(overrides: Partial<KioskUpdateDetail> = {}): KioskUpdateDetail {
  return {
    kioskId,
    platform: 'linux-x64',
    channel: 'Pilot',
    hasDevice: true,
    lastHeartbeatAt: '2026-10-25T10:00:00Z',
    maxIdleWaitMinutes: 30,
    components: [
      {
        component: 'App',
        running: '0.8.0',
        target: '0.8.1',
        state: {
          component: 'App',
          version: '0.8.1',
          status: 'WaitingForIdle',
          message: null,
          reportedAt: '2026-10-25T10:00:00Z',
        },
      },
      { component: 'Agent', running: '0.8.0', target: '0.8.0', state: null },
      { component: 'Provisioning', running: '0.4.1', target: null, state: null },
    ],
    history: [
      {
        component: 'App',
        version: '0.8.1',
        status: 'WaitingForIdle',
        message: null,
        reportedAt: '2026-10-25T10:00:00Z',
      },
      {
        component: 'App',
        version: '0.8.1',
        status: 'Ready',
        message: null,
        reportedAt: '2026-10-25T09:59:00Z',
      },
    ],
    skipped: [],
    ...overrides,
  };
}

const log: KioskLog = {
  id: 'log-1',
  commandId: 'c-1',
  size: 2048,
  lines: 120,
  redacted: 2,
  createdAt: '2026-10-25T10:01:00Z',
  expiresAt: '2026-11-24T10:01:00Z',
};

function command(overrides: Partial<KioskCommand>): KioskCommand {
  return {
    id: 'c-1',
    kioskId,
    type: 'UploadLogs',
    status: 'Sent',
    result: null,
    createdAt: '2026-10-25T10:00:00Z',
    completedAt: null,
    ...overrides,
  };
}

let backend: HttpTestingController;
let hub: FakeHub;
let dialogAnswer: { reason: string } | undefined;

function setUp(permissions: string[]): void {
  hub = new FakeHub();
  dialogAnswer = { reason: 'Pilot kioskunda yoxlama' };

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
      { provide: MatDialog, useValue: { open: () => ({ afterClosed: () => of(dialogAnswer) }) } },
    ],
  });

  backend = TestBed.inject(HttpTestingController);
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  await fixture.whenStable();
  fixture.detectChanges();
}

async function open(
  permissions: string[],
  shown: KioskUpdateDetail = detail(),
  commands: KioskCommand[] = [],
  logs: KioskLog[] = [log],
): Promise<ComponentFixture<KioskUpdatesComponent>> {
  setUp(permissions);
  const fixture = TestBed.createComponent(KioskUpdatesComponent);
  fixture.componentRef.setInput('kioskId', kioskId);
  fixture.componentRef.setInput('kioskCode', 'KSK-9007');
  fixture.detectChanges();

  await answer(fixture, shown, commands, logs, permissions.includes(Permissions.kioskLogsDownload));
  return fixture;
}

async function answer(
  fixture: ComponentFixture<unknown>,
  shown: KioskUpdateDetail,
  commands: KioskCommand[],
  logs: KioskLog[],
  withLogs: boolean,
): Promise<void> {
  backend.expectOne(`/api/admin/kiosks/${kioskId}/update`).flush(shown);
  await settle(fixture);
  backend.expectOne(`/api/admin/kiosks/${kioskId}/commands`).flush(commands);
  await settle(fixture);
  if (withLogs) {
    backend.expectOne(`/api/admin/kiosks/${kioskId}/logs`).flush(logs);
  }

  await settle(fixture);
}

function element(fixture: ComponentFixture<unknown>): HTMLElement {
  return fixture.nativeElement as HTMLElement;
}

const everything = [
  Permissions.kiosksView,
  Permissions.kioskCommandsUpdate,
  Permissions.kioskCommandsRestart,
  Permissions.kioskLogsDownload,
];

describe('KioskUpdatesComponent', () => {
  afterEach(() => backend.verify());

  it('shows the running and target versions, the update under way and the history', async () => {
    const fixture = await open(everything);
    const page = element(fixture);

    const rows = page.querySelectorAll('[data-table="versions"] tr.mat-mdc-row');
    expect(rows.length).toBe(3);
    expect(rows[0].textContent).toContain('0.8.0');
    expect(rows[0].textContent).toContain('0.8.1');
    // The agent is offered what it runs: no target is shown.
    expect(rows[1].querySelectorAll('td')[2].textContent?.trim()).toBe('');

    expect(page.querySelector('[data-notice="waiting"]')).not.toBeNull();
    expect(page.querySelectorAll('[data-table="history"] tr.mat-mdc-row').length).toBe(2);
  });

  it('says not taking new customers ahead of waiting, and a rolled-back version when nothing is under way', async () => {
    const draining = detail();
    const states = draining.components.map((component, index) =>
      index === 1
        ? {
            ...component,
            state: {
              component: 'Agent' as const,
              version: '0.8.1',
              status: 'Draining' as const,
              message: null,
              reportedAt: '',
            },
          }
        : component,
    );
    let fixture = await open(everything, { ...draining, components: states });
    expect(element(fixture).querySelector('[data-notice="draining"]')).not.toBeNull();
    backend.verify();
    TestBed.resetTestingModule();

    const back = detail({
      components: [
        {
          component: 'App',
          running: '0.8.0',
          target: null,
          state: {
            component: 'App',
            version: '0.8.1',
            status: 'RolledBack',
            message: 'Sağlamlıq yoxlamasından keçmədi.',
            reportedAt: '',
          },
        },
      ],
      skipped: [{ component: 'App', version: '0.8.1', at: '' }],
    });
    fixture = await open(everything, back);
    expect(element(fixture).querySelector('[data-notice="rolledBack"]')).not.toBeNull();
    expect(element(fixture).querySelector('[data-notice="skipped"]')).not.toBeNull();
  });

  it('sends a command only after it is confirmed with a reason, and the reason goes with it', async () => {
    const fixture = await open(everything);

    (element(fixture).querySelector('[data-command="update-now"]') as HTMLButtonElement).click();
    await settle(fixture);

    const sent = backend.expectOne(`/api/admin/kiosks/${kioskId}/update-now`);
    expect(sent.request.method).toBe('POST');
    expect(sent.request.body).toEqual({ reason: 'Pilot kioskunda yoxlama' });
    sent.flush(command({ id: 'c-2', type: 'UpdateNow', reason: 'Pilot kioskunda yoxlama' }));
    await settle(fixture);
  });

  it('sends nothing when the dialog is cancelled', async () => {
    const fixture = await open(everything);
    dialogAnswer = undefined;

    (
      element(fixture).querySelector('[data-command="rollback-version"]') as HTMLButtonElement
    ).click();
    await settle(fixture);

    backend.expectNone(`/api/admin/kiosks/${kioskId}/rollback-version`);
  });

  it('shows only the commands the account may send, and none for a kiosk without a device', async () => {
    let fixture = await open([Permissions.kiosksView]);
    expect(element(fixture).querySelectorAll('[data-command]').length).toBe(0);
    expect(element(fixture).querySelector('[data-table="logs"]')).toBeNull();
    backend.verify();
    TestBed.resetTestingModule();

    fixture = await open([Permissions.kiosksView, Permissions.kioskCommandsRestart]);
    expect(
      [...element(fixture).querySelectorAll('[data-command]')].map((button) =>
        button.getAttribute('data-command'),
      ),
    ).toEqual(['restart-agent']);
    backend.verify();
    TestBed.resetTestingModule();

    fixture = await open(everything, detail({ hasDevice: false }));
    expect(element(fixture).querySelectorAll('[data-command]').length).toBe(0);
    expect(element(fixture).querySelector('[data-no-device]')).not.toBeNull();
  });

  it('lists the logs with a download, and says while an upload is on its way', async () => {
    const fixture = await open(everything, detail(), [command({ status: 'Acknowledged' })]);
    const page = element(fixture);

    expect(page.querySelectorAll('[data-table="logs"] tr.mat-mdc-row').length).toBe(1);
    expect(page.querySelector('[data-download="log-1"]')).not.toBeNull();
    expect(page.querySelector('[data-log-upload] app-status-chip')).not.toBeNull();
  });

  it('reads everything again when this kiosk says its update changed - and not for another kiosk', async () => {
    const fixture = await open(everything);

    hub.fire(HubEvents.kioskUpdateChanged, { kioskId: 'another' });
    await new Promise((resolve) => setTimeout(resolve, liveDebounceMs + 50));
    backend.expectNone(`/api/admin/kiosks/${kioskId}/update`);

    hub.fire(HubEvents.kioskUpdateChanged, { kioskId });
    await new Promise((resolve) => setTimeout(resolve, liveDebounceMs + 50));
    const moved = detail({
      components: [
        {
          component: 'App',
          running: '0.8.1',
          target: '0.8.1',
          state: {
            component: 'App',
            version: '0.8.1',
            status: 'Succeeded',
            message: null,
            reportedAt: '',
          },
        },
      ],
    });
    await answer(fixture, moved, [], [log], true);

    expect(element(fixture).querySelector('[data-notice]')).toBeNull();
    expect(element(fixture).querySelector('[data-table="versions"]')?.textContent).toContain(
      '0.8.1',
    );
  });
});
