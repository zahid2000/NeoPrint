import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';

import { OperatorNotifyService } from './operator-notify.service';

interface Shown {
  readonly via: 'worker' | 'page';
  readonly title: string;
  readonly options: NotificationOptions;
}

/**
 * A window as a phone's browser has it: notifications allowed, and either a
 * service worker or none; `new Notification` fails as Chrome on Android
 * makes it fail when `pageNotifications` is false.
 */
function phone(worker: boolean, pageNotifications: boolean): { view: object; shown: Shown[] } {
  const shown: Shown[] = [];

  const Notification = function (this: unknown, title: string, options: NotificationOptions) {
    if (!pageNotifications) {
      throw new TypeError("Failed to construct 'Notification': Illegal constructor.");
    }

    shown.push({ via: 'page', title, options });
  } as unknown as typeof globalThis.Notification;
  Object.assign(Notification, { permission: 'granted', requestPermission: () => Promise.resolve('granted') });

  const registration = {
    showNotification: (title: string, options: NotificationOptions) => {
      shown.push({ via: 'worker', title, options });
      return Promise.resolve();
    },
  };

  const view = {
    Notification,
    navigator: { serviceWorker: worker ? { getRegistration: () => Promise.resolve(registration) } : undefined },
  };

  return { view, shown };
}

function service(view: object): OperatorNotifyService {
  TestBed.configureTestingModule({
    providers: [{ provide: DOCUMENT, useValue: { defaultView: view } }],
  });

  return TestBed.inject(OperatorNotifyService);
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

describe('OperatorNotifyService (T-322)', () => {
  it('shows a notification through the service worker, where a phone has no other way', async () => {
    const { view, shown } = phone(true, false);

    service(view).announce('Yeni tapşırıq: Təmizlik', 'KSK-9002', 'task-1', '/operator/tasks');
    await settle();

    expect(shown).toHaveLength(1);
    expect(shown[0].via).toBe('worker');
    expect(shown[0].title).toBe('Yeni tapşırıq: Təmizlik');
    expect(shown[0].options.tag).toBe('task-1');
    // A click opens the panel at the page it is about.
    expect(shown[0].options.data).toEqual({
      onActionClick: { default: { operation: 'focusLastFocusedOrOpen', url: '/operator/tasks' } },
    });
  });

  it('falls back to the page itself without a service worker', async () => {
    const { view, shown } = phone(false, true);

    service(view).announce('Hadisə', 'Yüksək', 'incident-1');
    await settle();

    expect(shown.map((item) => item.via)).toEqual(['page']);
  });

  it('says nothing until the operator has allowed it', async () => {
    const { view, shown } = phone(true, true);
    Object.assign((view as { Notification: object }).Notification, { permission: 'default' });

    service(view).announce('Hadisə', 'Yüksək', 'incident-1');
    await settle();

    expect(shown).toEqual([]);
  });
});
