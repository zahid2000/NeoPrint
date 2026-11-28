import { Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { CameraApiService, CameraFootage, CameraMinute } from '../../core/api/camera-api.service';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { TimePipe } from '../../shared/format.pipes';
import { Problem, toProblem } from '../../shared/problem';

/** The panel asks for the latest frame this often while watching live. */
export const LIVE_POLL_MS = 500;

/** No frame for this long: the kiosk is not sending. */
export const LIVE_SILENT_MS = 6000;

/** While the kiosk sends the minutes asked for, the list is read again this often. */
export const FETCH_POLL_MS = 5000;

/** A kiosk that has not answered in this time is said to be maybe offline. */
export const FETCH_SLOW_MS = 2 * 60 * 1000;

/** The longest span the kiosk is asked for at once (the backend's limit). */
export const MAX_FETCH_MINUTES = 60;

const pad = (value: number): string => value.toString().padStart(2, '0');

/**
 * Kiosk detail → "Kamera" (T-483, docs/DESIGN_SYSTEM.md): the camera live,
 * and the recorded minutes of a span - from the file server, or fetched from
 * the kiosk. Leaving the tab stops watching.
 */
@Component({
  selector: 'app-kiosk-camera',
  imports: [
    FormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatTooltipModule,
    TranslatePipe,
    TimePipe,
    EmptyStateComponent,
    ErrorBannerComponent,
  ],
  template: `
    <p class="neo-caption camera__audited">
      <mat-icon aria-hidden="true">policy</mat-icon>
      {{ 'camera.audited' | translate }}
    </p>

    @if (problem(); as current) {
      <app-error-banner [problem]="current" effect="camera.loadFailedEffect" [retryable]="false" />
    }

    @if (footage()?.cameraOn === false) {
      <app-empty-state
        icon="videocam_off"
        [message]="'camera.off' | translate"
        [hint]="'camera.offHint' | translate"
      />
    } @else {
      <section class="neo-card camera__card">
        <h3>{{ 'camera.liveTitle' | translate }}</h3>
        <div class="camera__frame" data-live>
          @if (frameUrl(); as src) {
            <img [src]="src" [alt]="'camera.liveAlt' | translate" />
          } @else {
            <span class="camera__idle">
              <mat-icon aria-hidden="true">videocam</mat-icon>
              {{ 'camera.liveIdle' | translate }}
            </span>
          }
          @if (watching()) {
            <span class="camera__badge" role="status">
              <span class="camera__dot" aria-hidden="true"></span>
              {{ 'camera.liveOn' | translate }}
              @if (frameAt(); as at) {
                <span>{{ at | time }}</span>
              }
            </span>
          }
        </div>
        @if (watching() && silent()) {
          <div class="neo-notice neo-notice--warning" role="status" data-silent>
            <mat-icon aria-hidden="true">videocam_off</mat-icon>
            <span>{{ 'camera.liveNoFrames' | translate }}</span>
          </div>
        }
        <div class="camera__actions">
          @if (watching()) {
            <button mat-stroked-button type="button" (click)="stopLive()" data-stop>
              <mat-icon>stop</mat-icon>
              {{ 'camera.liveStop' | translate }}
            </button>
          } @else {
            <button mat-flat-button color="primary" type="button" (click)="startLive()" data-watch>
              <mat-icon>play_arrow</mat-icon>
              {{ 'camera.liveStart' | translate }}
            </button>
          }
        </div>
      </section>

      <section class="neo-card camera__card">
        <h3>{{ 'camera.recordedTitle' | translate }}</h3>
        <div class="camera__form">
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>{{ 'camera.date' | translate }}</mat-label>
            <input
              matInput
              type="date"
              name="date"
              [ngModel]="date()"
              (ngModelChange)="date.set($event)"
            />
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>{{ 'camera.startTime' | translate }}</mat-label>
            <input
              matInput
              type="time"
              name="time"
              [ngModel]="time()"
              (ngModelChange)="time.set($event)"
            />
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>{{ 'camera.duration' | translate }}</mat-label>
            <mat-select [ngModel]="duration()" (ngModelChange)="duration.set($event)">
              @for (minutes of durations; track minutes) {
                <mat-option [value]="minutes">{{
                  'camera.minutes' | translate: { count: minutes }
                }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <button
            mat-stroked-button
            type="button"
            [disabled]="loading()"
            (click)="show()"
            data-show
          >
            <mat-icon>search</mat-icon>
            {{ 'camera.show' | translate }}
          </button>
        </div>

        @if (shown(); as result) {
          @if (minutes().length > 0) {
            <video
              class="camera__player"
              controls
              autoplay
              [src]="videoUrl() ?? ''"
              [attr.aria-label]="'camera.player' | translate"
              (ended)="next()"
            ></video>
            <mat-button-toggle-group
              class="camera__minutes"
              hideSingleSelectionIndicator
              [attr.aria-label]="'camera.minuteList' | translate"
              [value]="playing()"
              (change)="play($event.value)"
            >
              @for (minute of minutes(); track minute.id) {
                <mat-button-toggle
                  [value]="minute.id"
                  [matTooltip]="
                    (minute.fetched ? 'camera.fetchedNote' : 'camera.archivedNote') | translate
                  "
                  [attr.data-minute]="minute.id"
                >
                  {{ minute.start | time }}
                </mat-button-toggle>
              }
            </mat-button-toggle-group>
            @if (videoUrl(); as url) {
              <a mat-button class="camera__download" [href]="url" [download]="fileName()">
                <mat-icon>download</mat-icon>
                {{ 'camera.download' | translate }}
              </a>
            }
          }

          @if (fetchState(); as state) {
            <div class="neo-notice" role="status" [attr.data-fetch]="state">
              <mat-icon aria-hidden="true">{{
                state === 'nothing' ? 'info' : 'progress_activity'
              }}</mat-icon>
              <span>{{
                (state === 'slow'
                  ? 'camera.fetchSlow'
                  : state === 'nothing'
                    ? 'camera.fetchedNothing'
                    : 'camera.fetching'
                ) | translate
              }}</span>
            </div>
          } @else if (missing() > 0 && result.cameraOn) {
            <div class="neo-notice" role="status" data-missing>
              <mat-icon aria-hidden="true">cloud_download</mat-icon>
              <span>{{
                minutes().length > 0
                  ? ('camera.partial' | translate: { count: minutes().length })
                  : ('camera.allOnKiosk' | translate)
              }}</span>
              <button
                mat-stroked-button
                type="button"
                [disabled]="duration() > maxFetch || loading()"
                (click)="fetch()"
                data-fetch-button
              >
                <mat-icon>cloud_download</mat-icon>
                {{ 'camera.fetch' | translate }}
              </button>
            </div>
            @if (duration() > maxFetch) {
              <p class="neo-caption">{{ 'camera.fetchTooLong' | translate }}</p>
            }
          }

          @if (minutes().length === 0 && fetchState() === null && missing() === 0) {
            <app-empty-state
              icon="video_library"
              [message]="'camera.none' | translate"
              [hint]="'camera.noneHint' | translate"
            />
          }
        }
      </section>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
    }

    h3,
    p {
      margin: 0;
    }

    .camera__audited {
      display: flex;
      gap: var(--space-1);
      align-items: center;
    }

    .camera__audited mat-icon {
      width: var(--icon-size);
      height: var(--icon-size);
      font-size: var(--icon-size);
    }

    .camera__card {
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
    }

    .camera__frame {
      position: relative;
      display: flex;
      align-items: center;
      justify-content: center;
      width: 100%;
      max-width: 960px;
      aspect-ratio: 16 / 9;
      border-radius: var(--radius-md);
      background: var(--neutral-900);
      overflow: hidden;
    }

    .camera__frame img {
      width: 100%;
      height: 100%;
      object-fit: contain;
    }

    .camera__idle {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      align-items: center;
      color: var(--neutral-0);
      font-size: var(--font-size-body-small);
    }

    .camera__idle mat-icon {
      width: var(--space-12);
      height: var(--space-12);
      font-size: var(--space-12);
    }

    .camera__badge {
      position: absolute;
      top: var(--space-2);
      left: var(--space-2);
      display: inline-flex;
      gap: var(--space-2);
      align-items: center;
      padding: var(--space-1) var(--space-2);
      border-radius: var(--radius-sm);
      background: var(--neutral-0);
      color: var(--neutral-900);
      font-size: var(--font-size-caption);
      font-weight: 600;
    }

    .camera__dot {
      width: var(--space-2);
      height: var(--space-2);
      border-radius: 50%;
      background: var(--danger-500);
    }

    .camera__actions,
    .camera__form {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-3);
      align-items: center;
    }

    .camera__player {
      width: 100%;
      max-width: 960px;
      aspect-ratio: 16 / 9;
      border-radius: var(--radius-md);
      background: var(--neutral-900);
    }

    .camera__minutes {
      flex-wrap: wrap;
      max-width: 960px;
    }

    .camera__download {
      align-self: flex-start;
    }
  `,
})
export class KioskCameraComponent implements OnInit, OnDestroy {
  private readonly api = inject(CameraApiService);

  readonly kioskId = input.required<string>();
  /** For the downloaded file's name. */
  readonly kioskCode = input<string>('kiosk');

  readonly durations = [5, 15, 30, 60] as const;
  readonly maxFetch = MAX_FETCH_MINUTES;

  readonly problem = signal<Problem | null>(null);
  readonly loading = signal(false);
  readonly footage = signal<CameraFootage | null>(null);
  /** The footage of the span "Göstər" asked for; null before. */
  readonly shown = signal<CameraFootage | null>(null);

  readonly date = signal('');
  readonly time = signal('');
  readonly duration = signal<number>(15);

  readonly watching = signal(false);
  readonly frameUrl = signal<string | null>(null);
  readonly frameAt = signal<Date | null>(null);
  private readonly liveSince = signal<number>(0);
  /** When the latest frame reached the panel, on this browser's clock: the server's may differ. */
  private readonly frameReceived = signal<number | null>(null);
  private readonly now = signal(Date.now());

  readonly playing = signal<string | null>(null);
  readonly videoUrl = signal<string | null>(null);
  private readonly fetchAskedAt = signal<number | null>(null);

  private liveTimer: ReturnType<typeof setTimeout> | null = null;
  private fetchTimer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;

  readonly minutes = computed<readonly CameraMinute[]>(() => this.shown()?.minutes ?? []);

  /** Minutes of the span not on the server. */
  readonly missing = computed(() => {
    const shown = this.shown();
    if (!shown) {
      return 0;
    }
    const request = shown.request;
    // The kiosk already answered for this span: what it had is here.
    if (request?.status === 'Delivered') {
      return 0;
    }
    return Math.max(0, this.duration() - shown.minutes.length);
  });

  /** Where fetching from the kiosk stands, or null when nothing is being fetched. */
  readonly fetchState = computed<'sending' | 'slow' | 'nothing' | null>(() => {
    const request = this.shown()?.request;
    if (!request) {
      return null;
    }
    if (request.status === 'Delivered') {
      return request.delivered === 0 && this.minutes().length === 0 ? 'nothing' : null;
    }
    const asked = this.fetchAskedAt() ?? Date.parse(request.createdAt);
    return this.now() - asked > FETCH_SLOW_MS ? 'slow' : 'sending';
  });

  /** Watching, and no frame for a while. */
  readonly silent = computed(() => {
    const last = this.frameReceived() ?? this.liveSince();
    return this.now() - last > LIVE_SILENT_MS;
  });

  readonly fileName = computed(() => {
    const minute = this.minutes().find((item) => item.id === this.playing());
    if (!minute) {
      return `${this.kioskCode()}.mp4`;
    }
    const start = new Date(minute.start);
    return `${this.kioskCode()}-${start.getFullYear()}${pad(start.getMonth() + 1)}${pad(start.getDate())}-${pad(start.getHours())}${pad(start.getMinutes())}.mp4`;
  });

  async ngOnInit(): Promise<void> {
    // The last quarter of an hour by default.
    const from = new Date(Date.now() - 15 * 60 * 1000);
    this.date.set(`${from.getFullYear()}-${pad(from.getMonth() + 1)}-${pad(from.getDate())}`);
    this.time.set(`${pad(from.getHours())}:${pad(from.getMinutes())}`);

    // Whether the camera is on at all: an empty minute's span answers it.
    try {
      const [start, end] = this.span();
      this.footage.set(await firstValueFrom(this.api.footage(this.kioskId(), start, end)));
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    if (this.watching()) {
      this.stopLive();
    }
    this.clearFetchTimer();
    this.release(this.videoUrl());
  }

  async startLive(): Promise<void> {
    this.problem.set(null);
    try {
      await firstValueFrom(this.api.startLive(this.kioskId()));
    } catch (error) {
      this.problem.set(toProblem(error));
      return;
    }
    this.watching.set(true);
    this.liveSince.set(Date.now());
    this.now.set(Date.now());
    this.scheduleFrame(0);
  }

  stopLive(): void {
    this.watching.set(false);
    if (this.liveTimer !== null) {
      clearTimeout(this.liveTimer);
      this.liveTimer = null;
    }
    this.release(this.frameUrl());
    this.frameUrl.set(null);
    this.frameAt.set(null);
    this.frameReceived.set(null);
    // The kiosk stops at its next frame; a failure here only means it stops on its own.
    this.api.stopLive(this.kioskId()).subscribe({ error: () => undefined });
  }

  async show(): Promise<void> {
    this.loading.set(true);
    this.problem.set(null);
    this.clearFetchTimer();
    this.fetchAskedAt.set(null);
    try {
      const [from, to] = this.span();
      const footage = await firstValueFrom(this.api.footage(this.kioskId(), from, to));
      this.footage.set(footage);
      this.shown.set(footage);
      this.now.set(Date.now());
      if (footage.minutes.length > 0) {
        await this.play(footage.minutes[0].id);
      } else {
        this.playing.set(null);
        this.release(this.videoUrl());
        this.videoUrl.set(null);
      }
      if (footage.request?.status === 'Pending') {
        this.scheduleRefresh();
      }
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  /** Asks the kiosk for the span's minutes; the list fills in as they come. */
  async fetch(): Promise<void> {
    this.problem.set(null);
    try {
      const [from, to] = this.span();
      const request = await firstValueFrom(this.api.requestFootage(this.kioskId(), from, to));
      this.fetchAskedAt.set(Date.now());
      this.now.set(Date.now());
      this.shown.update((shown) => (shown ? { ...shown, request } : shown));
      this.scheduleRefresh();
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }

  async play(minuteId: string): Promise<void> {
    if (!minuteId) {
      return;
    }
    this.playing.set(minuteId);
    try {
      const blob = await firstValueFrom(this.api.minute(this.kioskId(), minuteId));
      if (this.playing() !== minuteId || this.destroyed) {
        return;
      }
      this.release(this.videoUrl());
      this.videoUrl.set(URL.createObjectURL(blob));
    } catch (error) {
      this.problem.set(toProblem(error));
    }
  }

  /** The minute ended: the next one plays. */
  async next(): Promise<void> {
    const list = this.minutes();
    const index = list.findIndex((minute) => minute.id === this.playing());
    if (index >= 0 && index + 1 < list.length) {
      await this.play(list[index + 1].id);
    }
  }

  /** The span the form says, in the panel's own time zone. */
  private span(): [Date, Date] {
    const from = new Date(`${this.date()}T${this.time() || '00:00'}:00`);
    return [from, new Date(from.getTime() + this.duration() * 60 * 1000)];
  }

  private scheduleFrame(delay: number): void {
    this.liveTimer = setTimeout(() => void this.frame(), delay);
  }

  private async frame(): Promise<void> {
    if (!this.watching() || this.destroyed) {
      return;
    }
    this.now.set(Date.now());
    try {
      const response = await firstValueFrom(this.api.liveFrame(this.kioskId()));
      if (response.status === 200 && response.body && this.watching()) {
        const previous = this.frameUrl();
        this.frameUrl.set(URL.createObjectURL(response.body));
        this.release(previous);
        const at = response.headers.get('X-Frame-At');
        this.frameAt.set(at ? new Date(at) : new Date());
        this.frameReceived.set(Date.now());
      }
    } catch {
      // A missed frame: the next one is asked for; the silence notice says when it lasts.
    }
    if (this.watching() && !this.destroyed) {
      this.scheduleFrame(LIVE_POLL_MS);
    }
  }

  private scheduleRefresh(): void {
    this.clearFetchTimer();
    this.fetchTimer = setTimeout(() => void this.refresh(), FETCH_POLL_MS);
  }

  /** While the kiosk sends: the list read again, new minutes added. */
  private async refresh(): Promise<void> {
    if (this.destroyed) {
      return;
    }
    this.now.set(Date.now());
    try {
      const [from, to] = this.span();
      const footage = await firstValueFrom(this.api.footage(this.kioskId(), from, to));
      const hadNone = this.minutes().length === 0;
      this.shown.set(footage);
      if (hadNone && footage.minutes.length > 0) {
        await this.play(footage.minutes[0].id);
      }
      if (footage.request?.status === 'Pending') {
        this.scheduleRefresh();
      }
    } catch {
      this.scheduleRefresh();
    }
  }

  private clearFetchTimer(): void {
    if (this.fetchTimer !== null) {
      clearTimeout(this.fetchTimer);
      this.fetchTimer = null;
    }
  }

  private release(url: string | null): void {
    if (url && typeof URL.revokeObjectURL === 'function') {
      URL.revokeObjectURL(url);
    }
  }
}
