import { HttpErrorResponse, HttpEvent, HttpHandlerFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { Observable, catchError, switchMap, throwError } from 'rxjs';

import { defaultLanguage } from '../i18n/languages';
import { AuthService } from './auth.service';

/** Endpoints that must never carry a token or trigger a refresh. */
const anonymousPaths = [
  '/api/auth/admin/login',
  '/api/auth/admin/refresh',
  '/api/auth/admin/logout',
];

/**
 * Attaches the access token and recovers from an expired one
 * (plan sections 2.8, 8A.1).
 */
export function authInterceptor(
  request: HttpRequest<unknown>,
  next: HttpHandlerFn,
): Observable<HttpEvent<unknown>> {
  const auth = inject(AuthService);

  // The server writes its messages in the language the panel is showing,
  // not the browser's: an Azerbaijani panel in an English browser still gets
  // Azerbaijani errors (docs/TASKS.md T-043).
  const language = inject(TranslateService).getCurrentLang() ?? defaultLanguage;
  request = request.clone({ setHeaders: { 'Accept-Language': language } });

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

      // One exchange for every request answered 401 at once - and for a live
      // connection that is being opened at the same moment (AuthService).
      return auth.sharedRefresh().pipe(
        switchMap(() => {
          return next(
            request.clone({
              setHeaders: { Authorization: `Bearer ${auth.token}` },
              withCredentials: true,
            }),
          );
        }),
        catchError((refreshError: unknown) => {
          // The refresh cookie is gone or refused; nothing left to recover
          // from, so the panel returns to the sign-in screen.
          auth.onSessionExpired();

          return throwError(() => refreshError);
        }),
      );
    }),
  );
}
