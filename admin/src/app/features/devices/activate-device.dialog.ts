import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { MatStepperModule } from '@angular/material/stepper';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import {
  ActivateDeviceResult,
  AdminApiService,
  DeviceDetails,
  KioskListItem,
} from '../../core/api/admin-api.service';
import { describeError } from '../login/login.component';
import { DateTimePipe } from '../../shared/format.pipes';

/**
 * The kiosk admin PIN length the panel asks for.
 *
 * The server's floor is four digits; the panel asks for six, because a kiosk
 * PIN is typed on a screen in public (plan section 7.11).
 */
export const adminPinLength = 6;

/**
 * The activation wizard (plan section 7.11).
 *
 * Three steps: confirm this is the right machine, decide whether it becomes a
 * new kiosk or takes over an existing one, then pick the printer. Nothing is
 * sent until the last step, so a wrong code costs nothing.
 */
@Component({
  selector: 'app-activate-device',
  imports: [
    ReactiveFormsModule,
    DateTimePipe,
    MatDialogModule,
    MatStepperModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatRadioModule,
    MatProgressSpinnerModule,
    TranslatePipe,
  ],
  templateUrl: './activate-device.dialog.html',
  styleUrl: './activate-device.dialog.scss',
})
export class ActivateDeviceDialogComponent {
  private readonly api = inject(AdminApiService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly dialogRef =
    inject<MatDialogRef<ActivateDeviceDialogComponent, ActivateDeviceResult>>(MatDialogRef);

  readonly device = inject<DeviceDetails>(MAT_DIALOG_DATA);

  readonly pinLength = adminPinLength;
  readonly languages = ['az', 'en', 'ru'] as const;

  readonly kiosks = signal<KioskListItem[]>([]);
  readonly busy = signal(false);
  readonly errorMessage = signal<string | null>(null);

  /** New kiosk, or an existing one that lost its machine. */
  readonly modeForm = this.formBuilder.nonNullable.group({
    mode: ['new' as 'new' | 'existing', [Validators.required]],
    kioskId: [''],
  });

  readonly newKioskForm = this.formBuilder.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(120)]],
    // Left blank the server allocates the next KSK-nnnn, which is what an
    // operator should normally let it do.
    code: [''],
    address: ['', [Validators.required]],
    lat: [40.4093, [Validators.required, Validators.min(-90), Validators.max(90)]],
    lng: [49.8671, [Validators.required, Validators.min(-180), Validators.max(180)]],
    defaultLanguage: ['az', [Validators.required]],
    paperCapacityA4: [500, [Validators.required, Validators.min(1)]],
    paperCapacityA3: [250, [Validators.required, Validators.min(0)]],
    adminPin: [
      '',
      [
        Validators.required,
        Validators.minLength(adminPinLength),
        Validators.maxLength(12),
        Validators.pattern(/^\d+$/),
      ],
    ],
    fiscalTerminalId: [''],
    maxFileSizeMb: [null as number | null],
    maxPages: [null as number | null],
  });

  readonly printerForm = this.formBuilder.nonNullable.group({
    // Optional: a kiosk with no printer yet activates as NotConfigured, and
    // the printer is selected later from the kiosk page.
    selectedPrinterName: [''],
  });

  /** Existing kiosks that no live device is bound to. */
  readonly bindableKiosks = computed(() =>
    this.kiosks().filter((kiosk) => kiosk.activeDeviceId === null),
  );

  readonly onlinePrinters = computed(() =>
    this.device.printers.filter((printer) => printer.isOnline),
  );

  constructor() {
    void this.loadKiosks();
  }

  private async loadKiosks(): Promise<void> {
    try {
      this.kiosks.set(await firstValueFrom(this.api.listKiosks()));
    } catch {
      // Without the kiosk list only the "new kiosk" branch is offered, which
      // is still a complete activation.
      this.kiosks.set([]);
    }
  }

  get isNewKiosk(): boolean {
    return this.modeForm.getRawValue().mode === 'new';
  }

  /** Whether the wizard has everything it needs to submit. */
  readonly canSubmit = computed(() => !this.busy());

  cancel(): void {
    this.dialogRef.close();
  }

  async submit(): Promise<void> {
    if (this.busy()) {
      return;
    }

    const mode = this.modeForm.getRawValue();

    if (mode.mode === 'existing' && !mode.kioskId) {
      this.errorMessage.set('activate.pickKiosk');
      return;
    }

    if (mode.mode === 'new' && this.newKioskForm.invalid) {
      this.newKioskForm.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.errorMessage.set(null);

    const printer = this.printerForm.getRawValue().selectedPrinterName;

    try {
      const result = await firstValueFrom(
        this.api.activateDevice(this.device.deviceId, {
          kioskId: mode.mode === 'existing' ? mode.kioskId : null,
          newKiosk: mode.mode === 'new' ? this.buildNewKiosk() : null,
          selectedPrinterName: printer || null,
        }),
      );

      this.dialogRef.close(result);
    } catch (error) {
      this.errorMessage.set(describeError(error));
    } finally {
      this.busy.set(false);
    }
  }

  private buildNewKiosk() {
    const form = this.newKioskForm.getRawValue();

    return {
      name: form.name.trim(),
      code: form.code.trim() || null,
      lat: form.lat,
      lng: form.lng,
      address: form.address.trim(),
      pricingProfileId: null,
      maxFileSizeMb: form.maxFileSizeMb,
      maxPages: form.maxPages,
      queueDisplayLimit: null,
      defaultLanguage: form.defaultLanguage,
      fiscalTerminalId: form.fiscalTerminalId.trim() || null,
      paperCapacityA4: form.paperCapacityA4,
      paperCapacityA3: form.paperCapacityA3,
      adminPin: form.adminPin,
    };
  }
}
