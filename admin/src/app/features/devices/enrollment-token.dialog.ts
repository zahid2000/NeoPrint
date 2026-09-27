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
import { describeError } from '../login/login.component';

/** How long a token lasts unless the operator shortens it. */
export const defaultTokenTtlHours = 24;

/**
 * Creating an enrollment token for zero-touch activation (plan section 7.11).
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

  readonly kiosks = signal<KioskListItem[]>([]);
  readonly created = signal<EnrollmentTokenResult | null>(null);
  readonly busy = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = this.formBuilder.nonNullable.group({
    mode: ['new' as 'new' | 'existing', [Validators.required]],
    kioskId: [''],
    ttlHours: [defaultTokenTtlHours, [Validators.required, Validators.min(1), Validators.max(720)]],
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

  async submit(): Promise<void> {
    if (this.form.invalid || this.busy() || this.created()) {
      return;
    }

    const form = this.form.getRawValue();

    if (form.mode === 'existing' && !form.kioskId) {
      this.errorMessage.set('activate.pickKiosk');
      return;
    }

    this.busy.set(true);
    this.errorMessage.set(null);

    try {
      const result = await firstValueFrom(
        this.api.createEnrollmentToken({
          kioskId: form.mode === 'existing' ? form.kioskId : null,
          // A token for a kiosk that does not exist yet carries no template in
          // step 1: the device registers and is then activated as a new kiosk
          // from the wizard, which is where the kiosk parameters are entered.
          newKiosk: null,
          ttlHours: form.ttlHours,
        }),
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
