import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { MatBadgeModule } from '@angular/material/badge';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { filter, map } from 'rxjs';

import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { SupportedLanguage, languageNames, supportedLanguages } from '../../core/i18n/languages';
import { AdminHubService } from '../../core/realtime/admin-hub.service';
import { HubEvents, IncidentEvent } from '../../core/realtime/hub-events';
import { IncidentLiveStore } from '../../core/realtime/incident-live.store';
import { panelConnection } from '../../core/realtime/panel-connection';
import { LiveIndicatorComponent } from '../../shared/live-indicator.component';
import { OperatorNotifyService } from './operator-notify.service';
import { OperatorTasksStore } from './operator-tasks.store';
import { ServiceTask, SupplyAlert } from './operator.models';

/**
 * The operator panel's frame (plan section 8B; docs/DESIGN_SYSTEM.md 5.4): a
 * bar on top, one column of content, four destinations at the bottom where a
 * thumb reaches them.
 *
 * It also says out loud what cannot wait - a task given to this operator, a
 * Critical alert, an incident - whichever page is open.
 */
@Component({
  selector: 'app-operator-shell',
  imports: [
    RouterOutlet,
    RouterLink,
    MatBadgeModule,
    MatButtonModule,
    MatDividerModule,
    MatIconModule,
    MatMenuModule,
    TranslatePipe,
    LiveIndicatorComponent,
  ],
  template: `
    <header class="operator__bar">
      <span class="operator__title">{{ 'operator.title' | translate }}</span>
      <app-live-indicator />
      <button
        mat-icon-button
        type="button"
        [attr.aria-label]="'operator.account' | translate"
        [matMenuTriggerFor]="accountMenu"
      >
        <span class="operator__avatar" aria-hidden="true">{{ initial() }}</span>
      </button>

      <mat-menu #accountMenu>
        <div class="operator__account">
          <strong>{{ admin()?.fullName || admin()?.email }}</strong>
          <span>{{ admin()?.roles?.join(', ') }}</span>
        </div>
        <mat-divider />
        @for (language of languages; track language.code) {
          <button mat-menu-item type="button" (click)="selectLanguage(language.code)">
            <mat-icon>{{ language.code === currentLanguage() ? 'check_circle' : 'language' }}</mat-icon>
            {{ language.name }}
          </button>
        }
        <mat-divider />
        @if (canFullPanel()) {
          <a mat-menu-item routerLink="/">
            <mat-icon>dashboard</mat-icon>
            {{ 'operator.fullPanel' | translate }}
          </a>
        }
        <button mat-menu-item type="button" (click)="signOut()">
          <mat-icon>logout</mat-icon>
          {{ 'operator.signOut' | translate }}
        </button>
      </mat-menu>
    </header>

    <main class="operator__content">
      <router-outlet />
    </main>

    <nav class="operator__nav" [attr.aria-label]="'operator.navLabel' | translate">
      @for (item of destinations(); track item.path) {
        <a
          class="operator__destination"
          [routerLink]="item.path"
          [class.operator__destination--active]="item.active"
          [attr.aria-current]="item.active ? 'page' : null"
        >
          <mat-icon
            aria-hidden="false"
            [matBadge]="item.count"
            [matBadgeHidden]="item.count === 0"
            [attr.aria-label]="item.count > 0 ? item.count : null"
            >{{ item.icon }}</mat-icon
          >
          <span>{{ item.label | translate }}</span>
        </a>
      }
    </nav>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 100dvh;
      background: var(--neutral-50);
    }

    .operator__bar {
      position: sticky;
      top: 0;
      z-index: 2;
      display: flex;
      gap: var(--space-2);
      align-items: center;
      min-height: var(--space-12);
      padding: var(--space-1) var(--space-2) var(--space-1) var(--space-4);
      background: var(--neutral-0);
      border-bottom: 1px solid var(--neutral-200);
    }

    .operator__title {
      flex: 1;
      min-width: 0;
      overflow: hidden;
      font-weight: var(--font-weight-semibold);
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .operator__avatar {
      display: grid;
      place-items: center;
      width: var(--space-8);
      height: var(--space-8);
      border-radius: var(--radius-full);
      background: var(--info-bg);
      color: var(--info-500);
      font-size: var(--font-size-body-small);
      font-weight: var(--font-weight-semibold);
    }

    .operator__account {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      padding: var(--space-2) var(--space-4);
      font-size: var(--font-size-body-small);

      span {
        color: var(--neutral-600);
      }
    }

    // One column, no wider than a hand's reach; the navigation never covers its end.
    .operator__content {
      flex: 1;
      width: 100%;
      max-width: 640px;
      margin: 0 auto;
      padding: var(--space-4) var(--space-4) calc(var(--space-16) + var(--space-6));
      box-sizing: border-box;
    }

    .operator__nav {
      position: fixed;
      right: 0;
      bottom: 0;
      left: 0;
      z-index: 2;
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      background: var(--neutral-0);
      border-top: 1px solid var(--neutral-200);
      padding-bottom: env(safe-area-inset-bottom);
    }

    .operator__destination {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      align-items: center;
      justify-content: center;
      min-height: var(--space-16);
      color: var(--neutral-600);
      font-size: var(--font-size-caption);
      text-decoration: none;

      // The icon font takes its size from the text around it; a destination's
      // words are small, its icon is not.
      mat-icon {
        width: var(--space-6);
        height: var(--space-6);
        font-size: var(--space-6);
        overflow: visible;
      }

      span {
        max-width: 100%;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
    }

    .operator__destination--active {
      color: var(--brand-600);
      font-weight: var(--font-weight-semibold);
    }
  `,
})
export class OperatorShellComponent {
  private readonly auth = inject(AuthService);
  private readonly hub = inject(AdminHubService);
  private readonly translate = inject(TranslateService);
  private readonly notify = inject(OperatorNotifyService);
  private readonly tasks = inject(OperatorTasksStore);
  private readonly incidents = inject(IncidentLiveStore);

  readonly admin = this.auth.admin;
  readonly initial = computed(() => (this.admin()?.fullName || this.admin()?.email || '?').charAt(0).toUpperCase());

  /** The operator role itself may open the full panel's dashboard; an account without it has nowhere to go there. */
  readonly canFullPanel = computed(() => this.auth.can(Permissions.dashboardView));

  readonly languages = supportedLanguages.map((code) => ({ code, name: languageNames[code] }));
  readonly currentLanguage = signal<string>(this.translate.getCurrentLang());

  private readonly router = inject(Router);

  /** Where the panel is now, without the query. */
  private readonly path = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      map(() => this.router.url.split('?')[0]),
    ),
    { initialValue: this.router.url.split('?')[0] },
  );

  /** The four places, each with what waits there. A kiosk's own page belongs to "Kiosklar". */
  readonly destinations = computed(() => {
    const path = this.path();
    const under = (prefix: string): boolean => path === prefix || path.startsWith(prefix + '/');

    return [
      {
        path: '/operator',
        icon: 'storefront',
        label: 'operator.nav.kiosks',
        count: 0,
        active: path === '/operator' || under('/operator/kiosks'),
      },
      {
        path: '/operator/tasks',
        icon: 'task_alt',
        label: 'operator.nav.tasks',
        count: this.tasks.waiting(),
        active: under('/operator/tasks'),
      },
      {
        path: '/operator/incidents',
        icon: 'report',
        label: 'operator.nav.incidents',
        count: this.incidents.open() ?? 0,
        active: under('/operator/incidents'),
      },
      {
        path: '/operator/inventory',
        icon: 'inventory_2',
        label: 'operator.nav.inventory',
        count: 0,
        active: under('/operator/inventory'),
      },
    ];
  });

  constructor() {
    panelConnection();

    // The counts on the navigation, whichever page is open.
    if (this.auth.can(Permissions.serviceTasksView)) {
      this.tasks.watch();
    }

    if (this.auth.can(Permissions.incidentsView)) {
      this.incidents.watch();
    }

    const text = (key: string, params?: Record<string, unknown>): string =>
      this.translate.instant(key, params) as string;

    // What cannot wait is said out loud - once the operator has allowed it.
    const off = [
      this.hub.on<ServiceTask>(HubEvents.serviceTaskAssigned, (task) => {
        if (task.assignedTo === this.auth.admin()?.id) {
          this.notify.announce(
            text('operator.tasks.notifyNew', { type: text('operator.taskType.' + task.type) }),
            `${task.kioskCode ?? ''} ${task.notes ?? ''}`.trim(),
            `task-${task.id}`,
          );
        }
      }),
      this.hub.on<SupplyAlert>(HubEvents.supplyAlertRaised, (alert) => {
        if (alert.level === 'Critical') {
          this.notify.announce(
            text('operator.tasks.notifyAlert', { resource: text('operator.resource.' + alert.resource) }),
            alert.kioskCode ?? '',
            `alert-${alert.id}`,
          );
        }
      }),
      this.hub.on<IncidentEvent>(HubEvents.incidentRaised, (incident) => {
        this.notify.announce(
          text('operator.tasks.notifyIncident'),
          text('money.incidentSeverity.' + (incident.severity ?? 'High')),
          `incident-${incident.incidentId}`,
        );
      }),
    ];

    inject(DestroyRef).onDestroy(() => off.forEach((unsubscribe) => unsubscribe()));
  }

  selectLanguage(language: SupportedLanguage): void {
    this.translate.use(language);
    this.currentLanguage.set(language);

    // A per-browser convenience, not state the server needs to know about.
    localStorage.setItem('neoprint.language', language);
  }

  signOut(): void {
    void this.auth.signOut();
  }
}
