import { Component, DestroyRef, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { AdminApiService, DeviceDetails } from '../../core/api/admin-api.service';
import { Permissions } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { ErrorBannerComponent } from '../../shared/error-banner.component';
import { NotifyService } from '../../shared/notify.service';
import { Problem, toProblem } from '../../shared/problem';
import { OperatorApiService } from './operator-api.service';
import { OperatorKioskCard } from './operator.models';
import { QrScanner, activationQr } from './qr-scanner';

/**
 * "Kiosk aktivləşdir" (plan sections 7.11, 8B): at an installation the
 * operator reads the code - or the QR - off the kiosk's screen, sees which
 * machine it is, and binds it to one of their own kiosks.
 *
 * Nothing here is a mechanism of its own: the code is looked up and the device
 * activated through the endpoints the "Cihazlar" page uses, and the server
 * refuses a kiosk outside the account's scope. Making a kiosk that does not
 * exist yet stays on that page - it takes `kiosks.create` and a form that is
 * not for a phone.
 */
@Component({
  selector: 'app-operator-activate',
  imports: [
    FormsModule,
    RouterLink,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    TranslatePipe,
    ErrorBannerComponent,
  ],
  template: `
    <div class="op-page">
      <a mat-button class="neo-back" routerLink="/operator">
        <mat-icon>arrow_back</mat-icon>
        {{ 'operator.kiosk.back' | translate }}
      </a>

      <h1 class="op-title">{{ 'operator.activate.title' | translate }}</h1>

      @if (!device()) {
        <p class="neo-caption">
          {{ (canScan ? 'operator.activate.explanationScan' : 'operator.activate.explanation') | translate }}
        </p>

        @if (scanning()) {
          <div class="op-viewfinder">
            <video #video autoplay muted playsinline [attr.aria-label]="'operator.activate.scanning' | translate"></video>
          </div>
          <p class="neo-caption" role="status">{{ 'operator.activate.scanning' | translate }}</p>
          <button mat-stroked-button type="button" class="op-primary" (click)="stopScan()">
            {{ 'operator.activate.stopScan' | translate }}
          </button>
        } @else {
          <form class="op-form" (ngSubmit)="find()">
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>{{ 'operator.activate.code' | translate }}</mat-label>
              <input
                matInput
                name="code"
                autocomplete="off"
                autocapitalize="characters"
                spellcheck="false"
                maxlength="9"
                placeholder="XXXX-XXXX"
                [ngModel]="code()"
                (ngModelChange)="code.set($event)"
              />
              <mat-hint>{{ 'operator.activate.codeHint' | translate }}</mat-hint>
            </mat-form-field>

            @if (failed(); as message) {
              <p class="neo-field-error" role="alert">
                <mat-icon aria-hidden="true">error</mat-icon>
                {{ message | translate }}
              </p>
            }

            <button mat-flat-button type="submit" class="op-primary" [disabled]="!codeValid() || busy()">
              {{ 'operator.activate.find' | translate }}
            </button>
          </form>

          @if (canScan) {
            <button mat-stroked-button type="button" class="op-primary" [disabled]="busy()" (click)="scan()">
              {{ 'operator.activate.scan' | translate }}
            </button>
          }
        }
      } @else if (kiosksProblem(); as problem) {
        <app-error-banner [problem]="problem" effect="operator.loadFailedEffect" (retry)="loadKiosks()" />
      } @else {
        @let found = device()!;

        <article class="neo-card op-card" data-device>
          <div class="op-card__head">
            <span class="op-card__name">
              {{ found.fingerprint.hostname || ('operator.activate.unnamed' | translate) }}
              <span class="op-card__sub">
                {{ found.platform }}{{ found.appVersion ? ' · ' + found.appVersion : '' }}
              </span>
            </span>
          </div>

          <ul class="op-rows">
            <li class="op-row" [class.op-row--warning]="found.printers.length === 0">
              <mat-icon aria-hidden="true">{{ found.printers.length === 0 ? 'warning' : 'print' }}</mat-icon>
              <span>
                {{
                  found.printers.length === 0
                    ? ('operator.activate.noPrinters' | translate)
                    : ('operator.activate.printers' | translate: { count: found.printers.length })
                }}
              </span>
            </li>
          </ul>
        </article>

        @if (kiosks() === null) {
          <div class="neo-skeleton neo-skeleton--block"></div>
        } @else if (kiosks()!.length === 0) {
          <p class="op-row op-row--warning" role="alert">
            <mat-icon aria-hidden="true">warning</mat-icon>
            <span>{{ 'operator.activate.noKiosks' | translate }}</span>
          </p>
        } @else {
          <div class="op-form">
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>{{ 'operator.activate.kiosk' | translate }}</mat-label>
              <mat-select [ngModel]="kioskId()" (ngModelChange)="kioskId.set($event)">
                @for (kiosk of kiosks()!; track kiosk.id) {
                  <mat-option [value]="kiosk.id">{{ kiosk.code }} — {{ kiosk.name }}</mat-option>
                }
              </mat-select>
              <mat-hint>{{ 'operator.activate.kioskHint' | translate }}</mat-hint>
            </mat-form-field>

            <!-- The kiosk has a machine already: this one takes its place (plan 7.11, "cihaz dəyişikliyi"). -->
            @if (replaces()) {
              <p class="op-row op-row--warning" role="alert">
                <mat-icon aria-hidden="true">warning</mat-icon>
                <span>{{ 'operator.activate.replaces' | translate: { code: chosen()?.code } }}</span>
              </p>
            }

            @if (found.printers.length > 0) {
              <mat-form-field subscriptSizing="dynamic">
                <mat-label>{{ 'operator.activate.printer' | translate }}</mat-label>
                <mat-select [ngModel]="printer()" (ngModelChange)="printer.set($event)">
                  <mat-option [value]="null">{{ 'operator.activate.printerLater' | translate }}</mat-option>
                  @for (option of found.printers; track option.name) {
                    <mat-option [value]="option.name">{{ option.name }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
            }
          </div>

          <button
            mat-flat-button
            type="button"
            class="op-primary"
            [disabled]="!kioskId() || busy()"
            (click)="activate()"
          >
            {{ 'operator.activate.confirm' | translate }}
          </button>
        }

        <button mat-button type="button" class="op-primary" [disabled]="busy()" (click)="another()">
          {{ 'operator.activate.another' | translate }}
        </button>
      }

      @if (canCreate) {
        <p class="neo-caption">
          {{ 'operator.activate.newKiosk' | translate }}
          <a routerLink="/devices">{{ 'operator.activate.newKioskLink' | translate }}</a>
        </p>
      }
    </div>
  `,
  styleUrl: './operator.scss',
})
export class OperatorActivateComponent {
  private readonly devices = inject(AdminApiService);
  private readonly api = inject(OperatorApiService);
  private readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  private readonly scanner = new QrScanner();
  private readonly video = viewChild<ElementRef<HTMLVideoElement>>('video');

  /** Reading the QR needs a camera and a browser that can read codes; without them only the code is asked for. */
  readonly canScan = QrScanner.supported();

  /** A new kiosk is made on the "Cihazlar" page, by the accounts that may make one and see that page. */
  readonly canCreate = this.auth.can(Permissions.kiosksCreate) && this.auth.can(Permissions.devicesView);

  readonly code = signal('');
  readonly busy = signal(false);
  readonly scanning = signal(false);

  /** The translation key of why nothing was found. */
  readonly failed = signal<string | null>(null);

  readonly device = signal<DeviceDetails | null>(null);

  /** The account's kiosks; null until they arrive. */
  readonly kiosks = signal<OperatorKioskCard[] | null>(null);
  readonly kiosksProblem = signal<Problem | null>(null);
  readonly kioskId = signal<string | null>(null);
  readonly printer = signal<string | null>(null);

  /** Eight letters and digits, with or without the dash the screen shows. */
  readonly codeValid = computed(() => /^[A-Za-z0-9]{4}-?[A-Za-z0-9]{4}$/.test(this.code().trim()));

  readonly chosen = computed(() => this.kiosks()?.find((kiosk) => kiosk.id === this.kioskId()) ?? null);

  /** A kiosk that is anything but "not activated" has a machine. */
  readonly replaces = computed(() => {
    const chosen = this.chosen();

    return chosen !== null && chosen.status !== 'NotActivated';
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.scanner.stop());
  }

  /** The code typed in. */
  async find(): Promise<void> {
    if (this.codeValid()) {
      await this.lookUp(this.code().trim().toUpperCase());
    }
  }

  async scan(): Promise<void> {
    this.failed.set(null);
    this.scanning.set(true);

    // The viewfinder is in the page only once `scanning` is drawn.
    await new Promise<void>((done) => setTimeout(done));

    const video = this.video()?.nativeElement;

    if (!video) {
      this.scanning.set(false);

      return;
    }

    try {
      const read = await this.scanner.read(video, activationQr);

      this.scanning.set(false);

      if (read !== null) {
        await this.lookUp(read);
      }
    } catch {
      // No camera, or the permission was refused: the code can still be typed.
      this.scanning.set(false);
      this.failed.set('operator.activate.noCamera');
    }
  }

  stopScan(): void {
    this.scanner.stop();
    this.scanning.set(false);
  }

  async loadKiosks(): Promise<void> {
    this.kiosksProblem.set(null);
    this.kiosks.set(null);

    try {
      // Those waiting for a machine first: that is what an installation is for.
      const kiosks = [...(await firstValueFrom(this.api.kiosks()))].sort(
        (a, b) =>
          Number(a.status !== 'NotActivated') - Number(b.status !== 'NotActivated') || a.code.localeCompare(b.code),
      );
      const waiting = kiosks.filter((kiosk) => kiosk.status === 'NotActivated');

      this.kiosks.set(kiosks);
      // One kiosk waiting for a machine is the one meant; otherwise the operator says which.
      this.kioskId.set(waiting.length === 1 ? waiting[0].id : null);
    } catch (error) {
      this.kiosksProblem.set(toProblem(error));
    }
  }

  async activate(): Promise<void> {
    const device = this.device();
    const kioskId = this.kioskId();

    if (!device || !kioskId || this.busy()) {
      return;
    }

    this.busy.set(true);

    try {
      const result = await firstValueFrom(
        this.devices.activateDevice(device.deviceId, { kioskId, selectedPrinterName: this.printer() }),
      );

      this.notify.success('operator.activate.done');
      // The kiosk's own page: its printer, and "Test çap".
      await this.router.navigate(['/operator/kiosks', result.kioskId]);
    } catch (error) {
      this.notify.error(toProblem(error).message);
    } finally {
      this.busy.set(false);
    }
  }

  another(): void {
    this.device.set(null);
    this.code.set('');
    this.failed.set(null);
  }

  private async lookUp(codeOrQr: string): Promise<void> {
    this.busy.set(true);
    this.failed.set(null);

    try {
      const device = await firstValueFrom(this.devices.lookupDevice(codeOrQr));

      this.device.set(device);
      this.printer.set(device.printers.length === 1 ? device.printers[0].name : null);
      await this.loadKiosks();
    } catch {
      // The server says no more than "not found", whatever was wrong with the code.
      this.failed.set('operator.activate.notFound');
    } finally {
      this.busy.set(false);
    }
  }
}
