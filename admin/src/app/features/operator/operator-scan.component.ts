import {
  Component,
  DestroyRef,
  ElementRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';

import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/auth.models';
import { QrScanner, activationQr, kioskLoginQr } from './qr-scanner';

/** What a kiosk's QR was read for: handed to the page that does it. */
export interface ScannedKioskQr {
  readonly code: string;
}

/**
 * "Kioskun QR-ını oxut" (T-425): one button for both of a kiosk's QR codes.
 * The activation QR of a new machine opens "Kiosku aktivləşdir" with it, the
 * admin-corner QR opens "Kioska daxil ol" - the code says which, the
 * operator does not have to. An activation QR is read only by an account
 * that may activate kiosks.
 */
@Component({
  selector: 'app-operator-scan',
  imports: [RouterLink, MatButtonModule, MatIconModule, TranslatePipe],
  template: `
    <div class="op-page">
      <a mat-button class="neo-back" routerLink="/operator">
        <mat-icon>arrow_back</mat-icon>
        {{ 'operator.kiosk.back' | translate }}
      </a>

      <h1 class="op-title">{{ 'operator.scan.title' | translate }}</h1>
      <p class="neo-caption">
        {{
          (canActivate() ? 'operator.scan.explanation' : 'operator.scan.explanationLogin')
            | translate
        }}
      </p>

      @if (failed(); as message) {
        <p class="neo-field-error" role="alert">
          <mat-icon aria-hidden="true">error</mat-icon>
          {{ message | translate }}
        </p>
      }

      @if (canScan) {
        @if (scanning()) {
          <div class="op-viewfinder">
            <video
              #video
              autoplay
              muted
              playsinline
              [attr.aria-label]="'operator.activate.scanning' | translate"
            ></video>
          </div>
          <p class="neo-caption" role="status">{{ 'operator.activate.scanning' | translate }}</p>
          <button mat-stroked-button type="button" class="op-primary" (click)="stop()">
            {{ 'operator.activate.stopScan' | translate }}
          </button>
        } @else {
          <button mat-flat-button type="button" class="op-primary" (click)="scan()">
            <mat-icon>qr_code_scanner</mat-icon>
            {{ 'operator.activate.scan' | translate }}
          </button>
        }
      }

      <!-- No camera reading here, or none at hand: the code typed in instead. -->
      <p class="neo-caption">{{ 'operator.scan.typeInstead' | translate }}</p>
      <div class="op-links">
        <a mat-button routerLink="/operator/kiosk-login" data-action="type-login">
          <mat-icon>login</mat-icon>
          {{ 'operator.kiosks.kioskLogin' | translate }}
        </a>
        @if (canActivate()) {
          <a mat-button routerLink="/operator/activate" data-action="type-activate">
            <mat-icon>devices</mat-icon>
            {{ 'operator.kiosks.activate' | translate }}
          </a>
        }
      </div>
    </div>
  `,
  styleUrl: './operator.scss',
})
export class OperatorScanComponent {
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  private readonly scanner = new QrScanner();
  private readonly video = viewChild<ElementRef<HTMLVideoElement>>('video');

  readonly canScan = QrScanner.supported();
  readonly canActivate = computed(() => this.auth.can(Permissions.kiosksActivate));

  readonly scanning = signal(false);
  readonly failed = signal<string | null>(null);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.scanner.stop());
  }

  /** Which page a code read belongs to; null for any other code. */
  routeFor(text: string): string | null {
    if (kioskLoginQr(text)) {
      return '/operator/kiosk-login';
    }
    if (activationQr(text) && this.canActivate()) {
      return '/operator/activate';
    }
    return null;
  }

  async scan(): Promise<void> {
    this.failed.set(null);
    this.scanning.set(true);

    // The viewfinder is in the page only once `scanning` is drawn.
    await new Promise<void>((resolve) => setTimeout(resolve));

    const video = this.video()?.nativeElement;
    if (!video) {
      this.scanning.set(false);
      return;
    }

    try {
      const read = await this.scanner.read(video, (text) => this.routeFor(text) !== null);
      this.scanning.set(false);
      if (read !== null) {
        await this.open(read);
      }
    } catch {
      this.scanning.set(false);
      this.failed.set('operator.activate.noCamera');
    }
  }

  stop(): void {
    this.scanner.stop();
    this.scanning.set(false);
  }

  /** The page the code is for, with the code: it goes on from there by itself. */
  async open(code: string): Promise<void> {
    const route = this.routeFor(code);
    if (route) {
      const state: ScannedKioskQr = { code };
      await this.router.navigateByUrl(route, { state });
    }
  }
}

/** The code a page was opened with from {@link OperatorScanComponent}, if any. */
export function scannedCode(router: Router): string | null {
  const state = router.getCurrentNavigation()?.extras.state as Partial<ScannedKioskQr> | undefined;
  return typeof state?.code === 'string' ? state.code : null;
}
