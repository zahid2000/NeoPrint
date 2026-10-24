import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import {
  KioskVersion,
  NewRollout,
  Release,
  ReleaseChannel,
  ReleasesApiService,
  Rollout,
} from '../../core/api/releases-api.service';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { BytesPipe } from '../../shared/format.pipes';
import { Problem, toProblem } from '../../shared/problem';

type FileSlot = 'package' | 'manifest' | 'signature';

/**
 * "Paket yüklə" (docs/DESIGN_SYSTEM.md, T-317): the package, its manifest and
 * its signature, and the channel it starts on. The server checks the
 * signature the way a kiosk will; what it refuses is said in the banner.
 */
@Component({
  selector: 'app-upload-release-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    TranslatePipe,
    ErrorBannerComponent,
    BytesPipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>{{ 'releases.uploadTitle' | translate }}</h2>
    <mat-dialog-content class="neo-dialog">
      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="releases.uploadFailedEffect" />
      }
      <p>{{ 'releases.uploadExplanation' | translate }}</p>

      @for (slot of slots; track slot) {
        <div class="release-file" [attr.data-file]="slot">
          <span class="release-file__label">{{ 'releases.file.' + slot | translate }}</span>
          <button mat-stroked-button type="button" (click)="input.click()" [disabled]="busy()">
            <mat-icon>upload</mat-icon>{{ 'releases.file.choose' | translate }}
          </button>
          <input #input type="file" hidden [accept]="accept[slot]" (change)="choose(slot, input)" />
          @if (files()[slot]; as file) {
            <span class="release-file__name">{{ file.name }} <span class="neo-caption">{{ file.size | bytes }}</span></span>
          } @else {
            <span class="neo-caption">{{ 'releases.file.none' | translate }}</span>
          }
        </div>
      }

      <mat-form-field>
        <mat-label>{{ 'releases.channelLabel' | translate }}</mat-label>
        <mat-select [(ngModel)]="channel" [disabled]="busy()">
          <mat-option value="Pilot">{{ 'releases.channel.Pilot' | translate }}</mat-option>
          <mat-option value="Stable">{{ 'releases.channel.Stable' | translate }}</mat-option>
        </mat-select>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close [disabled]="busy()">{{ 'common.cancel' | translate }}</button>
      <button mat-flat-button type="button" data-action="upload-confirm" [disabled]="!ready() || busy()" (click)="upload()">
        @if (busy()) {
          <mat-spinner diameter="18" />
        }
        {{ 'releases.uploadConfirm' | translate }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .release-file {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      align-items: center;
      margin-bottom: var(--space-3);
    }

    .release-file__label {
      flex: 0 0 100%;
      font-weight: var(--font-weight-medium);
    }

    .release-file__name {
      overflow-wrap: anywhere;
    }
  `,
})
export class UploadReleaseDialogComponent {
  private readonly api = inject(ReleasesApiService);
  private readonly ref = inject<MatDialogRef<UploadReleaseDialogComponent, Release>>(MatDialogRef);

  readonly slots: readonly FileSlot[] = ['package', 'manifest', 'signature'];
  readonly accept: Record<FileSlot, string> = { package: '.zip,.gz,.tar.gz', manifest: '.json', signature: '.sig' };

  readonly files = signal<Partial<Record<FileSlot, File>>>({});
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);
  readonly ready = computed(() => this.slots.every((slot) => this.files()[slot]));

  channel: ReleaseChannel = 'Pilot';

  choose(slot: FileSlot, input: HTMLInputElement): void {
    const file = input.files?.[0];

    if (file) {
      this.files.update((files) => ({ ...files, [slot]: file }));
    }
  }

  async upload(): Promise<void> {
    const { package: pkg, manifest, signature } = this.files();

    if (!pkg || !manifest || !signature) {
      return;
    }

    this.busy.set(true);
    this.problem.set(null);

    try {
      this.ref.close(await firstValueFrom(this.api.upload({ package: pkg, manifest, signature }, this.channel)));
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }
}

export function openUploadDialog(dialog: MatDialog): Promise<Release | undefined> {
  return firstValueFrom(
    dialog.open<UploadReleaseDialogComponent, void, Release>(UploadReleaseDialogComponent, { width: '560px', maxWidth: 'calc(100vw - 32px)' }).afterClosed(),
  );
}

export interface RolloutDialogData {
  readonly release: Release;
  readonly kiosks: readonly KioskVersion[];
}

/** The rollout targets as the dialog offers them; two of them are a channel. */
type TargetChoice = 'kiosks' | 'pilot' | 'stable' | 'percent' | 'all';

const pilotTargets: readonly TargetChoice[] = ['kiosks', 'pilot'];

/**
 * "Yay" (docs/DESIGN_SYSTEM.md, T-317): who a release goes to. A pilot
 * release goes only to the pilot channel or to kiosks picked by name.
 */
@Component({
  selector: 'app-rollout-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatRadioModule,
    MatSelectModule,
    TranslatePipe,
    ErrorBannerComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>
      {{ 'releases.rolloutTitle' | translate: { component: ('releases.component.' + data.release.component | translate), version: data.release.version } }}
    </h2>
    <mat-dialog-content class="neo-dialog">
      @if (problem(); as problem) {
        <app-error-banner [problem]="problem" effect="releases.rolloutFailedEffect" />
      }
      <p>{{ 'releases.rolloutExplanation' | translate }}</p>

      <mat-radio-group class="rollout__targets" [ngModel]="target()" (ngModelChange)="target.set($event)" [disabled]="busy()">
        @for (choice of targets; track choice) {
          <mat-radio-button [value]="choice" [disabled]="!allowed(choice)" [attr.data-target]="choice">
            {{ 'releases.rolloutTarget.' + choice | translate }}
          </mat-radio-button>
        }
      </mat-radio-group>
      @if (pilot) {
        <p class="neo-row-note"><mat-icon>info</mat-icon>{{ 'releases.pilotOnly' | translate }}</p>
      }

      @if (target() === 'kiosks') {
        @if (platformKiosks.length === 0) {
          <p class="neo-caption">{{ 'releases.noKiosksForPlatform' | translate }}</p>
        } @else {
          <mat-form-field class="rollout__field">
            <mat-label>{{ 'releases.kiosksLabel' | translate }}</mat-label>
            <mat-select multiple [ngModel]="kioskIds()" (ngModelChange)="kioskIds.set($event)" [disabled]="busy()">
              @for (k of platformKiosks; track k.kioskId) {
                <mat-option [value]="k.kioskId">{{ k.code }} · {{ k.name }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }
      }

      @if (target() === 'percent') {
        <mat-form-field class="rollout__field">
          <mat-label>{{ 'releases.percentLabel' | translate }}</mat-label>
          <input matInput type="number" min="1" max="100" [ngModel]="percent()" (ngModelChange)="percent.set($event)" [disabled]="busy()" />
        </mat-form-field>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close [disabled]="busy()">{{ 'common.cancel' | translate }}</button>
      <button mat-flat-button type="button" data-action="rollout-confirm" [disabled]="!ready() || busy()" (click)="create()">
        @if (busy()) {
          <mat-spinner diameter="18" />
        }
        {{ 'releases.rolloutConfirm' | translate }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .rollout__targets {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      margin-bottom: var(--space-3);
    }

    .rollout__field {
      width: 100%;
    }
  `,
})
export class RolloutDialogComponent {
  private readonly api = inject(ReleasesApiService);
  private readonly ref = inject<MatDialogRef<RolloutDialogComponent, Rollout>>(MatDialogRef);
  readonly data = inject<RolloutDialogData>(MAT_DIALOG_DATA);

  readonly targets: readonly TargetChoice[] = ['kiosks', 'pilot', 'stable', 'percent', 'all'];
  readonly pilot = this.data.release.channel === 'Pilot';
  readonly platformKiosks = this.data.kiosks.filter((kiosk) => kiosk.platform === this.data.release.platform);

  readonly target = signal<TargetChoice | null>(null);
  readonly kioskIds = signal<string[]>([]);
  readonly percent = signal<number | null>(null);
  readonly busy = signal(false);
  readonly problem = signal<Problem | null>(null);

  readonly ready = computed(() => {
    switch (this.target()) {
      case 'kiosks':
        return this.kioskIds().length > 0;
      case 'percent': {
        const percent = Number(this.percent());
        return Number.isInteger(percent) && percent >= 1 && percent <= 100;
      }
      case null:
        return false;
      default:
        return true;
    }
  });

  allowed(choice: TargetChoice): boolean {
    return !this.pilot || pilotTargets.includes(choice);
  }

  async create(): Promise<void> {
    const rollout = this.request();

    if (!rollout) {
      return;
    }

    this.busy.set(true);
    this.problem.set(null);

    try {
      this.ref.close(await firstValueFrom(this.api.createRollout(rollout)));
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }

  private request(): NewRollout | null {
    const releaseId = this.data.release.id;

    switch (this.target()) {
      case 'kiosks':
        return { releaseId, target: 'Kiosks', kioskIds: this.kioskIds() };
      case 'pilot':
        return { releaseId, target: 'Channel', channel: 'Pilot' };
      case 'stable':
        return { releaseId, target: 'Channel', channel: 'Stable' };
      case 'percent':
        return { releaseId, target: 'Percent', percent: Number(this.percent()) };
      case 'all':
        return { releaseId, target: 'All' };
      default:
        return null;
    }
  }
}

export function openRolloutDialog(dialog: MatDialog, data: RolloutDialogData): Promise<Rollout | undefined> {
  return firstValueFrom(
    dialog.open<RolloutDialogComponent, RolloutDialogData, Rollout>(RolloutDialogComponent, { data, width: '560px', maxWidth: 'calc(100vw - 32px)' }).afterClosed(),
  );
}
