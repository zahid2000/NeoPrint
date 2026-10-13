import { DestroyRef, Injectable, computed, inject } from '@angular/core';
import { map } from 'rxjs';

import { Page } from '../../core/api/step4.models';
import { AuthService } from '../../core/auth/auth.service';
import { HubEvents } from '../../core/realtime/hub-events';
import { OperatorApiService } from './operator-api.service';
import { ServiceTask, openTaskStatuses } from './operator.models';
import { operatorScreen } from './operator-screen';

const pageItems = map((page: Page<ServiceTask>) => page.items);

/**
 * The tasks still to be done in the operator's kiosks - once for the panel:
 * the "Tapşırıqlar" page lists them and the navigation counts them, and both
 * read this (the same rule as the kiosk statuses, T-254).
 *
 * Follows `ServiceTaskAssigned` and `ServiceTaskUpdated`; what it shows is
 * kept on the phone for a visit without a connection.
 */
@Injectable({ providedIn: 'root' })
export class OperatorTasksStore {
  private readonly api = inject(OperatorApiService);
  private readonly auth = inject(AuthService);

  private watchers = 0;

  private readonly screen = operatorScreen<readonly ServiceTask[]>({
    key: () => (this.watchers > 0 ? 'tasks' : null),
    fetch: () => this.api.tasks(openTaskStatuses).pipe(pageItems),
    events: [HubEvents.serviceTaskAssigned, HubEvents.serviceTaskUpdated],
  });

  readonly at = this.screen.at;
  readonly loading = this.screen.loading;
  readonly offline = this.screen.offline;
  readonly problem = this.screen.problem;
  readonly canAct = this.screen.canAct;

  /** The caller's own: given to them, or being done by them. The most urgent first, as the server sends them. */
  readonly mine = computed(() => {
    const me = this.auth.admin()?.id;

    return (this.screen.data() ?? []).filter((task) => task.assignedTo === me);
  });

  /** Nobody's yet: any operator of the kiosk may take one. */
  readonly pool = computed(() => (this.screen.data() ?? []).filter((task) => !task.assignedTo));

  /** What the navigation counts: what waits for this operator, and what waits for anybody. */
  readonly waiting = computed(() => this.mine().length + this.pool().length);

  /** Called from the constructor of whatever shows the tasks or their count. */
  watch(): void {
    this.watchers++;
    inject(DestroyRef).onDestroy(() => this.watchers--);

    void this.screen.reload();
  }

  reload(): Promise<void> {
    return this.screen.reload();
  }

  /** A task as an action's answer gave it: shown at once, the list is asked for behind it. */
  apply(changed: ServiceTask): void {
    const open = openTaskStatuses.includes(changed.status);
    const rest = (this.screen.data() ?? []).filter((task) => task.id !== changed.id);

    this.screen.show(open ? [changed, ...rest] : rest);
    void this.screen.reload();
  }
}
