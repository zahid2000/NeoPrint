import { Component, DestroyRef, ElementRef, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import { toProblem } from '../../shared/problem';
import { OperatorApiService, OperatorKioskLoginApproved } from './operator-api.service';
import { QrScanner, kioskLoginQr } from './qr-scanner';
import { scannedCode } from './operator-scan.component';

/**
 * "Kioska daxil ol" (T-392; docs/DECISIONS.md 2026-11-10): the kiosk's admin
 * corner shows a one-off QR, the operator reads it here, and the kiosk's menu
 * opens in this account's name - every change made there is audited as theirs.
 */
@Component({
  selector: 'app-operator-kiosk-login',
  imports: [
    FormsModule,
    RouterLink,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    TranslatePipe,
  ],
  template: `
    <div class="op-page">
      <a mat-button class="neo-back" routerLink="/operator">
        <mat-icon>arrow_back</mat-icon>
        {{ 'operator.kiosk.back' | translate }}
      </a>

      <h1 class="op-title">{{ 'operator.kioskLogin.title' | translate }}</h1>

      @if (done(); as kiosk) {
        <article class="neo-card op-card" role="status">
          <p class="op-row op-row--success">
            <mat-icon aria-hidden="true">check_circle</mat-icon>
            <span>{{
              'operator.kioskLogin.done'
                | translate: { code: kiosk.kioskCode, name: kiosk.kioskName }
            }}</span>
          </p>
          <p class="neo-caption">{{ 'operator.kioskLogin.doneHint' | translate }}</p>
        </article>
        <button mat-button type="button" class="op-primary" (click)="again()">
          {{ 'operator.kioskLogin.another' | translate }}
        </button>
      } @else {
        <p class="neo-caption">{{ 'operator.kioskLogin.explanation' | translate }}</p>

        @if (failed(); as message) {
          <p class="neo-field-error" role="alert">
            <mat-icon aria-hidden="true">error</mat-icon>
            {{ message | translate }}
          </p>
        }

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
          <button mat-stroked-button type="button" class="op-primary" (click)="stopScan()">
            {{ 'operator.activate.stopScan' | translate }}
          </button>
        } @else if (canScan) {
          <button
            mat-flat-button
            type="button"
            class="op-primary"
            [disabled]="busy()"
            (click)="scan()"
          >
            <mat-icon>qr_code_scanner</mat-icon>
            {{ 'operator.activate.scan' | translate }}
          </button>
        } @else {
          <!-- No camera reading here: the QR's text, as another app read it. -->
          <form class="op-form" (ngSubmit)="approve(typed())">
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>{{ 'operator.kioskLogin.code' | translate }}</mat-label>
              <input
                matInput
                name="code"
                autocomplete="off"
                spellcheck="false"
                [ngModel]="typed()"
                (ngModelChange)="typed.set($event)"
              />
              <mat-hint>{{ 'operator.kioskLogin.codeHint' | translate }}</mat-hint>
            </mat-form-field>
            <button
              mat-flat-button
              type="submit"
              class="op-primary"
              [disabled]="!typed().trim() || busy()"
            >
              {{ 'operator.kioskLogin.confirm' | translate }}
            </button>
          </form>
        }
      }
    </div>
  `,
  styleUrl: './operator.scss',
})
export class OperatorKioskLoginComponent {
  private readonly api = inject(OperatorApiService);
  private readonly scanner = new QrScanner();
  private readonly video = viewChild<ElementRef<HTMLVideoElement>>('video');

  readonly canScan = QrScanner.supported();

  readonly typed = signal('');
  readonly busy = signal(false);
  readonly scanning = signal(false);
  readonly failed = signal<string | null>(null);
  readonly done = signal<OperatorKioskLoginApproved | null>(null);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.scanner.stop());

    // Read on "Kioskun QR-ını oxut" (T-425): approved at once.
    const scanned = scannedCode(inject(Router));
    if (scanned) {
      void this.approve(scanned);
    }
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
      const read = await this.scanner.read(video, kioskLoginQr);
      this.scanning.set(false);
      if (read !== null) {
        await this.approve(read);
      }
    } catch {
      this.scanning.set(false);
      this.failed.set('operator.activate.noCamera');
    }
  }

  stopScan(): void {
    this.scanner.stop();
    this.scanning.set(false);
  }

  async approve(code: string): Promise<void> {
    if (!code.trim() || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.failed.set(null);
    try {
      this.done.set(await firstValueFrom(this.api.approveKioskLogin(code.trim())));
    } catch (error) {
      this.failed.set(toProblem(error).message);
    } finally {
      this.busy.set(false);
    }
  }

  again(): void {
    this.done.set(null);
    this.typed.set('');
  }
}
