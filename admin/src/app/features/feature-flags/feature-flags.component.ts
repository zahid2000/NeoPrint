import { Component, inject, signal } from '@angular/core';
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
import { describeError } from '../login/login.component';

/** One flag's unsaved state while the operator is changing it. */
interface FlagDraft {
  defaultEnabled: boolean;
  rolloutPercent: number;
  kioskOverrides: Record<string, boolean>;
}

/**
 * Feature flags (plan section 2.16).
 *
 * A flag has a default, a percentage rollout and per-kiosk overrides. An
 * override always wins over the rollout, which is what makes it safe to try a
 * feature on one kiosk before opening it to a share of the traffic.
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
  readonly kiosks = signal<KioskListItem[]>([]);
  readonly drafts = signal<Record<string, FlagDraft>>({});

  readonly loading = signal(false);
  readonly savingKey = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);

  constructor() {
    void this.reload();
  }

  async reload(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);

    try {
      const flags = await firstValueFrom(this.api.listFeatureFlags());
      this.flags.set(flags);

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
