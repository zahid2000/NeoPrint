import { HttpErrorResponse, HttpEvent, HttpHandlerFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Observable, catchError, shareReplay, switchMap, throwError } from 'rxjs';

import { AuthService } from './auth.service';

/** Endpoints that must never carry a token or trigger a refresh. */
const anonymousPaths = [
  '/api/auth/admin/login',
  '/api/auth/admin/refresh',
  '/api/auth/admin/logout',
];

/**
 * A single refresh shared by every request that hits a 401 at once.
 *
 * The refresh token rotates on use (plan section 2.8), so five parallel
 * refreshes would have four rejected as replays - and a replay makes the
 * server revoke the whole chain, signing the administrator out.
 */
let refreshInFlight: Observable<unknown> | null = null;

/**
 * Attaches the access token and recovers from an expired one
 * (plan sections 2.8, 8A.1).
 */
export function authInterceptor(
  request: HttpRequest<unknown>,
  next: HttpHandlerFn,
): Observable<HttpEvent<unknown>> {
  const auth = inject(AuthService);

  const isAnonymous = anonymousPaths.some((path) => request.url.includes(path));

  const authorized =
    !isAnonymous && auth.token
      ? request.clone({
          setHeaders: { Authorization: `Bearer ${auth.token}` },
          // The refresh cookie has to travel on every admin call.
          withCredentials: true,
        })
      : request.clone({ withCredentials: true });

  return next(authorized).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || isAnonymous) {
        return throwError(() => error);
      }

      // shareReplay, because the refresh observable is cold: subscribing to it
      // twice would send two requests and defeat the whole point of sharing.
      refreshInFlight ??= auth.refresh().pipe(shareReplay({ bufferSize: 1, refCount: false }));

      const shared = refreshInFlight;

      return shared.pipe(
        switchMap(() => {
          release(shared);

          return next(
            request.clone({
              setHeaders: { Authorization: `Bearer ${auth.token}` },
              withCredentials: true,
            }),
          );
        }),
        catchError((refreshError: unknown) => {
          release(shared);

          // The refresh cookie is gone or refused; nothing left to recover
          // from, so the panel returns to the sign-in screen.
          auth.onSessionExpired();

          return throwError(() => refreshError);
        }),
      );
    }),
  );
}

/**
 * Clears the shared refresh, but only if it is still the current one.
 *
 * A later 401 may already have started a new refresh; clearing that one would
 * let the next request start a second, which rotation would reject.
 */
function release(shared: Observable<unknown>): void {
  if (refreshInFlight === shared) {
    refreshInFlight = null;
  }
}
