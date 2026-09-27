import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, PlatformSetting } from '../../core/api/admin-api.service';
import { describeError } from '../login/login.component';

/** Settings grouped by the prefix of their key, as the panel shows them. */
interface SettingGroup {
  readonly name: string;
  readonly settings: readonly PlatformSetting[];
}

/**
 * Platform settings (plan section 2.4).
 *
 * Everything the platform can be tuned with lives here, with its documented
 * default alongside so it is always clear what has been changed and what it
 * would go back to.
 */
@Component({
  selector: 'app-settings',
  imports: [
    FormsModule,
    MatCardModule,
    MatExpansionModule,
    MatFormFieldModule,
    MatInputModule,
    MatSlideToggleModule,
    MatButtonModule,
    MatIconModule,
    MatProgressBarModule,
    MatTooltipModule,
    TranslatePipe,
  ],
  templateUrl: './settings.component.html',
  styleUrl: './settings.component.scss',
})
export class SettingsComponent {
  private readonly api = inject(AdminApiService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly translate = inject(TranslateService);

  readonly settings = signal<PlatformSetting[]>([]);

  /** Edited values, keyed by setting, kept apart from what the server holds. */
  readonly draft = signal<Record<string, string>>({});

  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  /**
   * Grouped by the part of the key before the dot.
   *
   * Forty-odd settings in one list is unusable; the prefix is already how the
   * plan organises them.
   */
  readonly groups = computed<SettingGroup[]>(() => {
    const byPrefix = new Map<string, PlatformSetting[]>();

    for (const setting of this.settings()) {
      const prefix = setting.key.split('.')[0];
      const existing = byPrefix.get(prefix);

      if (existing) {
        existing.push(setting);
      } else {
        byPrefix.set(prefix, [setting]);
      }
    }

    return [...byPrefix.entries()]
      .map(([name, settings]) => ({ name, settings }))
      .sort((left, right) => left.name.localeCompare(right.name));
  });

  readonly changed = computed(() => {
    const draft = this.draft();

    return this.settings().filter(
      (setting) => draft[setting.key] !== undefined && draft[setting.key] !== setting.value,
    );
  });

  readonly hasChanges = computed(() => this.changed().length > 0);

  constructor() {
    void this.reload();
  }

  async reload(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);

    try {
      const settings = await firstValueFrom(this.api.listSettings());

      this.settings.set(settings);
      this.draft.set(Object.fromEntries(settings.map((setting) => [setting.key, setting.value])));
    } catch (error) {
      this.errorMessage.set(describeError(error));
    } finally {
      this.loading.set(false);
    }
  }

  value(setting: PlatformSetting): string {
    return this.draft()[setting.key] ?? setting.value;
  }

  boolValue(setting: PlatformSetting): boolean {
    return this.value(setting) === 'true';
  }

  set(setting: PlatformSetting, value: string): void {
    this.draft.update((current) => ({ ...current, [setting.key]: value }));
  }

  setBool(setting: PlatformSetting, value: boolean): void {
    this.set(setting, value ? 'true' : 'false');
  }

  isChanged(setting: PlatformSetting): boolean {
    return this.value(setting) !== setting.value;
  }

  /** A group's name in the panel's language; the key prefix itself when there is no translation. */
  groupLabel(name: string): string {
    const key = `settings.group.${name}`;
    const label = this.translate.instant(key);
    return label === key ? name : label;
  }

  /** Whether a group holds anything the operator has edited. */
  groupHasChanges(group: SettingGroup): boolean {
    return group.settings.some((setting) => this.isChanged(setting));
  }

  /** Puts a setting back to the value it was documented with. */
  resetToDefault(setting: PlatformSetting): void {
    this.set(setting, setting.defaultValue);
  }

  revertAll(): void {
    this.draft.set(
      Object.fromEntries(this.settings().map((setting) => [setting.key, setting.value])),
    );
  }

  async save(): Promise<void> {
    if (!this.hasChanges() || this.saving()) {
      return;
    }

    this.saving.set(true);
    this.errorMessage.set(null);

    // Only what changed: sending the whole set would write an audit entry for
    // every setting on the page.
    const values = Object.fromEntries(
      this.changed().map((setting) => [setting.key, this.value(setting)]),
    );

    try {
      const saved = await firstValueFrom(this.api.updateSettings(values));

      this.settings.set(saved);
      this.draft.set(Object.fromEntries(saved.map((setting) => [setting.key, setting.value])));

      this.notify('settings.saved');
    } catch (error) {
      // An unknown key or a value of the wrong type is refused before anything
      // is written, and the server says which one.
      this.errorMessage.set(describeError(error));
    } finally {
      this.saving.set(false);
    }
  }

  private notify(messageKey: string): void {
    this.snackBar.open(this.translate.instant(messageKey) as string, undefined, {
      duration: 4000,
    });
  }
}
