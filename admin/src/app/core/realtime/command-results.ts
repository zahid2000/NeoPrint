import { DestroyRef, inject } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';

import { NotifyService } from '../../shared/notify.service';
import { KioskCommand } from '../api/step3.models';
import { AdminHubService } from './admin-hub.service';
import { HubEvents } from './hub-events';

/** A command the kiosk has finished with, one way or the other. */
const over = new Set(['Succeeded', 'Failed', 'Expired']);

/** How long an answer that came before its own request returned is kept for it. */
const earlyForMs = 30_000;

/**
 * Says how a command this page sent ended (T-271): a kiosk answers over the
 * hub some moments after the request returned, and until now the answer only
 * changed a row in a table further down the page - or, in the operator panel,
 * nothing at all. Whoever pressed "Test çap" is told whether the page printed.
 *
 * Only the commands this page sent are spoken of: what a colleague sends to
 * the same kiosk is theirs to hear about.
 *
 * Call it where `inject` works; hand it every command the page sends.
 */
export function commandResults(): { sent(command: KioskCommand): void } {
  const hub = inject(AdminHubService);
  const notify = inject(NotifyService);
  const translate = inject(TranslateService);

  /** Sent from here, not answered yet. */
  const waiting = new Set<string>();

  /** Answered before the request that sent them returned: a kiosk can be that quick. */
  const early = new Map<string, KioskCommand>();

  function say(command: KioskCommand): void {
    const name = translate.instant(`printers.command.${command.type}`) as string;
    const result = command.result?.trim();

    if (command.status === 'Succeeded') {
      notify.success(result ? 'printers.resultSucceeded' : 'printers.resultSucceededPlain', { command: name, result });
    } else if (command.status === 'Expired') {
      notify.error('printers.resultExpired', { command: name });
    } else {
      notify.error(result ? 'printers.resultFailed' : 'printers.resultFailedPlain', { command: name, result });
    }
  }

  const off = hub.on<KioskCommand>(HubEvents.commandResult, (command) => {
    if (!over.has(command.status)) {
      return;
    }

    if (waiting.delete(command.id)) {
      say(command);

      return;
    }

    early.set(command.id, command);
    setTimeout(() => early.delete(command.id), earlyForMs);
  });

  inject(DestroyRef).onDestroy(off);

  return {
    sent(command: KioskCommand): void {
      const answered = over.has(command.status) ? command : early.get(command.id);

      if (answered) {
        early.delete(command.id);
        say(answered);
      } else {
        waiting.add(command.id);
      }
    },
  };
}
