import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import {
  AdminApiService,
  EnrollmentTokenResult,
  KioskListItem,
} from '../../core/api/admin-api.service';
import { DateTimePipe } from '../../shared/format.pipes';
import { describeError } from '../login/login.component';
import { adminPinLength } from './activate-device.dialog';

/** How long a token lasts unless the operator chooses otherwise. */
export const defaultTokenTtlHours = 24;

/** The lifetimes offered (docs/DESIGN_SYSTEM.md, Cihazlar): an hour to thirty days. */
export const tokenLifetimes = [1, 24, 72, 168, 720] as const;

/** The most new kiosks one token may make: a golden image's clones (T-327, EnrollmentToken.MaxUsesLimit). */
export const maxTokenUses = 50;

/**
 * Creating an enrollment token for zero-touch activation (plan section 7.11,
 * T-327): once for a kiosk that exists, or for a number of new kiosks made
 * from the parameters written here - the clones of one golden image, each
 * with a code of its own.
 *
 * The raw token comes back exactly once - the server keeps only its hash - so
 * once it is shown the dialog switches to a copy-only view and does not let it
 * be re-created by accident.
 */
@Component({
  selector: 'app-enrollment-token',
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatRadioModule,
    MatTabsModule,
    MatProgressSpinnerModule,
    TranslatePipe,
    DateTimePipe,
  ],
  templateUrl: './enrollment-token.dialog.html',
  styleUrl: './activate-device.dialog.scss',
})
export class EnrollmentTokenDialogComponent {
  private readonly api = inject(AdminApiService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly snackBar = inject(MatSnackBar);
  private readonly translate = inject(TranslateService);
  private readonly dialogRef =
    inject<MatDialogRef<EnrollmentTokenDialogComponent, EnrollmentTokenResult>>(MatDialogRef);

  readonly lifetimes = tokenLifetimes;
  readonly maxUses = maxTokenUses;
  readonly pinLength = adminPinLength;
  readonly languages = ['az', 'en', 'ru'] as const;

  readonly kiosks = signal<KioskListItem[]>([]);
  readonly created = signal<EnrollmentTokenResult | null>(null);
  readonly busy = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = this.formBuilder.nonNullable.group({
    mode: ['new' as 'new' | 'existing', [Validators.required]],
    kioskId: [''],
    ttlHours: [defaultTokenTtlHours as number, [Validators.required]],
  });

  /** What each new kiosk is made with: the activation wizard's fields, less the code - each gets its own. */
  readonly newKioskForm = this.formBuilder.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(120)]],
    address: ['', [Validators.required]],
    lat: [40.4093, [Validators.required, Validators.min(-90), Validators.max(90)]],
    lng: [49.8671, [Validators.required, Validators.min(-180), Validators.max(180)]],
    defaultLanguage: ['az', [Validators.required]],
    paperCapacityA4: [500, [Validators.required, Validators.min(1)]],
    paperCapacityA3: [250, [Validators.required, Validators.min(0)]],
    adminPin: [
      '',
      [Validators.required, Validators.minLength(adminPinLength), Validators.maxLength(12), Validators.pattern(/^\d+$/)],
    ],
    uses: [1, [Validators.required, Validators.min(1), Validators.max(maxTokenUses)]],
  });

  readonly bindableKiosks = computed(() =>
    this.kiosks().filter((kiosk) => kiosk.activeDeviceId === null),
  );

  constructor() {
    void this.loadKiosks();
  }

  private async loadKiosks(): Promise<void> {
    try {
      this.kiosks.set(await firstValueFrom(this.api.listKiosks()));
    } catch {
      this.kiosks.set([]);
    }
  }

  get isNewKiosk(): boolean {
    return this.form.getRawValue().mode === 'new';
  }

  get invalid(): boolean {
    return this.form.invalid || (this.isNewKiosk ? this.newKioskForm.invalid : !this.form.getRawValue().kioskId);
  }

  async submit(): Promise<void> {
    if (this.busy() || this.created()) {
      return;
    }

    const form = this.form.getRawValue();

    if (form.mode === 'existing' && !form.kioskId) {
      this.errorMessage.set('activate.pickKiosk');
      return;
    }

    if (form.mode === 'new' && this.newKioskForm.invalid) {
      this.newKioskForm.markAllAsTouched();
      return;
    }

    const kiosk = this.newKioskForm.getRawValue();

    this.busy.set(true);
    this.errorMessage.set(null);

    try {
      const result = await firstValueFrom(
        this.api.createEnrollmentToken(
          form.mode === 'existing'
            ? { kioskId: form.kioskId, newKiosk: null, ttlHours: form.ttlHours, maxUses: 1 }
            : {
                kioskId: null,
                newKiosk: {
                  name: kiosk.name.trim(),
                  // Each kiosk the token makes gets the next KSK-nnnn.
                  code: null,
                  lat: kiosk.lat,
                  lng: kiosk.lng,
                  address: kiosk.address.trim(),
                  pricingProfileId: null,
                  maxFileSizeMb: null,
                  maxPages: null,
                  queueDisplayLimit: null,
                  defaultLanguage: kiosk.defaultLanguage,
                  fiscalTerminalId: null,
                  paperCapacityA4: kiosk.paperCapacityA4,
                  paperCapacityA3: kiosk.paperCapacityA3,
                  adminPin: kiosk.adminPin,
                },
                ttlHours: form.ttlHours,
                maxUses: kiosk.uses,
              },
        ),
      );

      this.created.set(result);
    } catch (error) {
      this.errorMessage.set(describeError(error));
    } finally {
      this.busy.set(false);
    }
  }

  async copy(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      this.notify('common.copied');
    } catch {
      // Clipboard access needs a secure context and permission; on a refusal
      // the operator can still select the text by hand.
      this.notify('common.copyFailed');
    }
  }

  close(): void {
    this.dialogRef.close(this.created() ?? undefined);
  }

  private notify(messageKey: string): void {
    this.snackBar.open(this.translate.instant(messageKey) as string, undefined, {
      duration: 3000,
    });
  }
}
