import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';

import {
  AdminApiService,
  AdminUser,
  KioskListItem,
  RoleResponse,
} from '../../core/api/admin-api.service';
import { AuthService } from '../../core/auth/auth.service';
import { minimumAdminPasswordLength } from '../login/change-password.component';
import { describeError } from '../login/login.component';
import { DateTimePipe } from '../../shared/format.pipes';

/**
 * Administrator and operator accounts (plan sections 8A.2, 8A.3).
 *
 * Accounts are deactivated rather than deleted: an account that did things is
 * referenced by its audit entries, and a trail pointing at nothing is worse
 * than one pointing at a disabled account.
 */
@Component({
  selector: 'app-admin-users',
  imports: [
    ReactiveFormsModule,
    DateTimePipe,
    MatCardModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatCheckboxModule,
    MatProgressBarModule,
    MatTooltipModule,
    TranslatePipe,
  ],
  templateUrl: './admin-users.component.html',
  styleUrl: './admin-users.component.scss',
})
export class AdminUsersComponent {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AuthService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly snackBar = inject(MatSnackBar);
  private readonly translate = inject(TranslateService);

  readonly minimumPasswordLength = minimumAdminPasswordLength;

  readonly admins = signal<AdminUser[]>([]);
  readonly roles = signal<RoleResponse[]>([]);
  readonly kiosks = signal<KioskListItem[]>([]);

  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  /** The account being edited, or null while creating one. */
  readonly editingId = signal<string | null>(null);

  readonly showForm = signal(false);

  readonly columns = ['name', 'roles', 'scope', 'status', 'lastLogin', 'actions'] as const;

  readonly form = this.formBuilder.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    fullName: ['', [Validators.required, Validators.maxLength(120)]],
    password: ['', [Validators.required, Validators.minLength(minimumAdminPasswordLength)]],
    roleIds: [[] as string[], [Validators.required]],
    allKiosks: [true],
    kioskIds: [[] as string[]],
  });

  /** The signed-in account, which the page will not let itself disable. */
  readonly ownId = computed(() => this.auth.admin()?.id ?? null);

  readonly isEditing = computed(() => this.editingId() !== null);

  constructor() {
    void this.reload();
  }

  async reload(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);

    try {
      const [admins, roles] = await Promise.all([
        firstValueFrom(this.api.listAdmins()),
        firstValueFrom(this.api.listRoles()),
      ]);

      this.admins.set(admins);
      this.roles.set(roles);

      try {
        this.kiosks.set(await firstValueFrom(this.api.listKiosks()));
      } catch {
        // Managing accounts does not require the kiosk permission, so without
        // it the scope is shown by id rather than by code.
        this.kiosks.set([]);
      }
    } catch (error) {
      this.errorMessage.set(describeError(error));
    } finally {
      this.loading.set(false);
    }
  }

  startNew(): void {
    this.editingId.set(null);
    this.showForm.set(true);

    this.form.reset({
      email: '',
      fullName: '',
      password: '',
      roleIds: [],
      allKiosks: true,
      kioskIds: [],
    });

    this.form.controls.email.enable();
    this.form.controls.password.enable();
  }

  startEdit(admin: AdminUser): void {
    this.editingId.set(admin.id);
    this.showForm.set(true);

    this.form.reset({
      email: admin.email,
      fullName: admin.fullName,
      password: '',
      roleIds: [...admin.roleIds],
      allKiosks: admin.allKiosks,
      kioskIds: [...admin.kioskIds],
    });

    // An address identifies the account and a password is set by its holder,
    // so neither is edited here.
    this.form.controls.email.disable();
    this.form.controls.password.disable();
  }

  cancel(): void {
    this.showForm.set(false);
    this.editingId.set(null);
  }

  kioskLabel(kioskId: string): string {
    return this.kiosks().find((kiosk) => kiosk.id === kioskId)?.code ?? kioskId;
  }

  async save(): Promise<void> {
    if (this.form.invalid || this.saving()) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    this.errorMessage.set(null);

    const form = this.form.getRawValue();
    const editingId = this.editingId();

    try {
      if (editingId) {
        await firstValueFrom(
          this.api.updateAdmin(editingId, {
            fullName: form.fullName,
            roleIds: form.roleIds,
            allKiosks: form.allKiosks,
            kioskIds: form.allKiosks ? [] : form.kioskIds,
          }),
        );
      } else {
        await firstValueFrom(
          this.api.createAdmin({
            email: form.email,
            fullName: form.fullName,
            password: form.password,
            roleIds: form.roleIds,
            allKiosks: form.allKiosks,
            kioskIds: form.allKiosks ? [] : form.kioskIds,
          }),
        );
      }

      this.showForm.set(false);
      this.editingId.set(null);

      await this.reload();
      this.notify('admins.saved');
    } catch (error) {
      this.errorMessage.set(describeError(error));
    } finally {
      this.saving.set(false);
    }
  }

  async setActive(admin: AdminUser, isActive: boolean): Promise<void> {
    const reason = isActive
      ? null
      : window.prompt(this.translate.instant('admins.deactivateReason') as string);

    if (!isActive && reason === null) {
      return;
    }

    try {
      await firstValueFrom(this.api.setAdminActive(admin.id, isActive, reason || null));
      await this.reload();
    } catch (error) {
      // The server refuses to disable the last active SuperAdmin, and says so.
      this.errorMessage.set(describeError(error));
    }
  }

  async resetPassword(admin: AdminUser): Promise<void> {
    const password = window.prompt(
      this.translate.instant('admins.newPasswordPrompt', {
        min: minimumAdminPasswordLength,
      }) as string,
    );

    if (!password) {
      return;
    }

    try {
      await firstValueFrom(this.api.resetAdminPassword(admin.id, password));
      await this.reload();

      // Every session of that account has just been revoked, and they have to
      // change the password at their next sign-in.
      this.notify('admins.passwordReset');
    } catch (error) {
      this.errorMessage.set(describeError(error));
    }
  }

  private notify(messageKey: string): void {
    this.snackBar.open(this.translate.instant(messageKey) as string, undefined, {
      duration: 4000,
    });
  }
}
