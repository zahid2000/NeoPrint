import { Component, DestroyRef, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSliderModule } from '@angular/material/slider';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import {
  AdminApiService,
  FeatureFlagDetails,
  KioskListItem,
} from '../../core/api/admin-api.service';
import { defaultLanguage, translations } from '../../core/i18n/translations';
import { HubEvents } from '../../core/realtime/hub-events';
import { liveDebounceMs, liveRefresh } from '../../core/realtime/live-refresh';
import { describeError } from '../login/login.component';

/** One flag's unsaved state while the operator is changing it. */
interface FlagDraft {
  defaultEnabled: boolean;
  rolloutPercent: number;
  kioskOverrides: Record<string, boolean>;
}

/** What a flag is on the server, in a form two answers can be compared by. */
function serverState(flags: readonly FeatureFlagDetails[]): string {
  return JSON.stringify(
    [...flags]
      .sort((a, b) => a.key.localeCompare(b.key))
      .map((flag) => [
        flag.key,
        flag.defaultEnabled,
        flag.rolloutPercent,
        Object.entries(flag.kioskOverrides).sort(([a], [b]) => a.localeCompare(b)),
        [...flag.userAllowlist].sort(),
      ]),
  );
}

/**
 * Feature flags (plan section 2.16).
 *
 * A flag has a default, a percentage rollout and per-kiosk overrides. An
 * override always wins over the rollout, which is what makes it safe to try a
 * feature on one kiosk before opening it to a share of the traffic.
 *
 * The page may hold changes that are not saved yet, so it never refreshes by
 * itself. When somebody else saves a flag (`FeatureFlagsChanged`) it says so
 * and offers "Yenilə" - which takes the server's values and keeps what was
 * being changed here (T-253).
 */
@Component({
  selector: 'app-feature-flags',
  imports: [
    MatCardModule,
    MatExpansionModule,
    MatSlideToggleModule,
    MatSliderModule,
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatProgressBarModule,
    MatTooltipModule,
    TranslatePipe,
  ],
  templateUrl: './feature-flags.component.html',
  styleUrl: './feature-flags.component.scss',
})
export class FeatureFlagsComponent {
  private readonly api = inject(AdminApiService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly translate = inject(TranslateService);

  readonly flags = signal<FeatureFlagDetails[]>([]);

  /** Whether this build has a human name for the flag; otherwise the key shows. */
  hasText(flag: FeatureFlagDetails): boolean {
    return `features.flag.${flag.key}.name` in translations[defaultLanguage];
  }

  readonly kiosks = signal<KioskListItem[]>([]);
  readonly drafts = signal<Record<string, FlagDraft>>({});

  readonly loading = signal(false);
  readonly savingKey = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);

  /** The server holds something other than what this page last took in. */
  readonly changedElsewhere = signal(false);

  /** What the server answered when the difference was noticed. */
  private fresh: FeatureFlagDetails[] | null = null;
  private recheck: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    void this.reload();

    liveRefresh({
      events: [HubEvents.featureFlagsChanged],
      reload: () => this.lookForChanges(),
    });

    inject(DestroyRef).onDestroy(() => clearTimeout(this.recheck));
  }

  /**
   * Asks what the server holds now and compares - nothing on the page moves.
   * The event also follows this page's own save, and may come before the
   * save's answer: while a save is on its way the question waits.
   */
  private async lookForChanges(): Promise<void> {
    clearTimeout(this.recheck);

    if (this.loading()) {
      return;
    }

    if (this.savingKey()) {
      this.recheck = setTimeout(() => void this.lookForChanges(), liveDebounceMs);

      return;
    }

    try {
      const fresh = await firstValueFrom(this.api.listFeatureFlags());

      if (this.savingKey() || this.loading()) {
        this.recheck = setTimeout(() => void this.lookForChanges(), liveDebounceMs);

        return;
      }

      const changed = serverState(fresh) !== serverState(this.flags());

      this.fresh = changed ? fresh : null;
      this.changedElsewhere.set(changed);
    } catch {
      // Nothing to say: the header's indicator shows the panel is cut off.
    }
  }

  /**
   * "Yenilə" on the notice: the server's values, except in the flags that
   * were being changed here - those keep what was entered, now set against
   * the new server values.
   */
  takeChanges(): void {
    const fresh = this.fresh;

    if (!fresh) {
      this.changedElsewhere.set(false);

      return;
    }

    const edited = new Map(
      this.flags()
        .filter((flag) => this.isDirty(flag))
        .map((flag) => [flag.key, this.draft(flag.key)]),
    );

    this.flags.set(fresh);
    this.drafts.set(
      Object.fromEntries(
        fresh.map((flag) => [
          flag.key,
          edited.get(flag.key) ?? {
            defaultEnabled: flag.defaultEnabled,
            rolloutPercent: flag.rolloutPercent,
            kioskOverrides: { ...flag.kioskOverrides },
          },
        ]),
      ),
    );

    this.fresh = null;
    this.changedElsewhere.set(false);
  }

  async reload(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);

    try {
      const flags = await firstValueFrom(this.api.listFeatureFlags());
      this.flags.set(flags);
      this.fresh = null;
      this.changedElsewhere.set(false);

      this.drafts.set(
        Object.fromEntries(
          flags.map((flag) => [
            flag.key,
            {
              defaultEnabled: flag.defaultEnabled,
              rolloutPercent: flag.rolloutPercent,
              kioskOverrides: { ...flag.kioskOverrides },
            },
          ]),
        ),
      );

      try {
        this.kiosks.set(await firstValueFrom(this.api.listKiosks()));
      } catch {
        // Managing flags does not require the kiosk permission, so without it
        // the per-kiosk overrides are shown by id rather than by code.
        this.kiosks.set([]);
      }
    } catch (error) {
      this.errorMessage.set(describeError(error));
    } finally {
      this.loading.set(false);
    }
  }

  draft(key: string): FlagDraft {
    return (
      this.drafts()[key] ?? {
        defaultEnabled: false,
        rolloutPercent: 0,
        kioskOverrides: {},
      }
    );
  }

  /** Whether a flag differs from what the server holds. */
  isDirty(flag: FeatureFlagDetails): boolean {
    const draft = this.draft(flag.key);

    return (
      draft.defaultEnabled !== flag.defaultEnabled ||
      draft.rolloutPercent !== flag.rolloutPercent ||
      JSON.stringify(draft.kioskOverrides) !== JSON.stringify(flag.kioskOverrides)
    );
  }

  setDefault(key: string, enabled: boolean): void {
    this.patch(key, { defaultEnabled: enabled });
  }

  setRollout(key: string, percent: number): void {
    this.patch(key, { rolloutPercent: Math.max(0, Math.min(100, percent)) });
  }

  /** Cycles a kiosk between on, off and following the default. */
  cycleOverride(key: string, kioskId: string): void {
    const current = this.draft(key).kioskOverrides[kioskId];
    const overrides = { ...this.draft(key).kioskOverrides };

    if (current === undefined) {
      overrides[kioskId] = true;
    } else if (current) {
      overrides[kioskId] = false;
    } else {
      delete overrides[kioskId];
    }

    this.patch(key, { kioskOverrides: overrides });
  }

  overrideState(key: string, kioskId: string): 'on' | 'off' | 'default' {
    const value = this.draft(key).kioskOverrides[kioskId];

    if (value === undefined) {
      return 'default';
    }

    return value ? 'on' : 'off';
  }

  kioskLabel(kioskId: string): string {
    return this.kiosks().find((kiosk) => kiosk.id === kioskId)?.code ?? kioskId;
  }

  overriddenKioskIds(key: string): string[] {
    return Object.keys(this.draft(key).kioskOverrides);
  }

  private patch(key: string, change: Partial<FlagDraft>): void {
    this.drafts.update((current) => ({
      ...current,
      [key]: { ...this.draft(key), ...change },
    }));
  }

  async save(flag: FeatureFlagDetails): Promise<void> {
    if (this.savingKey()) {
      return;
    }

    this.savingKey.set(flag.key);
    this.errorMessage.set(null);

    const draft = this.draft(flag.key);

    try {
      const saved = await firstValueFrom(
        this.api.updateFeatureFlag(flag.key, {
          defaultEnabled: draft.defaultEnabled,
          rolloutPercent: draft.rolloutPercent,
          kioskOverrides: draft.kioskOverrides,
          userAllowlist: flag.userAllowlist,
        }),
      );

      this.flags.update((flags) =>
        flags.map((candidate) => (candidate.key === saved.key ? saved : candidate)),
      );

      this.notify('features.saved');
    } catch (error) {
      this.errorMessage.set(describeError(error));
    } finally {
      this.savingKey.set(null);
    }
  }

  revert(flag: FeatureFlagDetails): void {
    this.patch(flag.key, {
      defaultEnabled: flag.defaultEnabled,
      rolloutPercent: flag.rolloutPercent,
      kioskOverrides: { ...flag.kioskOverrides },
    });
  }

  private notify(messageKey: string): void {
    this.snackBar.open(this.translate.instant(messageKey) as string, undefined, {
      duration: 4000,
    });
  }
}
