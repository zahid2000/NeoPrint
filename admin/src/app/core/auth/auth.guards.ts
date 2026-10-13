import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthService } from './auth.service';

/**
 * Requires a signed-in administrator.
 *
 * Also holds the mandatory password change: while one is outstanding the panel
 * goes nowhere but that screen (plan section 2.8).
 */
export const authGuard: CanActivateFn = (route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isSignedIn()) {
    return router.createUrlTree(['/login'], {
      queryParams: { returnUrl: state.url },
    });
  }

  if (auth.mustChangePassword() && !state.url.startsWith('/change-password')) {
    return router.createUrlTree(['/change-password']);
  }

  return true;
};

/**
 * Requires a permission from the catalogue (plan section 8A.2).
 *
 * The presentation half of the check. The endpoint behind the page is guarded
 * on the server; this only keeps someone from reaching a page whose every
 * request would be refused.
 */
export function permissionGuard(...permissions: readonly string[]): CanActivateFn {
  return () => {
    const auth = inject(AuthService);
    const router = inject(Router);

    if (auth.canAny(permissions)) {
      return true;
    }

    // Sent to the dashboard rather than shown a bare error: an operator whose
    // scope does not include a page has somewhere useful to land.
    return router.createUrlTree(['/']);
  };
}

/** Keeps a signed-in administrator off the sign-in screen. */
export const anonymousGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.isSignedIn() ? router.createUrlTree([auth.homePath()]) : true;
};
