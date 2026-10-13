import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import {
  AdminApiService,
  DeviceDetails,
  DeviceListItem,
  EnrollmentTokenListItem,
} from '../../core/api/admin-api.service';
import { Permissions } from '../../core/auth/auth.models';
import { HasPermissionDirective } from '../../core/auth/has-permission.directive';
import { liveRefresh } from '../../core/realtime/live-refresh';
import { describeError } from '../login/login.component';
import { ActivateDeviceDialogComponent } from './activate-device.dialog';
import { EnrollmentTokenDialogComponent } from './enrollment-token.dialog';
import { DateTimePipe } from '../../shared/format.pipes';
import { HubEvents } from '../../core/realtime/hub-events';

/**
 * The Devices page (plan sections 7.11, 8A.3).
 *
 * Four tabs: devices waiting to be activated, active devices, enrollment
 * tokens and revoked devices. Activation is the flow that turns a
 * just-installed universal build into a working kiosk.
 */
@Component({
  selector: 'app-devices',
  imports: [
    ReactiveFormsModule,
    DateTimePipe,
    MatCardModule,
    MatTabsModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatFormFieldModule,
    MatInputModule,
    MatCheckboxModule,
    MatDialogModule,
    MatProgressBarModule,
    MatTooltipModule,
    TranslatePipe,
    HasPermissionDirective,
  ],
  templateUrl: './devices.component.html',
  styleUrl: './devices.component.scss',
})
export class DevicesComponent {
  private readonly api = inject(AdminApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly translate = inject(TranslateService);
  private readonly formBuilder = inject(FormBuilder);

  readonly permissions = Permissions;

  readonly pending = signal<DeviceListItem[]>([]);
  readonly active = signal<DeviceListItem[]>([]);
  readonly revoked = signal<DeviceListItem[]>([]);
  readonly tokens = signal<EnrollmentTokenListItem[]>([]);

  readonly loading = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly deviceColumns = [
    'hostname',
    'platform',
    'version',
    'lastSeen',
    'printers',
    'actions',
  ] as const;

  readonly activeColumns = [
    'kiosk',
    'hostname',
    'platform',
    'version',
    'lastSeen',
    'actions',
  ] as const;

  readonly tokenColumns = ['target', 'createdAt', 'expiresAt', 'status', 'actions'] as const;

  /** The pairing code an administrator types in from the kiosk screen. */
  readonly lookupForm = this.formBuilder.nonNullable.group({
    code: ['', [Validators.required]],
  });

  readonly lookupBusy = signal(false);

  constructor() {
    void this.reload();

    // A kiosk that has just been switched on for the first time appears in
    // "waiting" by itself; one activated or revoked elsewhere moves (T-248).
    liveRefresh({
      events: [HubEvents.deviceRegistered, HubEvents.deviceActivated, HubEvents.deviceRevoked],
      reload: () => this.reload(true),
    });
  }

  /** Fetches the lists; quietly when nobody asked - see `liveRefresh`. */
  async reload(quiet = false): Promise<void> {
    if (!quiet) {
      this.loading.set(true);
      this.errorMessage.set(null);
    }

    try {
      const [pending, active, revoked] = await Promise.all([
        firstValueFrom(this.api.listDevices('Pending')),
        firstValueFrom(this.api.listDevices('Active')),
        firstValueFrom(this.api.listDevices('Revoked')),
      ]);

      this.pending.set(pending);
      this.active.set(active);
      this.revoked.set(revoked);

      await this.reloadTokens();
    } catch (error) {
      if (!quiet) {
        this.errorMessage.set(describeError(error));
      }
    } finally {
      this.loading.set(false);
    }
  }

  private async reloadTokens(): Promise<void> {
    try {
      this.tokens.set(await firstValueFrom(this.api.listEnrollmentTokens()));
    } catch {
      // Enrollment tokens need their own permission, which an operator who
      // can still see devices may not hold. An empty list is the honest
      // result rather than an error banner over the whole page.
      this.tokens.set([]);
    }
  }

  /** Finds a device from the code shown on the kiosk, then opens the wizard. */
  async lookup(): Promise<void> {
    if (this.lookupForm.invalid || this.lookupBusy()) {
      return;
    }

    this.lookupBusy.set(true);

    try {
      const details = await firstValueFrom(
        this.api.lookupDevice(this.lookupForm.getRawValue().code),
      );

      this.lookupForm.reset();
      await this.openActivation(details);
    } catch {
      this.notify('devices.lookupNotFound');
    } finally {
      this.lookupBusy.set(false);
    }
  }

  async activate(device: DeviceListItem): Promise<void> {
    try {
      // The wizard needs the fingerprint and the reported printers, which the
      // list row does not carry in full.
      const details = await firstValueFrom(
        this.api.lookupDevice(`neoprint://activate/${device.id}`),
      );

      await this.openActivation(details);
    } catch {
      this.notify('devices.lookupNotFound');
    }
  }

  private async openActivation(details: DeviceDetails): Promise<void> {
    const result = await firstValueFrom(
      this.dialog
        .open(ActivateDeviceDialogComponent, {
          data: details,
          width: '640px',
          disableClose: true,
        })
        .afterClosed(),
    );

    if (result) {
      this.snackBar.open(
        this.translate.instant('activate.success', {
          code: result.kioskCode,
        }) as string,
        undefined,
        { duration: 6000 },
      );

      await this.reload();
    }
  }

  async revoke(device: DeviceListItem): Promise<void> {
    // Plan section 8A.1: a destructive action is confirmed, and the reason is
    // recorded in the audit log.
    const reason = window.prompt(
      `${this.translate.instant('devices.revokeConfirm')}\n\n${this.translate.instant('devices.revokeReason')}:`,
    );

    if (reason === null) {
      return;
    }

    try {
      await firstValueFrom(this.api.revokeDevice(device.id, reason || null));
      await this.reload();
    } catch (error) {
      this.notify(describeError(error));
    }
  }

  async createToken(): Promise<void> {
    const created = await firstValueFrom(
      this.dialog
        .open(EnrollmentTokenDialogComponent, {
          width: '640px',
          disableClose: true,
        })
        .afterClosed(),
    );

    if (created) {
      await this.reloadTokens();
    }
  }

  async revokeToken(token: EnrollmentTokenListItem): Promise<void> {
    try {
      await firstValueFrom(this.api.revokeEnrollmentToken(token.id));
      await this.reloadTokens();
    } catch (error) {
      this.notify(describeError(error));
    }
  }

  tokenStatusKey(token: EnrollmentTokenListItem): string {
    if (token.usedByDeviceId) {
      return 'tokens.used';
    }

    if (token.revokedAt) {
      return 'tokens.revoked';
    }

    return token.isUsable ? 'tokens.usable' : 'tokens.expired';
  }

  private notify(messageKey: string): void {
    this.snackBar.open(this.translate.instant(messageKey) as string, undefined, { duration: 5000 });
  }
}
