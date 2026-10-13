import { DestroyRef, inject } from '@angular/core';

import { AuthService } from '../auth/auth.service';
import { AdminHubService } from './admin-hub.service';
import { HubEvents } from './hub-events';

/**
 * Opens the panel's live connection and keeps the session in step with it,
 * for as long as the shell that called it is on screen. Both shells - the
 * admin panel's and the operator panel's - call it once from their
 * constructor; a shell is the first point at which there is a session for the
 * hub to authorise.
 */
export function panelConnection(): void {
  const auth = inject(AuthService);
  const hub = inject(AdminHubService);

  void hub.start();

  const off = [
    // A role change used to reach the panel only on the next token exchange,
    // up to fifteen minutes later; a permission taken away was immediate but
    // one granted was not (docs/DECISIONS.md). The server announces it, so
    // the menu now follows within a second.
    hub.on(HubEvents.permissionsChanged, () => void auth.reloadPermissions()),

    // This account itself was changed - its kiosks, its roles, or what one of
    // its roles permits. The server closes the connection; a panel on another
    // instance of the API hears this instead and opens a new one, which is
    // when what it may hear is decided again (T-249, T-261).
    hub.on(HubEvents.accountChanged, () => {
      void auth.reloadPermissions().then(() => hub.restart());
    }),

    // Whatever ended the connection may have been about this account.
    hub.onReconnect(() => void auth.reloadProfile()),
  ];

  inject(DestroyRef).onDestroy(() => {
    off.forEach((unsubscribe) => unsubscribe());
    void hub.stop();
  });
}
