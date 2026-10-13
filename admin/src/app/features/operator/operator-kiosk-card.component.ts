import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';

import { KioskStatus } from '../../core/api/admin-api.service';
import { lowPaperShare } from '../../core/api/step3.models';
import { KioskLiveStore } from '../../core/realtime/kiosk-live.store';
import { DateTimePipe } from '../../shared/format.pipes';
import { StatusChipComponent } from '../../shared/status-chip.component';
import { OperatorKioskCard } from './operator.models';

/** One reason a kiosk needs somebody: its tone, its icon, and the words. */
export interface CardConcern {
  readonly tone: 'danger' | 'warning';
  readonly icon: string;
  readonly key: string;
  readonly params?: Record<string, unknown>;

  /** Translated before it is put into {@link key}'s text. */
  readonly translate?: Record<string, string>;
}

const papers = ['A4', 'A3'] as const;

/** Why a kiosk needs somebody, the most pressing first (docs/DESIGN_SYSTEM.md 5.4, "Kiosk kartı"). */
export function concernsOf(card: OperatorKioskCard, status: string): CardConcern[] {
  const found: CardConcern[] = [];

  if (card.safeModeSince) {
    found.push({ tone: 'danger', icon: 'report', key: 'operator.card.safeMode' });
  }

  for (const alert of card.alerts) {
    found.push({
      tone: alert.level === 'Critical' ? 'danger' : 'warning',
      icon: alert.level === 'Critical' ? 'error' : 'warning',
      key: 'operator.card.alert',
      params: { value: alert.value },
      translate: { resource: 'operator.resource.' + alert.resource, unit: 'operator.unit.' + alert.unit },
    });
  }

  for (const size of papers) {
    const tray = card.paper[size];

    if (tray && tray.capacity > 0 && tray.counted === false) {
      found.push({ tone: 'warning', icon: 'warning', key: 'operator.card.paperUnset', params: { size } });
    }
  }

  // A kiosk that is out of touch cannot say anything true about its printer.
  if (card.printer && !card.printer.stale && status !== 'Offline') {
    if (card.printer.reasons.length > 0) {
      found.push({
        tone: 'danger',
        icon: 'print_disabled',
        key: 'operator.card.printerReasons',
        params: { reasons: card.printer.reasons.join(', ') },
      });
    } else if (card.printer.online === false) {
      found.push({ tone: 'danger', icon: 'print_disabled', key: 'operator.card.printerOffline' });
    }
  }

  if (card.openIncidents > 0) {
    found.push({
      tone: card.worstIncident === 'Medium' ? 'warning' : 'danger',
      icon: 'report',
      key: 'operator.card.incidents',
      params: { count: card.openIncidents },
    });
  }

  return found;
}

/**
 * A kiosk's health card (plan section 8B, "Mənim kiosklarım"): its status, why
 * it needs somebody, its printer, paper, toner, queue and when it was last
 * heard from. The same card heads the kiosk's own page.
 *
 * The status is the store's (T-254): the card and every other page say the
 * same at the same moment.
 */
@Component({
  selector: 'app-operator-kiosk-card',
  imports: [MatIconModule, TranslatePipe, StatusChipComponent, DateTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let kiosk = card();

    <div class="op-card__head">
      <!-- On the kiosk's own page the name is the page's heading; in the list it is one card among several. -->
      @if (heading()) {
        <h1 class="op-card__name op-card__heading">
          {{ kiosk.code }} — {{ kiosk.name }}
          <span class="op-card__sub">{{ kiosk.address }}</span>
        </h1>
      } @else {
        <span class="op-card__name">
          {{ kiosk.code }} — {{ kiosk.name }}
          <span class="op-card__sub">{{ kiosk.address }}</span>
        </span>
      }
      <app-status-chip [status]="status()" [text]="'kiosks.status.' + status() | translate" />
    </div>

    <ul class="op-rows">
      @for (concern of concerns(); track $index) {
        <li class="op-row" [class.op-row--danger]="concern.tone === 'danger'" [class.op-row--warning]="concern.tone === 'warning'">
          <mat-icon aria-hidden="true">{{ concern.icon }}</mat-icon>
          <span>{{ concern.key | translate: paramsOf(concern) }}</span>
        </li>
      } @empty {
        @if (fine()) {
          <li class="op-row op-row--muted">
            <mat-icon aria-hidden="true">check_circle</mat-icon>
            <span>{{ 'operator.kiosks.fine' | translate }}</span>
          </li>
        }
      }

      <li class="op-row">
        <mat-icon aria-hidden="true">print</mat-icon>
        <span>
          @if (kiosk.printer; as printer) {
            {{ 'operator.card.printer' | translate: { name: printer.name } }}
            @if (printer.stale) {
              <span class="op-card__sub">
                {{ 'operator.card.printerStale' | translate: { time: (printer.readAt | dateTime) } }}
              </span>
            }
          } @else {
            {{ 'operator.card.printerNone' | translate }}
          }
        </span>
      </li>

      @for (tray of trays(); track tray.size) {
        <li class="op-row op-row--tray">
          <mat-icon aria-hidden="true">description</mat-icon>
          <span class="tray">
            {{ 'operator.card.paper' | translate: { size: tray.size, current: tray.current, capacity: tray.capacity } }}
            @if (tray.runsOutAt) {
              <span class="op-card__sub">
                {{ 'operator.card.runsOut' | translate: { time: (tray.runsOutAt | dateTime) } }}
              </span>
            }
            <span
              class="op-bar"
              role="progressbar"
              [attr.aria-valuenow]="tray.current"
              aria-valuemin="0"
              [attr.aria-valuemax]="tray.capacity"
            >
              <span class="op-bar__fill" [class.op-bar__fill--low]="tray.low" [style.width.%]="tray.percent"></span>
            </span>
          </span>
        </li>
      }

      @for (level of kiosk.levels; track level.resource + level.variant) {
        <li class="op-row">
          <mat-icon aria-hidden="true">invert_colors</mat-icon>
          <span>{{ 'operator.card.level' | translate: { name: level.name, percent: level.percent } }}</span>
        </li>
      }

      <li class="op-row">
        <mat-icon aria-hidden="true">queue</mat-icon>
        <span>{{ 'operator.card.queue' | translate: { count: kiosk.queueLength } }}</span>
      </li>

      @if (kiosk.openTasks > 0) {
        <li class="op-row">
          <mat-icon aria-hidden="true">task_alt</mat-icon>
          <span>{{ 'operator.card.tasks' | translate: { count: kiosk.openTasks } }}</span>
        </li>
      }

      <li class="op-row op-row--muted">
        <mat-icon aria-hidden="true">history</mat-icon>
        <span>
          @if (kiosk.lastHeartbeatAt) {
            {{ 'operator.card.lastSeen' | translate: { time: (kiosk.lastHeartbeatAt | dateTime) } }}
          } @else {
            {{ 'operator.card.neverSeen' | translate }}
          }
        </span>
      </li>
    </ul>
  `,
  styleUrl: './operator.scss',
  styles: `
    .op-card__heading {
      margin: 0;
      font-size: var(--font-size-h2);
    }

    .tray {
      display: flex;
      flex: 1;
      flex-direction: column;
      gap: var(--space-1);
      min-width: 0;
    }
  `,
})
export class OperatorKioskCardComponent {
  private readonly store = inject(KioskLiveStore);
  private readonly translate = inject(TranslateService);

  readonly card = input.required<OperatorKioskCard>();

  /** Whether this card heads a page, and so carries the page's heading. */
  readonly heading = input(false);

  /** The status every page shows for this kiosk now; the card's own until the store has heard of it. */
  readonly status = computed<string>(() => this.store.statusOf(this.card().id) ?? (this.card().status as KioskStatus));

  readonly concerns = computed(() => concernsOf(this.card(), this.status()));

  /**
   * A reason's parameters, with the names inside them in the reader's
   * language: "A4 kağız: 20 vərəq qalıb" is one sentence, not a key inside a
   * key. Asked from the template, so a change of language is followed.
   */
  paramsOf(concern: CardConcern): Record<string, unknown> {
    const named = Object.fromEntries(
      Object.entries(concern.translate ?? {}).map(([name, key]) => [name, this.translate.instant(key) as string]),
    );

    return { ...concern.params, ...named };
  }

  /** In service and nothing to do: said in words, not left as an empty space. */
  readonly fine = computed(() => this.status() === 'Online' && this.card().openTasks === 0);

  readonly trays = computed(() => {
    const card = this.card();

    // A tray nobody has counted has no level to show: it is among the reasons above instead (T-258).
    return papers
      .filter((size) => (card.paper[size]?.capacity ?? 0) > 0 && card.paper[size].counted !== false)
      .map((size) => {
        const tray = card.paper[size];
        const forecast = card.forecasts.find((item) => item.resource === `Paper${size}`);

        return {
          size,
          current: tray.current,
          capacity: tray.capacity,
          low: tray.current < tray.capacity * lowPaperShare,
          percent: Math.min(100, (tray.current / tray.capacity) * 100),
          runsOutAt: forecast?.runsOutAt ?? null,
        };
      });
  });
}
