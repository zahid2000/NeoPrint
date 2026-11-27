import * as Sentry from '@sentry/angular';
import { describe, expect, it, vi } from 'vitest';

import { scrub, startErrorTracking } from './error-tracking';


const answer = (body: unknown, ok = true) => () => Promise.resolve({ ok, json: () => Promise.resolve(body) } as Response);

describe('error tracking (T-478)', () => {

  it('starts only with a DSN from the API', async () => {
    const init = vi.fn();

    expect(await startErrorTracking(answer({ environment: 'Development' }), init)).toBe(false);
    expect(await startErrorTracking(answer({}, false), init)).toBe(false);
    expect(await startErrorTracking(() => Promise.reject(new Error('offline')), init)).toBe(false);
    expect(init).not.toHaveBeenCalled();

    expect(await startErrorTracking(answer({ dsn: 'https://key@errors.example.test/4', environment: 'Development' }), init)).toBe(true);
    expect(init).toHaveBeenCalledWith(expect.objectContaining({ dsn: 'https://key@errors.example.test/4', sendDefaultPii: false, tracesSampleRate: 0 }));
  });

  it('sends the error, not who caused it or what they sent', () => {
    const event = scrub({
      type: undefined,
      user: { email: 'admin@example.test', ip_address: '203.0.113.7' },
      request: {
        url: 'https://panel.example.test/users?search=someone#top',
        cookies: { refresh: 'secret' },
        headers: { Authorization: 'Bearer secret' },
        query_string: 'search=someone',
        data: '{"password":"secret"}',
      },
      exception: { values: [{ type: 'Error', value: 'boom' }] },
    } as Sentry.ErrorEvent);

    expect(event.user).toBeUndefined();
    expect(event.request).toEqual({ url: 'https://panel.example.test/users' });
    expect(event.exception?.values?.[0].value).toBe('boom');
  });
});
