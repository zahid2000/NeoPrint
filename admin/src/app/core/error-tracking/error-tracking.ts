import { ErrorHandler, Provider } from '@angular/core';
import * as Sentry from '@sentry/angular';

/** What the API answers at /api/error-tracking/admin (T-478). */
interface ErrorTrackingConfig {
  readonly dsn?: string | null;
  readonly environment?: string;
}

/**
 * Error tracking (plan step 8, T-478; docs/MONITORING.md): the panel's
 * unhandled errors go to the self-hosted GlitchTip through the Sentry SDK.
 *
 * Started before the app, from the DSN the API hands out; with none - the
 * API has error tracking off, or cannot be reached - nothing is sent and
 * nothing else changes. What is sent is the error and its stack: no user, no
 * IP, no cookies, no query strings, no breadcrumbs of what was typed or
 * fetched.
 */
export async function startErrorTracking(
  fetchConfig: () => Promise<Response> = defaultFetch,
  init: (options: Sentry.BrowserOptions) => unknown = Sentry.init,
): Promise<boolean> {
  let config: ErrorTrackingConfig;
  try {
    const response = await fetchConfig();
    if (!response.ok) {
      return false;
    }
    config = (await response.json()) as ErrorTrackingConfig;
  } catch {
    return false;
  }

  if (!config.dsn) {
    return false;
  }

  init({
    dsn: config.dsn,
    environment: config.environment,
    sendDefaultPii: false,
    tracesSampleRate: 0,
    // The default integrations, without the ones that record what the
    // operator does (clicks, fetches, console) before an error.
    integrations: (defaults) => defaults.filter((integration) => integration.name !== 'Breadcrumbs'),
    beforeSend: scrub,
  });
  return true;
}

/** Drops anything that could name a person or carry a credential. */
export function scrub<T extends Sentry.ErrorEvent>(event: T): T {
  delete event.user;
  if (event.request) {
    delete event.request.cookies;
    delete event.request.headers;
    delete event.request.query_string;
    delete event.request.data;
    if (event.request.url) {
      event.request.url = event.request.url.split(/[?#]/)[0];
    }
  }
  return event;
}

/** Angular's error handler, reporting to GlitchTip once it is started. */
export function provideErrorTracking(): Provider {
  return { provide: ErrorHandler, useValue: Sentry.createErrorHandler({ logErrors: true }) };
}

function defaultFetch(): Promise<Response> {
  return fetch('/api/error-tracking/admin', { credentials: 'omit' });
}
